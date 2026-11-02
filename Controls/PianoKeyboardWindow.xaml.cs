using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private const int MaxOctave = 6;       // 3 个八度可见，最高到 B8，避免超出 MIDI 127
    private const int OctavesVisible = 3;
    private const int Velocity = 100;

    private const int PressAnimMs = 90;
    private const int ReleaseAnimMs = 160;

    private int _baseOctave = 4;
    private OutputDevice? _output;

    private readonly HashSet<Key> _pressedKeys = new();
    private readonly HashSet<int> _activeNotes = new();
    private readonly Dictionary<int, long> _keyPressStarted = new();
    private readonly Dictionary<int, long> _keyReleaseStarted = new();

    private bool _shiftDown;
    private bool _spaceDown;
    private bool _pedalLatched;
    private bool _pedalSentOn;

    private int? _mousePressedNote;
    private int? _hoverNote;
    private bool _mouseHeld;

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
        { Key.OemOpenBrackets, 29 }, { Key.OemPlus, 30 }, { Key.OemCloseBrackets, 31 },
        { Key.OemPipe, 33 }, { Key.OemBackslash, 33 },
    };

    private static readonly SolidColorBrush PedalOnBrush = CreateFrozenBrush(0xA7, 0x8B, 0xFA);
    private static readonly SolidColorBrush PedalOffBrush = CreateFrozenBrush(0x71, 0x71, 0x7A);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // 绘制相关：白色 / 黑色键底色不变，激活高亮使用主题紫
    private readonly SKPaint _whitePaint  = new() { Color = new SKColor(0xE4, 0xE4, 0xE7), IsAntialias = false };
    private readonly SKPaint _blackPaint  = new() { Color = new SKColor(0x18, 0x18, 0x1B), IsAntialias = false };
    private readonly SKPaint _activeWhite = new() { Color = new SKColor(0x8B, 0x5C, 0xF6), IsAntialias = false };
    private readonly SKPaint _activeBlack = new() { Color = new SKColor(0xA7, 0x8B, 0xFA), IsAntialias = false };
    private readonly SKPaint _borderPaint = new() { Color = new SKColor(0x27, 0x27, 0x2A), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _labelPaint  = new() { Color = new SKColor(0x80, 0x80, 0x88), IsAntialias = true };
    private readonly SKFont  _labelFont   = new(SKTypeface.Default, 10);
    private readonly SKPaint _keyFillPaint = new() { IsAntialias = false };
    private readonly SKPaint _keyBorderPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
    private readonly SKPaint _highlightPaint = new() { IsAntialias = false };
    private readonly SKPaint _pedalLinePaint = new() { IsAntialias = false };

    private bool PedalActive => _shiftDown || _spaceDown || _pedalLatched;

    public PianoKeyboardWindow(string portName)
    {
        InitializeComponent();

        try
        {
            _output = OutputDevice.GetByName(portName);
            SendCC(64, 0);   // 打开窗口时先把踏板复位，避免上一段 Sustain 残留
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
        BtnPedal.Click   += (_, _) => TogglePedalLatch();

        PianoCanvas.PaintSurface += OnPaint;
        PianoCanvas.MouseDown += OnMouseDown;
        PianoCanvas.MouseMove += OnMouseMove;
        PianoCanvas.MouseUp   += OnMouseUp;
        PianoCanvas.MouseLeave += OnMouseLeave;
        PianoCanvas.MouseWheel += OnMouseWheel;

        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp   += OnKeyUp;

        Loaded += OnLoaded;
        Deactivated += OnDeactivated;

        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;

            _shiftDown = false;
            _spaceDown = false;
            _pedalLatched = false;
            UpdatePedal(false);
            ReleaseAll();

            _output?.Dispose();
            _output = null;
        };

        UpdateRangeLabel();
        UpdatePedal(false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering += OnRendering;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = ease
        });

        _hoverNote = IsMouseOver ? HitTestNote(Mouse.GetPosition(PianoCanvas)) : null;
        PianoCanvas.InvalidateVisual();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 焦点跑掉时把所有按住的键和踏板都放掉，避免留下长音
        _shiftDown = false;
        _spaceDown = false;
        _pedalLatched = false;
        UpdatePedal(false);
        ReleaseAll();
        _hoverNote = null;
        PianoCanvas.InvalidateVisual();
    }

    // ---------------- 快捷键 ----------------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            if (!_shiftDown)
            {
                _shiftDown = true;
                UpdatePedal();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            if (!_spaceDown)
            {
                _spaceDown = true;
                UpdatePedal();
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

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && (e.Key == Key.Left || e.Key == Key.Right))
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
            if (_shiftDown)
            {
                _shiftDown = false;
                UpdatePedal();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            if (_spaceDown)
            {
                _spaceDown = false;
                UpdatePedal();
            }
            e.Handled = true;
            return;
        }

        if (!KeyMap.TryGetValue(e.Key, out int offset)) return;
        if (!_pressedKeys.Contains(e.Key)) return;

        _pressedKeys.Remove(e.Key);

        int note = MidiNote(offset);

        // 鼠标仍按着这个音符时不要熄灭，也不要提前发 Note Off
        if (_mousePressedNote != note)
        {
            SendNoteOff(note);
            _activeNotes.Remove(note);
        }

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

        _hoverNote = IsMouseOver ? HitTestNote(Mouse.GetPosition(PianoCanvas)) : null;
        AnimateOctaveSlide(delta);
        PianoCanvas.InvalidateVisual();
    }

    private void AnimateOctaveSlide(int delta)
    {
        if (PianoCanvas.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            PianoCanvas.RenderTransform = transform;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        double offset = delta > 0 ? -20 : 20;

        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = ease
            });

        PianoCanvas.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = ease
            });
    }

    private void ReleaseAll()
    {
        foreach (var note in _activeNotes.ToList())
            SendNoteOff(note);

        _activeNotes.Clear();
        _pressedKeys.Clear();
        _mousePressedNote = null;
        _mouseHeld = false;

        if (PianoCanvas.IsMouseCaptured)
            PianoCanvas.ReleaseMouseCapture();

        PianoCanvas.InvalidateVisual();
    }

    private void UpdateRangeLabel()
    {
        int low = _baseOctave;
        int high = _baseOctave + OctavesVisible - 1;
        LblRange.Text = $"C{low} - B{high}";
    }

    // ---------------- 踏板 ----------------

    private void TogglePedalLatch()
    {
        _pedalLatched = !_pedalLatched;
        UpdatePedal();
    }

    private void UpdatePedal(bool animate = true)
    {
        bool on = PedalActive;

        if (on != _pedalSentOn)
        {
            SendCC(64, on ? 127 : 0);
            _pedalSentOn = on;
        }

        UpdatePedalUi(on, animate);
        PianoCanvas.InvalidateVisual();
    }

    private void UpdatePedalUi(bool on, bool animate)
    {
        PedalDot.Fill = on ? PedalOnBrush : PedalOffBrush;
        LblPedal.Foreground = on ? PedalOnBrush : PedalOffBrush;
        LblPedal.Text = on ? "踏板 开" : "踏板 关";
        BtnPedal.ToolTip = on
            ? "踏板已踩下，点击松开；Shift / 空格按住也可以"
            : "踏板已松开，点击踩下；Shift / 空格按住也可以";

        if (!animate) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var pulse = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = ease
        };

        PedalDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        PedalDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    // ---------------- 鼠标 ----------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        Keyboard.Focus(this);

        var pos = e.GetPosition(PianoCanvas);
        int? note = HitTestNote(pos);
        _hoverNote = note;
        if (!note.HasValue) return;

        SendNoteOn(note.Value, Velocity);
        _activeNotes.Add(note.Value);
        _mousePressedNote = note;
        _mouseHeld = true;
        PianoCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(PianoCanvas);
        int? hit = HitTestNote(pos);

        if (e.LeftButton != MouseButtonState.Pressed)
            _mouseHeld = false;

        if (!_mouseHeld)
        {
            // 还没按住：只更新悬停
            if (hit != _hoverNote)
            {
                _hoverNote = hit;
                PianoCanvas.InvalidateVisual();
            }
            return;
        }

        if (hit == _mousePressedNote)
        {
            if (hit != _hoverNote)
            {
                _hoverNote = hit;
                PianoCanvas.InvalidateVisual();
            }
            return;
        }

        if (_mousePressedNote.HasValue)
        {
            int old = _mousePressedNote.Value;
            bool keyboardHoldsOld = _pressedKeys.Any(k =>
                KeyMap.TryGetValue(k, out int off) && MidiNote(off) == old);
            if (!keyboardHoldsOld)
            {
                SendNoteOff(old);
                _activeNotes.Remove(old);
            }
        }

        if (hit.HasValue)
        {
            SendNoteOn(hit.Value, Velocity);
            _activeNotes.Add(hit.Value);
        }

        _mousePressedNote = hit;
        _hoverNote = hit;
        PianoCanvas.InvalidateVisual();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _mouseHeld = false;

        if (_mousePressedNote.HasValue)
        {
            int note = _mousePressedNote.Value;

            // 键盘仍按着同一音符时不熄灭，也不要提前发 Note Off
            bool stillHeld = _pressedKeys.Any(k =>
                KeyMap.TryGetValue(k, out int off) && MidiNote(off) == note);
            if (!stillHeld)
            {
                SendNoteOff(note);
                _activeNotes.Remove(note);
            }

            _mousePressedNote = null;
        }

        if (PianoCanvas.IsMouseCaptured)
            PianoCanvas.ReleaseMouseCapture();

        _hoverNote = HitTestNote(e.GetPosition(PianoCanvas));
        PianoCanvas.InvalidateVisual();
        e.Handled = true;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_mouseHeld) return;
        if (_hoverNote == null) return;

        _hoverNote = null;
        PianoCanvas.InvalidateVisual();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        ChangeOctave(e.Delta > 0 ? +1 : -1);
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
        if (note < 0 || note > 127) return;

        _keyReleaseStarted.Remove(note);
        _keyPressStarted[note] = Environment.TickCount64;
        PianoCanvas.InvalidateVisual();

        if (_output == null) return;
        try
        {
            _output.SendEvent(new NoteOnEvent(
                (SevenBitNumber)(byte)note, (SevenBitNumber)(byte)velocity));
        }
        catch { }
    }

    private void SendNoteOff(int note)
    {
        if (note < 0 || note > 127) return;

        _keyPressStarted.Remove(note);
        _keyReleaseStarted[note] = Environment.TickCount64;
        PianoCanvas.InvalidateVisual();

        if (_output == null) return;
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

    // ---------------- 动画帧 ----------------

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        if (_keyPressStarted.Count == 0 && _keyReleaseStarted.Count == 0) return;

        long now = Environment.TickCount64;
        PianoCanvas.InvalidateVisual();
        PruneKeyAnimations(now);
    }

    private void PruneKeyAnimations(long now)
    {
        if (_keyPressStarted.Count > 0)
        {
            var expired = _keyPressStarted
                .Where(kv => now - kv.Value >= PressAnimMs)
                .Select(kv => kv.Key)
                .ToList();
            foreach (int key in expired) _keyPressStarted.Remove(key);
        }

        if (_keyReleaseStarted.Count > 0)
        {
            var expired = _keyReleaseStarted
                .Where(kv => now - kv.Value >= ReleaseAnimMs)
                .Select(kv => kv.Key)
                .ToList();
            foreach (int key in expired) _keyReleaseStarted.Remove(key);
        }
    }

    private float KeyPressAmount(int midi, long now)
    {
        if (_keyPressStarted.TryGetValue(midi, out long pressStart))
        {
            float t = (now - pressStart) / (float)PressAnimMs;
            if (t >= 1f)
            {
                _keyPressStarted.Remove(midi);
                return 1f;
            }
            return EaseOutCubic(Math.Clamp(t, 0f, 1f));
        }

        // 鼠标和键盘同时按着同一个音时，以“仍按住”为准
        if (_activeNotes.Contains(midi)) return 1f;

        if (_keyReleaseStarted.TryGetValue(midi, out long releaseStart))
        {
            float t = (now - releaseStart) / (float)ReleaseAnimMs;
            if (t >= 1f)
            {
                _keyReleaseStarted.Remove(midi);
                return 0f;
            }
            return 1f - EaseOutCubic(Math.Clamp(t, 0f, 1f));
        }

        return 0f;
    }

    private static float EaseOutCubic(float t)
    {
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    private static SKColor Lerp(SKColor from, SKColor to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(from.Red   + (to.Red   - from.Red)   * t),
            (byte)(from.Green + (to.Green - from.Green) * t),
            (byte)(from.Blue  + (to.Blue  - from.Blue)  * t),
            (byte)(from.Alpha + (to.Alpha - from.Alpha) * t));
    }

    // ---------------- 绘制 ----------------

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width;
        int h = e.Info.Height;
        canvas.Clear(new SKColor(0x18, 0x18, 0x1B));
        if (w <= 0 || h <= 0) return;

        long now = Environment.TickCount64;

        int whiteCount = 7 * OctavesVisible;
        float whiteW = (float)w / whiteCount;
        float whiteH = h;
        float blackW = whiteW * 0.6f;
        float blackH = whiteH * 0.6f;

        int baseMidi = (_baseOctave + 1) * 12;
        int[] whiteSemitones = { 0, 2, 4, 5, 7, 9, 11 };
        string[] whiteNames = { "C", "D", "E", "F", "G", "A", "B" };

        // 白键
        for (int i = 0; i < whiteCount; i++)
        {
            int octave = i / 7;
            int idx = i % 7;
            int midi = baseMidi + octave * 12 + whiteSemitones[idx];
            float x = i * whiteW;

            float press = KeyPressAmount(midi, now);
            float shift = press * 2f;
            float drawH = Math.Max(1f, whiteH - shift);

            _keyFillPaint.Color = Lerp(_whitePaint.Color, _activeWhite.Color, press * 0.92f);
            canvas.DrawRect(x, shift, whiteW, drawH, _keyFillPaint);
            canvas.DrawRect(x + 0.5f, shift + 0.5f, whiteW - 1f, drawH - 1f, _borderPaint);

            if (_hoverNote == midi && press < 0.04f)
            {
                _keyFillPaint.Color = new SKColor(0x8B, 0x5C, 0xF6, 0x18);
                canvas.DrawRect(x + 1, shift + 1, whiteW - 2, drawH - 2, _keyFillPaint);

                _keyBorderPaint.Color = new SKColor(0xA7, 0x8B, 0xFA, 0x9A);
                canvas.DrawRect(x + 0.5f, shift + 0.5f, whiteW - 1f, drawH - 1f, _keyBorderPaint);
            }

            if (press > 0.02f)
            {
                _keyBorderPaint.Color = new SKColor(0xA7, 0x8B, 0xFA, (byte)(180 * press));
                canvas.DrawRect(x + 0.5f, shift + 0.5f, whiteW - 1f, drawH - 1f, _keyBorderPaint);

                _highlightPaint.Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)(80 * press));
                canvas.DrawRect(x, shift, whiteW, 2, _highlightPaint);
            }

            // 所有白键都标音名；按下的键文字变亮
            _labelPaint.Color = press > 0.05f
                ? new SKColor(0xFA, 0xFA, 0xFA)
                : new SKColor(0x80, 0x80, 0x88);
            string label = $"{whiteNames[idx]}{midi / 12 - 1}";
            canvas.DrawText(label, x + whiteW / 2, whiteH - 8,
                            SKTextAlign.Center, _labelFont, _labelPaint);
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

                float press = KeyPressAmount(midi, now);
                float shift = press * 2f;
                float drawH = Math.Max(1f, blackH - shift);

                _keyFillPaint.Color = Lerp(_blackPaint.Color, _activeBlack.Color, press * 0.95f);
                canvas.DrawRect(x, shift, blackW, drawH, _keyFillPaint);

                if (_hoverNote == midi && press < 0.04f)
                {
                    _keyFillPaint.Color = new SKColor(0x8B, 0x5C, 0xF6, 0x20);
                    canvas.DrawRect(x + 1, shift + 1, blackW - 2, drawH - 2, _keyFillPaint);

                    _keyBorderPaint.Color = new SKColor(0xA7, 0x8B, 0xFA, 0x9A);
                    canvas.DrawRect(x + 0.5f, shift + 0.5f, blackW - 1f, drawH - 1f, _keyBorderPaint);
                }

                if (press > 0.02f)
                {
                    _keyBorderPaint.Color = new SKColor(0xA7, 0x8B, 0xFA, (byte)(190 * press));
                    canvas.DrawRect(x + 0.5f, shift + 0.5f, blackW - 1f, drawH - 1f, _keyBorderPaint);

                    _highlightPaint.Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)(70 * press));
                    canvas.DrawRect(x, shift, blackW, 2, _highlightPaint);
                }
            }
        }

        // 踏板踩下时，键盘顶部亮一条细线，配合底部状态文字
        if (PedalActive)
        {
            _pedalLinePaint.Color = new SKColor(0xA7, 0x8B, 0xFA, 0xE6);
            canvas.DrawRect(0, 0, w, 2, _pedalLinePaint);
        }
    }
}
