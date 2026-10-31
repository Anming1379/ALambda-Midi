using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;

namespace MidiPlayer.Services;

/// <summary>
/// MIDI 编辑器的播放会话：负责试听、播放 / 暂停 / 停止 / 定位。
/// 播放和试听共用同一个输出端口，避免占用冲突。
/// </summary>
public sealed class MidiEditorSession : IDisposable
{
    private readonly MidiDocument _doc;
    private readonly DispatcherTimer _previewTimer;
    private readonly List<(DateTime Due, int Note, int Channel)> _previewOffs = new();

    private OutputDevice? _output;
    private string? _portName;
    private Playback? _playback;
    private TempoMap? _tempoMap;
    private long _positionTicks;
    private bool _playing;

    public MidiEditorSession(MidiDocument doc)
    {
        _doc = doc;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _previewTimer.Tick += (_, _) => FlushPreviewOffs();
        _previewTimer.Start();
    }

    public bool IsPlaying => _playing;

    public long PositionTicks
    {
        get
        {
            if (!_playing || _playback == null || _tempoMap == null) return _positionTicks;
            try
            {
                var metric = _playback.GetCurrentTime<MetricTimeSpan>();
                return Math.Max(0, TimeConverter.ConvertTo<MidiTimeSpan>(metric, _tempoMap).TimeSpan);
            }
            catch
            {
                return _positionTicks;
            }
        }
    }

    // ---------------- 播放 ----------------

    public bool Play(string? portName)
    {
        if (_doc.Notes.Count == 0) return false;
        if (_playing) return true;

        // 播完之后再按播放：从头开始
        if (_doc.LastNoteEnd > 0 && _positionTicks >= _doc.LastNoteEnd)
            _positionTicks = 0;

        var output = EnsureOutput(portName);
        if (output == null) return false;

        try
        {
            var file = _doc.BuildMidiFile();
            _tempoMap = file.GetTempoMap();

            ReleasePreviews();
            MidiPlayerService.ResetControllers(output);

            _playback = file.GetPlayback(output);
            _playback.Finished += OnPlaybackFinished;

            var start = TimeConverter.ConvertTo<MetricTimeSpan>(_positionTicks, _tempoMap);
            _playback.MoveToTime(start);
            _playback.Start();
            _playing = true;
            return true;
        }
        catch
        {
            CleanupPlayback();
            return false;
        }
    }

    public void Pause()
    {
        if (!_playing) return;
        _positionTicks = PositionTicks;
        _playing = false;
        CleanupPlayback();
    }

    /// <summary>离开编辑器时释放输出端口，主窗口可以继续使用同一个端口</summary>
    public void ReleaseOutput()
    {
        if (_playing) _positionTicks = PositionTicks;
        _playing = false;
        CleanupPlayback();
        ReleasePreviews();
        DisposeOutput();
        _tempoMap = null;
    }

    public void Stop()
    {
        _playing = false;
        _positionTicks = 0;
        CleanupPlayback();
        ReleasePreviews();

        if (_output != null)
        {
            try { MidiPlayerService.ResetControllers(_output); } catch { }
        }
    }

    public void SeekTicks(long ticks)
    {
        ticks = Math.Clamp(ticks, 0, Math.Max(0, _doc.ContentEndTicks));

        if (_playing && _playback != null && _tempoMap != null)
        {
            try
            {
                _playback.MoveToTime(TimeConverter.ConvertTo<MetricTimeSpan>(ticks, _tempoMap));
            }
            catch { }
        }
        _positionTicks = ticks;
    }

    private void OnPlaybackFinished(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        void Finish()
        {
            if (!ReferenceEquals(sender, _playback)) return;
            _positionTicks = Math.Max(0, _doc.LastNoteEnd);
            _playing = false;
            CleanupPlayback();
        }

        if (dispatcher == null) Finish();
        else
        {
            try { dispatcher.BeginInvoke(new Action(Finish)); }
            catch { Finish(); }
        }
    }

    private void CleanupPlayback()
    {
        var playback = _playback;
        _playback = null;
        if (playback == null) return;
        try { playback.Finished -= OnPlaybackFinished; } catch { }
        try { if (playback.IsRunning) playback.Stop(); } catch { }
        try { playback.Dispose(); } catch { }
    }

    // ---------------- 试听 ----------------

    public void PreviewNote(int noteNumber, int channel, string? portName)
    {
        noteNumber = Math.Clamp(noteNumber, 0, 127);
        channel = Math.Clamp(channel, 0, 15);

        var output = EnsureOutput(portName);
        if (output == null) return;

        try
        {
            // 先关掉同一个音，避免连点造成叠音
            output.SendEvent(new NoteOffEvent(
                (SevenBitNumber)noteNumber, (SevenBitNumber)0)
            {
                Channel = (FourBitNumber)channel
            });
            output.SendEvent(new NoteOnEvent(
                (SevenBitNumber)noteNumber, (SevenBitNumber)100)
            {
                Channel = (FourBitNumber)channel
            });
            _previewOffs.Add((DateTime.UtcNow.AddMilliseconds(230), noteNumber, channel));
        }
        catch { }
    }

    private void FlushPreviewOffs()
    {
        if (_previewOffs.Count == 0 || _output == null) return;
        var now = DateTime.UtcNow;
        for (int i = _previewOffs.Count - 1; i >= 0; i--)
        {
            var (due, note, channel) = _previewOffs[i];
            if (due > now) continue;
            _previewOffs.RemoveAt(i);
            try
            {
                _output.SendEvent(new NoteOffEvent(
                    (SevenBitNumber)note, (SevenBitNumber)0)
                {
                    Channel = (FourBitNumber)channel
                });
            }
            catch { }
        }
    }

    private void ReleasePreviews()
    {
        var snapshot = _previewOffs.ToArray();
        _previewOffs.Clear();
        if (_output == null) return;
        foreach (var (_, note, channel) in snapshot)
        {
            try
            {
                _output.SendEvent(new NoteOffEvent(
                    (SevenBitNumber)note, (SevenBitNumber)0)
                {
                    Channel = (FourBitNumber)channel
                });
            }
            catch { }
        }
    }

    // ---------------- 输出端口 ----------------

    private OutputDevice? EnsureOutput(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName) || portName.StartsWith("("))
            return _output;

        if (_output != null && string.Equals(_portName, portName, StringComparison.Ordinal))
            return _output;

        // 播放中不换端口，避免声音断掉
        if (_playing && _output != null) return _output;

        DisposeOutput();
        try
        {
            _output = OutputDevice.GetByName(portName);
            _portName = portName;
        }
        catch
        {
            _output = null;
            _portName = null;
        }
        return _output;
    }

    private void DisposeOutput()
    {
        try { _output?.Dispose(); } catch { }
        _output = null;
    }

    public void Dispose()
    {
        _playing = false;
        _previewTimer.Stop();
        ReleasePreviews();
        CleanupPlayback();
        DisposeOutput();
    }
}
