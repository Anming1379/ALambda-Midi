using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MidiPlayer.Services;

/// <summary>编辑器里的一个音符（时间单位：tick）</summary>
public class EditorNote
{
    public int  NoteNumber { get; set; }
    public long StartTicks { get; set; }
    public long LengthTicks { get; set; }
    public int  Velocity   { get; set; } = 100;
    public int  Channel    { get; set; }
    public bool IsSelected { get; set; }

    public long EndTicks => StartTicks + LengthTicks;

    public EditorNote Clone() => (EditorNote)MemberwiseClone();
}

/// <summary>延音踏板事件（CC64），0 = 断开，127 = 踩下</summary>
public class PedalEvent
{
    public long Tick  { get; set; }
    public int  Value { get; set; }

    public PedalEvent Clone() => (PedalEvent)MemberwiseClone();
}

/// <summary>
/// MIDI 编辑器文档：音符列表 + 基础文件 + 撤销/重做 + 保存。
/// 音符统一用 tick 绝对时间，播放/保存时重新生成 MIDI 文件。
/// </summary>
public class MidiDocument
{
    public const int DefaultPpq = 480;
    private const int MaxUndo = 100;

    private readonly List<DocSnapshot> _undo = new();
    private readonly List<DocSnapshot> _redo = new();

    private MidiFile _baseFile = null!;

    public string? SourcePath { get; private set; }
    public string  Title      { get; set; } = "未命名";
    public List<EditorNote> Notes { get; } = new();
    public List<PedalEvent> Pedal { get; } = new();

    public int Ppq         { get; private set; } = DefaultPpq;
    public int Bpm         { get; set; } = 120;
    public int Numerator   { get; set; } = 4;
    public int Denominator { get; set; } = 4;

    public bool IsDirty { get; private set; }
    public string? SavePath   { get; set; }
    public string? SaveName   { get; set; }
    public string? SaveFolder { get; set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private MidiDocument() { }

    // ---------------- 创建 / 读取 ----------------

    public static MidiDocument CreateNew()
    {
        var track = new TrackChunk();
        var file = new MidiFile(track);
        file.TimeDivision = new TicksPerQuarterNoteTimeDivision((short)DefaultPpq);

        return new MidiDocument
        {
            _baseFile = file,
            Ppq = DefaultPpq,
            Bpm = 120,
            Numerator = 4,
            Denominator = 4,
            Title = "未命名"
        };
    }

    public static MidiDocument FromFile(string path)
    {
        var file = MidiFile.Read(path);
        int ppq = DefaultPpq;
        if (file.TimeDivision is TicksPerQuarterNoteTimeDivision tpq)
            ppq = tpq.TicksPerQuarterNote;

        var doc = new MidiDocument
        {
            _baseFile = file,
            Ppq = ppq,
            SourcePath = path,
            Title = Path.GetFileNameWithoutExtension(path)
        };

        var tempoMap = file.GetTempoMap();
        var initialTempo = tempoMap.GetTempoAtTime(new MidiTimeSpan(0));
        doc.Bpm = Math.Clamp(
            (int)Math.Round(60_000_000.0 / initialTempo.MicrosecondsPerQuarterNote), 20, 400);

        var initialSig = tempoMap.GetTimeSignatureAtTime(new MidiTimeSpan(0));
        doc.Numerator = initialSig.Numerator;
        doc.Denominator = initialSig.Denominator;

        foreach (var timedEvent in file.GetTimedEvents())
        {
            if (timedEvent.Event is ControlChangeEvent cc && (int)cc.ControlNumber == 64)
            {
                doc.Pedal.Add(new PedalEvent
                {
                    Tick = Math.Max(0, timedEvent.Time),
                    Value = Math.Clamp((int)cc.ControlValue, 0, 127)
                });
            }
        }
        doc.NormalizePedal();

        foreach (var note in file.GetNotes())
        {
            doc.Notes.Add(new EditorNote
            {
                NoteNumber = Math.Clamp((int)note.NoteNumber, 0, 127),
                StartTicks = Math.Max(0, note.Time),
                LengthTicks = Math.Max(1, note.Length),
                Velocity = Math.Clamp((int)note.Velocity, 1, 127),
                Channel = Math.Clamp((int)note.Channel, 0, 15)
            });
        }

        doc.Notes.Sort((a, b) => a.StartTicks.CompareTo(b.StartTicks));
        return doc;
    }

    // ---------------- 网格辅助 ----------------

    public long QuarterTicks => Math.Max(1, Ppq);

    /// <summary>一拍对应的 tick（6/8 拍时一拍是八分音符）</summary>
    public long BeatTicks => Math.Max(1, Ppq * 4L / Math.Max(1, Denominator));

    public long BarTicks => Math.Max(1, BeatTicks * Math.Max(1, Numerator));

    /// <summary>默认吸附：十六分音符</summary>
    public long SnapTicks => Math.Max(1, BeatTicks / 4);

    public long LastNoteEnd => Notes.Count == 0 ? 0 : Notes.Max(n => n.EndTicks);

    public long ContentEndTicks => Math.Max(BarTicks * 8, LastNoteEnd + BarTicks);

    public double TicksToSeconds(long ticks) => ticks * 60.0 / (Math.Max(1, Bpm) * (double)Math.Max(1, Ppq));

    public static string FormatSeconds(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int m = (int)(seconds / 60);
        double s = seconds - m * 60;
        return $"{m:D2}:{s:00.0}";
    }

    // ---------------- 撤销 / 重做 ----------------

    private sealed class DocSnapshot
    {
        public List<EditorNote> Notes = new();
        public List<PedalEvent> Pedal = new();
    }

    public void PushUndo()
    {
        _undo.Add(Snapshot());
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private DocSnapshot Snapshot() => new()
    {
        Notes = Notes.Select(n => n.Clone()).ToList(),
        Pedal = Pedal.Select(p => p.Clone()).ToList()
    };

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Snapshot());
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(snapshot);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Snapshot());
        var snapshot = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(snapshot);
    }

    private void Restore(DocSnapshot snapshot)
    {
        Notes.Clear();
        foreach (var n in snapshot.Notes) Notes.Add(n.Clone());
        Pedal.Clear();
        foreach (var p in snapshot.Pedal) Pedal.Add(p.Clone());
        SortPedal();
        IsDirty = true;
    }

    public void MarkDirty() => IsDirty = true;

    public void MarkSaved(string path, string name, string folder)
    {
        SavePath = path;
        SaveName = name;
        SaveFolder = folder;
        IsDirty = false;
    }

    // ---------------- 生成 MIDI 文件 ----------------

    /// <summary>
    /// 生成一个可以播放/保存的 MidiFile：
    /// 保留原文件里的速度以外的所有非音符事件（踏板、控制器、音色等），
    /// 把音符替换成编辑器里的内容，并把 BPM / 拍号写成文件开头的统一设置。
    /// </summary>
    public MidiFile BuildMidiFile()
    {
        // 深拷贝基础文件（通过序列化再读回）
        MidiFile file;
        using (var ms = new MemoryStream())
        {
            _baseFile.Write(ms);
            ms.Position = 0;
            file = MidiFile.Read(ms);
        }

        // 去掉原音符，避免和编辑结果重复
        file.RemoveNotes();

        // 去掉原来的 BPM / 拍号，改成编辑器里的单一设置
        foreach (var chunk in file.GetTrackChunks())
        {
            chunk.RemoveTimedEvents(e =>
                e.Event is SetTempoEvent || e.Event is TimeSignatureEvent ||
                (e.Event is ControlChangeEvent cc && (int)cc.ControlNumber == 64));
        }

        // 写入 BPM / 拍号
        var target = file.GetTrackChunks().FirstOrDefault();
        if (target == null)
        {
            target = new TrackChunk();
            file.Chunks.Add(target);
        }

        using (var manager = target.ManageTimedEvents())
        {
            long microseconds = Math.Max(1, (long)Math.Round(60_000_000.0 / Math.Max(20, Bpm)));
            manager.Objects.Add(new TimedEvent(new SetTempoEvent(microseconds), 0));
            manager.Objects.Add(new TimedEvent(
                new TimeSignatureEvent((byte)Math.Clamp(Numerator, 1, 32),
                                       (byte)Math.Clamp(Denominator, 1, 32)), 0));
            manager.SaveChanges();
        }

        // 追加音符轨
        var noteTrack = new TrackChunk();
        var events = new List<(long Tick, int Order, MidiEvent Event)>();

        // 踏板：编辑区是全局的，所以写入所有用到的通道，避免只对通道 1 生效
        var pedalChannels = Notes.Select(n => Math.Clamp(n.Channel, 0, 15))
                                 .Distinct()
                                 .ToList();
        if (pedalChannels.Count == 0) pedalChannels.Add(0);

        foreach (var pedal in Pedal)
        {
            int value = Math.Clamp(pedal.Value, 0, 127);
            foreach (int channel in pedalChannels)
            {
                events.Add((Math.Max(0, pedal.Tick), value >= 64 ? 2 : 0,
                    new ControlChangeEvent((SevenBitNumber)64, (SevenBitNumber)value)
                    {
                        Channel = (FourBitNumber)channel
                    }));
            }
        }

        foreach (var note in Notes)
        {
            int number = Math.Clamp(note.NoteNumber, 0, 127);
            int channel = Math.Clamp(note.Channel, 0, 15);
            long start = Math.Max(0, note.StartTicks);
            long length = Math.Max(1, note.LengthTicks);

            events.Add((start, 1, new NoteOnEvent(
                (SevenBitNumber)number, (SevenBitNumber)Math.Clamp(note.Velocity, 1, 127))
            {
                Channel = (FourBitNumber)channel
            }));
            events.Add((start + length, 0, new NoteOffEvent(
                (SevenBitNumber)number, (SevenBitNumber)0)
            {
                Channel = (FourBitNumber)channel
            }));
        }

        events.Sort((a, b) =>
        {
            int c = a.Tick.CompareTo(b.Tick);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });

        long lastTick = 0;
        foreach (var (tick, _, ev) in events)
        {
            ev.DeltaTime = tick - lastTick;
            lastTick = tick;
            noteTrack.Events.Add(ev);
        }
        // EndOfTrackEvent 由 DryWetMidi 在写入时自动补齐
        file.Chunks.Add(noteTrack);

        return file;
    }

    // ---------------- 踏板 ----------------

    public void SortPedal() =>
        Pedal.Sort((a, b) => a.Tick.CompareTo(b.Tick));

    /// <summary>
    /// 同一 tick 上的重复踏板事件合并成一个（多通道保存后重新读取时会遇到），
    /// 相邻且值完全相同的踏板事件也会被合并，避免编辑后产生一堆冗余边界。
    /// </summary>
    public void NormalizePedal()
    {
        if (Pedal.Count == 0) return;

        SortPedal();
        var merged = new List<PedalEvent>(Pedal.Count);
        foreach (var pedal in Pedal)
        {
            if (merged.Count > 0 && merged[^1].Tick == pedal.Tick)
            {
                merged[^1].Value = pedal.Value;
                continue;
            }

            if (merged.Count > 0 && merged[^1].Value == pedal.Value)
                continue;

            merged.Add(pedal);
        }

        if (merged.Count == Pedal.Count) return;
        Pedal.Clear();
        Pedal.AddRange(merged);
    }

    public int GetPedalValue(long tick)
    {
        int value = 0;
        foreach (var pedal in Pedal)
        {
            if (pedal.Tick > tick) break;
            value = pedal.Value;
        }
        return value;
    }

    public PedalEvent AddPedalPoint(long tick, int value)
    {
        tick = Math.Max(0, tick);
        value = value >= 64 ? 127 : 0;

        var existing = Pedal.FirstOrDefault(p => p.Tick == tick);
        if (existing != null)
        {
            existing.Value = value;
            return existing;
        }

        var point = new PedalEvent { Tick = tick, Value = value };
        Pedal.Add(point);
        SortPedal();
        return point;
    }

    public void RemovePedalPoint(PedalEvent point) => Pedal.Remove(point);

    public void SaveTo(string path)
    {
        var file = BuildMidiFile();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        file.Write(path, overwriteFile: true);
    }
}
