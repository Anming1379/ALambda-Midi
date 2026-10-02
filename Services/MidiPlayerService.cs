using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Common;

namespace MidiPlayer.Services;

public record NoteInfo(int NoteNumber, int Velocity, TimeSpan Start, TimeSpan Duration);
public record BeatLine(TimeSpan Time, bool IsMeasureStart);

public enum PlayerStatus { Stopped, Playing, Paused }

public class MidiPlayerService : IDisposable
{
    private MidiFile? _originalFile;   // 原始文件
    private MidiFile? _workingFile;    // 应用移调后的文件
    private Playback? _playback;
    private OutputDevice? _output;
    private List<NoteInfo> _notes = new();
    private List<BeatLine> _beatLines = new();

    private string? _currentPortName;
    private TimeSpan _pausedPosition = TimeSpan.Zero;
    private PlayerStatus _status = PlayerStatus.Stopped;

    public event Action? PlaybackFinished;

    public string? CurrentFilePath { get; private set; }
    public string CurrentFileName => CurrentFilePath == null ? "(未打开)" : Path.GetFileName(CurrentFilePath);
    public IReadOnlyList<NoteInfo> Notes => _notes;
    public IReadOnlyList<BeatLine> BeatLines => _beatLines;
    public PlayerStatus Status => _status;

    public int Transpose { get; private set; } = 0;     // -12 .. +12
    public int OriginalBpm { get; private set; } = 120;
    public int CurrentBpm  { get; private set; } = 120;

    public static IEnumerable<string> GetOutputPortNames()
        => OutputDevice.GetAll().Select(d => d.Name);

    // ---------------- 加载 ----------------

    public void Load(string path)
    {
        Stop();
        _originalFile = MidiFile.Read(path);
        _workingFile = _originalFile;
        CurrentFilePath = path;
        Transpose = 0;

        // 读初始 BPM
        var tempoMap = _originalFile.GetTempoMap();
        var tempos = tempoMap.GetTempoChanges().ToList();
        OriginalBpm = tempos.Count > 0
            ? (int)Math.Round(60_000_000.0 / tempos[0].Value.MicrosecondsPerQuarterNote)
            : 120;
        CurrentBpm = OriginalBpm;

        // 解析音符和网格（基于原始文件，移调不影响可视化）
        _notes = new List<NoteInfo>();
        foreach (var note in _originalFile.GetNotes())
        {
            var start = note.TimeAs<MetricTimeSpan>(tempoMap);
            var length = note.LengthAs<MetricTimeSpan>(tempoMap);
            _notes.Add(new NoteInfo(
                note.NoteNumber,
                note.Velocity,
                TimeSpan.FromMicroseconds(start.TotalMicroseconds),
                TimeSpan.FromMicroseconds(length.TotalMicroseconds)));
        }
        _notes.Sort((a, b) => a.Start.CompareTo(b.Start));

        _beatLines = ComputeBeatLines(_originalFile, tempoMap);
    }

    private static List<BeatLine> ComputeBeatLines(MidiFile file, TempoMap tempoMap)
    {
        var result = new List<BeatLine>();
        if (file.TimeDivision is not TicksPerQuarterNoteTimeDivision ppq) return result;
        int tpqn = ppq.TicksPerQuarterNote;

        var sigList = new List<(long Tick, int Num, int Den)>();
        foreach (var s in tempoMap.GetTimeSignatureChanges())
            sigList.Add((s.Time, s.Value.Numerator, s.Value.Denominator));

        if (sigList.Count == 0 || sigList[0].Tick > 0)
            sigList.Insert(0, (0L, 4, 4));

        long totalTicks = file.GetDuration<MidiTimeSpan>().TimeSpan;

        for (int i = 0; i < sigList.Count; i++)
        {
            var (startTick, num, den) = sigList[i];
            long endTick = (i + 1 < sigList.Count) ? sigList[i + 1].Tick : totalTicks;
            if (den <= 0 || num <= 0) continue;

            long ticksPerBeat = (4L * tpqn) / den;
            if (ticksPerBeat <= 0) continue;

            int beatIdx = 0;
            for (long t = startTick; t < endTick; t += ticksPerBeat)
            {
                var metric = TimeConverter.ConvertTo<MetricTimeSpan>(t, tempoMap);
                result.Add(new BeatLine(
                    TimeSpan.FromMicroseconds(metric.TotalMicroseconds),
                    beatIdx % num == 0));
                beatIdx++;
            }
        }
        return result;
    }

    // ---------------- 移调 / BPM ----------------

    public void SetTranspose(int semitones)
    {
        semitones = Math.Clamp(semitones, -12, 12);
        if (semitones == Transpose) return;

        bool wasPlaying = _status == PlayerStatus.Playing;
        var pos = GetCurrentTime();
        var port = _currentPortName;

        Stop();
        Transpose = semitones;
        RebuildWorkingFile();

        if (wasPlaying && port != null)
        {
            Play(port);
            Seek(pos);
        }
    }

    public void SetBpm(int bpm)
    {
        bpm = Math.Clamp(bpm, 20, 400);
        if (bpm == CurrentBpm) return;
        CurrentBpm = bpm;

        if (_playback != null)
            _playback.Speed = (float)CurrentBpm / OriginalBpm;
    }

    private void RebuildWorkingFile()
    {
        if (_originalFile == null) return;

        if (Transpose == 0)
        {
            _workingFile = _originalFile;
            return;
        }

        // 序列化 + 读回，得到深拷贝
        using var ms = new MemoryStream();
        _originalFile.Write(ms);
        ms.Position = 0;
        _workingFile = MidiFile.Read(ms);

        // 遍历所有 NoteOn/NoteOff，改音高
        foreach (var chunk in _workingFile.GetTrackChunks())
        {
            foreach (var ev in chunk.Events)
            {
if (ev is NoteOnEvent on)
{
    int n = Math.Clamp((int)on.NoteNumber + Transpose, 0, 127);
    on.NoteNumber = (SevenBitNumber)(byte)n;
}
else if (ev is NoteOffEvent off)
{
    int n = Math.Clamp((int)off.NoteNumber + Transpose, 0, 127);
    off.NoteNumber = (SevenBitNumber)(byte)n;
}
            }
        }
    }

    // ---------------- 播放 ----------------

    public TimeSpan GetDuration()
    {
        if (_notes.Count == 0) return TimeSpan.Zero;
        return _notes.Max(n => n.Start + n.Duration);
    }

    public TimeSpan GetCurrentTime()
    {
        if (_status == PlayerStatus.Paused) return _pausedPosition;
        if (_playback == null) return TimeSpan.Zero;
        var t = _playback.GetCurrentTime<MetricTimeSpan>();
        return TimeSpan.FromMicroseconds(t.TotalMicroseconds);
    }

    public void Play(string portName)
    {
        if (_workingFile == null) return;
        StopInternal();

        _currentPortName = portName;
        _output = OutputDevice.GetByName(portName);
        _playback = _workingFile.GetPlayback(_output);
        _playback.Speed = (float)CurrentBpm / OriginalBpm;
        _playback.Finished += OnPlaybackFinished;
        _playback.Start();
        _status = PlayerStatus.Playing;
    }

    public void Pause()
    {
        if (_status != PlayerStatus.Playing || _playback == null) return;

        _pausedPosition = GetCurrentTime();
        _playback.Finished -= OnPlaybackFinished;
        if (_playback.IsRunning) _playback.Stop();
        _playback.Dispose();
        _playback = null;
        _status = PlayerStatus.Paused;
    }

    public void Resume()
    {
        if (_status != PlayerStatus.Paused || _workingFile == null || _output == null) return;

        _playback = _workingFile.GetPlayback(_output);
        _playback.Speed = (float)CurrentBpm / OriginalBpm;
        _playback.Finished += OnPlaybackFinished;
        _playback.MoveToTime(new MetricTimeSpan((long)_pausedPosition.TotalMicroseconds));
        _playback.Start();
        _status = PlayerStatus.Playing;
    }

    public void Seek(TimeSpan target)
    {
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        var duration = GetDuration();
        if (target > duration) target = duration;

        if (_status == PlayerStatus.Paused)
            _pausedPosition = target;
        else if (_playback != null)
            _playback.MoveToTime(new MetricTimeSpan((long)target.TotalMicroseconds));
    }

    public void Stop()
    {
        StopInternal();
        _status = PlayerStatus.Stopped;
        _pausedPosition = TimeSpan.Zero;
        CurrentBpm = OriginalBpm;
    }

    private void StopInternal()
    {
        if (_playback != null)
        {
            _playback.Finished -= OnPlaybackFinished;
            if (_playback.IsRunning) _playback.Stop();
            _playback.Dispose();
            _playback = null;
        }
        _output?.Dispose();
        _output = null;
    }

    private void OnPlaybackFinished(object? sender, EventArgs e) => PlaybackFinished?.Invoke();

    public void Dispose() => Stop();
}