using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace MidiPlayer.Controls;

public partial class PianoKeyboardWindow : Window
{
    private const int MinOctave = 0;
    private const int MaxOctave = 7;
    private const int OctavesVisible = 3;
    private const int Velocity = 100;

    private int _baseOctave = 4;
    private OutputDevice? _output;

    private readonly HashSet<Key> _pressedKeys = new();
    private readonly HashSet<int> _activeNotes = new();
    private bool _shiftDown;
    private bool _sustainOn;

    private int? _mousePressedNote = null;

    // 电脑键盘 → 相对 baseOctave 的半音偏移
    private static readonly Dictionary<Key, int> KeyMap = new()
    {
        // 低八度（Z 行）
        { Key.Z, 0 },  { Key.S, 1 },  { Key.X, 2 },  { Key.D, 3 },  { Key.C, 4 },
        { Key.V, 5 },  { Key.G, 6 },  { Key.B, 7 },  { Key.H, 8 },  { Key.N, 9 },
        { Key.J, 10 }, { Key.M, 11 },
        { Key.OemComma, 12 }, { Key.L, 13 }, { Key.OemPeriod, 14 },
        { Key.OemSemicolon, 15 }, { Key.OemQuestion, 16 },

        // 高八度（Q 行）
        { Key.Q, 12 }, { Key.D2, 13 }, { Key.W, 14 }, { Key.D3, 15 }, { Key.E, 16 },
        { Key.R, 17 }, { Key.D5, 18 }, { Key.T, 19 }, { Key.D6, 20 }, { Key.Y, 21 },
        { Key.D7, 22 }, { Key.U, 23 }, { Key.I, 24 }, { Key.D9, 25 }, { Key.O, 26 },
        { Key.D0, 27 }, { Key.P, 28 },
    };

    // 绘制相关
    private readonly SKPaint _whitePaint  = new() { Color = new SKColor(0xE4, 0xE4, 0xE7), IsAntialias = false };
    private readonly SKPaint _blackPaint  = new() { Color = new SKColor(0x18, 0x18, 0x1B), IsAntialias = false };
    private readonly SKPaint _activeWhite = new() { Color = new SKColor(0x3B, 0x82, 0xF6), IsAntialias = false };
    private readonly SKPaint _activeBlack = new() { Color = new SKColor(0x60, 0xA5, 0xFA), IsAntialias = false };
    private readonly SKPaint _borderPaint = new() { Color = new SKColor(0x27, 0x27, 0x2A), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _labelPaint  = new() { Color = new SKColor(0x80, 0x80, 0x88), IsAntialias = true };
    private readonly SKFont  _labelFont   = new(SKTypeface.Default, 11);

private readonly DispatcherTimer _redrawTimer = new()
{
    Interval = TimeSpan.FromMilliseconds(33)
};

    public PianoKeyboardWindow(string portName)
    {
        InitializeComponent();

        try
        {
            _output = OutputDevice.GetByName(portName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开端口 {portName}: {ex.Message}", "错误");
            Loaded += (_, _) => Close();
            return;
        }

        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        BtnClose.Click   += (_, _) => Close();
        BtnOctDown.Click += (_, _) => ChangeOctave(-1);
        BtnOctUp.Click   += (_, _) => ChangeOctave(+1);

        PianoCanvas.PaintSurface += OnPaint;
        PianoCanvas.MouseDown += OnMouseDown;
        PianoCanvas.MouseMove += OnMouseMove;
        PianoCanvas.MouseUp   += OnMouseUp;

        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp   += OnKeyUp;

_redrawTimer.Tick += (_, _) => PianoCanvas.InvalidateVisual();
_redrawTimer.Start();

        UpdateRangeLabel();

        Closed += (_, _) =>
        {
            _redrawTimer.Stop();
            ReleaseAll();
            _output?.Dispose();
            _output = null;
        };
    }

    // ---------------- 键盘 ----------------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            _shiftDown = true;
            if (!_sustainOn)
            {
                _sustainOn = true;
                SendCC(64, 127);
            }
            e.Handled = true;
            return;
        }

        if (_shiftDown && (e.Key == Key.Left || e.Key == Key.Right))
        {
            ChangeOctave(e.Key == Key.Left ? -1 : +1);
            e.Handled = true;
            return;
        }

        if (!KeyMap.TryGetValue(e.Key, out int offset)) return;
        if (_pressedKeys.Contains(e.Key)) { e.Handled = true; return; }

        _pressedKeys.Add(e.Key);

        int note = MidiNote(offset);
        if (note >= 0 && note <= 127)
        {
            SendNoteOn(note, Velocity);
            _activeNotes.Add(note);
        }
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            _shiftDown = false;
            if (_sustainOn)
            {
                _sustainOn = false;
                SendCC(64, 0);
            }
            e.Handled = true;
            return;
        }

        if (!KeyMap.TryGetValue(e.Key, out int offset)) return;
        if (!_pressedKeys.Contains(e.Key)) return;

        _pressedKeys.Remove(e.Key);

        int note = MidiNote(offset);
        SendNoteOff(note);

        // 鼠标仍按着这个音符时不要熄灭
        if (_mousePressedNote != note)
            _activeNotes.Remove(note);

        e.Handled = true;
    }

    private int MidiNote(int offset) => (_baseOctave + 1) * 12 + offset;

    private void ChangeOctave(int delta)
    {
        int next = Math.Clamp(_baseOctave + delta, MinOctave, MaxOctave);
        if (next == _baseOctave) return;

        ReleaseAll();
        _baseOctave = next;
        UpdateRangeLabel();
    }

    private void ReleaseAll()
    {
        foreach (var note in _activeNotes.ToList())
            SendNoteOff(note);
        _activeNotes.Clear();
        _pressedKeys.Clear();
        _mousePressedNote = null;
    }

    private void UpdateRangeLabel()
    {
        int low = _baseOctave;
        int high = _baseOctave + OctavesVisible - 1;
        LblRange.Text = $"C{low} - B{high}";
    }

    // ---------------- 鼠标 ----------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(this);

        var pos = e.GetPosition(PianoCanvas);
        int? note = HitTestNote(pos);
        if (!note.HasValue) return;

        SendNoteOn(note.Value, Velocity);
        _activeNotes.Add(note.Value);
        _mousePressedNote = note;
        PianoCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mousePressedNote.HasValue) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(PianoCanvas);
        int? note = HitTestNote(pos);
        if (note == _mousePressedNote) return;

        int old = _mousePressedNote.Value;
        SendNoteOff(old);
        _activeNotes.Remove(old);

        if (note.HasValue)
        {
            SendNoteOn(note.Value, Velocity);
            _activeNotes.Add(note.Value);
        }
        _mousePressedNote = note;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mousePressedNote.HasValue)
        {
            int note = _mousePressedNote.Value;
            SendNoteOff(note);

            // 键盘仍按着同一音符时不熄灭
            bool stillHeld = _pressedKeys.Any(k =>
                KeyMap.TryGetValue(k, out int off) && MidiNote(off) == note);
            if (!stillHeld) _activeNotes.Remove(note);

            _mousePressedNote = null;
        }
        PianoCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private int? HitTestNote(Point p)
    {
        float w = (float)PianoCanvas.ActualWidth;
        float h = (float)PianoCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return null;

        int whiteCount = 7 * OctavesVisible;
        float whiteW = w / whiteCount;
        float blackW = whiteW * 0.6f;
        float blackH = h * 0.6f;

        int baseMidi = (_baseOctave + 1) * 12;

        // 黑键优先
        if (p.Y < blackH)
        {
            int[] blackSemitones = { 1, 3, 6, 8, 10 };
            int[] rightWhiteIdx  = { 1, 2, 4, 5, 6 };

            for (int o = 0; o < OctavesVisible; o++)
            {
                for (int k = 0; k < 5; k++)
                {
                    int absWhite = o * 7 + rightWhiteIdx[k];
                    float cx = absWhite * whiteW;
                    if (p.X >= cx - blackW / 2 && p.X < cx + blackW / 2)
                        return baseMidi + o * 12 + blackSemitones[k];
                }
            }
        }

        // 白键
        int wi = (int)(p.X / whiteW);
        if (wi < 0 || wi >= whiteCount) return null;
        int octave = wi / 7;
        int idx = wi % 7;
        int[] whiteSemitones = { 0, 2, 4, 5, 7, 9, 11 };
        return baseMidi + octave * 12 + whiteSemitones[idx];
    }

    // ---------------- MIDI 发送 ----------------

    private void SendNoteOn(int note, int velocity)
    {
        if (_output == null || note < 0 || note > 127) return;
        try
        {
            _output.SendEvent(new NoteOnEvent(
                (SevenBitNumber)(byte)note, (SevenBitNumber)(byte)velocity));
        }
        catch { }
    }

    private void SendNoteOff(int note)
    {
        if (_output == null || note < 0 || note > 127) return;
        try
        {
            _output.SendEvent(new NoteOffEvent(
                (SevenBitNumber)(byte)note, (SevenBitNumber)0));
        }
        catch { }
    }

    private void SendCC(int controller, int value)
    {
        if (_output == null) return;
        try
        {
            _output.SendEvent(new ControlChangeEvent(
                (SevenBitNumber)(byte)controller, (SevenBitNumber)(byte)value));
        }
        catch { }
    }

    // ---------------- 绘制 ----------------

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width;
        int h = e.Info.Height;
        canvas.Clear(new SKColor(0x18, 0x18, 0x1B));
        if (w <= 0 || h <= 0) return;

        int whiteCount = 7 * OctavesVisible;
        float whiteW = (float)w / whiteCount;
        float whiteH = h;
        float blackW = whiteW * 0.6f;
        float blackH = whiteH * 0.6f;

        int baseMidi = (_baseOctave + 1) * 12;
        int[] whiteSemitones = { 0, 2, 4, 5, 7, 9, 11 };

        // 白键
        for (int i = 0; i < whiteCount; i++)
        {
            int octave = i / 7;
            int idx = i % 7;
            int midi = baseMidi + octave * 12 + whiteSemitones[idx];
            float x = i * whiteW;

            var paint = _activeNotes.Contains(midi) ? _activeWhite : _whitePaint;
            canvas.DrawRect(x, 0, whiteW, whiteH, paint);
            canvas.DrawRect(x + 0.5f, 0.5f, whiteW - 1, whiteH - 1, _borderPaint);

            // 只在 C 键上标音名
            if (whiteSemitones[idx] == 0)
            {
                int octaveNum = midi / 12 - 1;
                string label = $"C{octaveNum}";
                float labelY = whiteH - 8;
                canvas.DrawText(label, x + whiteW / 2, labelY,
                                SKTextAlign.Center, _labelFont, _labelPaint);
            }
        }

        // 黑键
        int[] blackSemitones = { 1, 3, 6, 8, 10 };
        int[] rightWhiteIdx  = { 1, 2, 4, 5, 6 };

        for (int o = 0; o < OctavesVisible; o++)
        {
            for (int k = 0; k < 5; k++)
            {
                int midi = baseMidi + o * 12 + blackSemitones[k];
                int absWhite = o * 7 + rightWhiteIdx[k];
                float cx = absWhite * whiteW;
                float x = cx - blackW / 2;

                var paint = _activeNotes.Contains(midi) ? _activeBlack : _blackPaint;
                canvas.DrawRect(x, 0, blackW, blackH, paint);
            }
        }
    }
}