using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MidiPlayer.Services;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace MidiPlayer.Controls;

/// <summary>
/// 可编辑的钢琴窗 / 鼓编辑器。
/// 左键放音符 / 拖动音符 / 拖右边缘改长度，右键删除（可滑动连续删），
/// Shift + 拖动框选，滚轮滚动网格，Ctrl / Alt + 滚轮缩放。
/// </summary>
public class PianoRollControl : ContentControl
{
    private enum DragMode { None, Seek, Create, Move, Resize, Marquee, Delete }

    private const float KeyWidth    = 66f;
    private const float RulerHeight = 26f;

    // ---------------- 配色（简约直角、暗色） ----------------
    private static readonly SKColor BgColor      = new(0x0B, 0x0B, 0x0F);
    private static readonly SKColor LaneWhite    = new(0x11, 0x11, 0x16);
    private static readonly SKColor LaneBlack    = new(0x0D, 0x0D, 0x12);
    private static readonly SKColor GridSub      = new SKColor(0xFF, 0xFF, 0xFF, 0x12);
    private static readonly SKColor GridBeat     = new SKColor(0xFF, 0xFF, 0xFF, 0x20);
    private static readonly SKColor GridBar      = new SKColor(0xFF, 0xFF, 0xFF, 0x3C);
    private static readonly SKColor RowLine      = new SKColor(0xFF, 0xFF, 0xFF, 0x0A);
    private static readonly SKColor PanelBg      = new(0x14, 0x14, 0x19);
    private static readonly SKColor WhiteKeyBg   = new(0x26, 0x26, 0x30);
    private static readonly SKColor BlackKeyBg   = new(0x15, 0x15, 0x1B);
    private static readonly SKColor TextColor    = new(0xA1, 0xA1, 0xAA);
    private static readonly SKColor TextDim      = new(0x71, 0x71, 0x7A);
    private static readonly SKColor NoteColor    = new(0xD9, 0xD4, 0xE8);
    private static readonly SKColor NoteHoverColor = new(0xF4, 0xF1, 0xFC);
    private static readonly SKColor Accent       = new(0x8B, 0x5C, 0xF6);
    private static readonly SKColor AccentSoft   = new(0xC4, 0xB5, 0xFD);
    private static readonly SKColor ScaleTint    = new SKColor(0x8B, 0x5C, 0xF6, 0x10);
    private static readonly SKColor OutOfScale   = new SKColor(0x00, 0x00, 0x00, 0x4A);
    private static readonly SKColor PlayheadColor = new(0xFA, 0xFA, 0xFA);

    private readonly SKElement _sk = new() { IgnorePixelScaling = true };
    private readonly SKPaint _fillPaint = new() { IsAntialias = false };
    private readonly SKPaint _linePaint = new() { IsAntialias = false };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _notePaint = new() { IsAntialias = false };
    private readonly SKPaint _playheadPaint = new() { IsAntialias = false };
    private readonly SKPaint _dashPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly bool[] _litKeys = new bool[128];
    private readonly SKFont _fontSmall;
    private readonly SKFont _fontTiny;

    // ---------------- 视图状态 ----------------
    private double _scrollRows;      // 从 MIDI 127 开始向下滚动的行数
    private long _scrollTicks;

    private readonly List<(EditorNote Note, long Length)> _resizeOrigins = new();
    private long _resizeAnchorLength;

    // 播放位置的平滑显示（避免被音频时钟的采样间隔抖成卡顿）
    private double _displayTicks;
    private long _displayLastStamp;
    private bool _displayReady;
    private long _lastRawPosition = -1;

    public double PxPerQuarter { get; private set; } = 64;
    public double RowHeight { get; private set; } = 14;

    // ---------------- 公开属性 ----------------
    public MidiDocument? Document { get; set; }
    public MidiEditorSession? Session { get; set; }
    public bool DrumMode { get; set; }
    public bool SnapEnabled { get; set; } = true;
    public bool ScaleAssist { get; set; }
    public int  ScaleRoot { get; set; }
    public int  ScaleKind { get; set; }

    public event Action? DocumentEdited;
    public event Action? SelectionChanged;
    public event Action? ViewChanged;
    /// <summary>(音高, 通道)</summary>
    public event Action<int, int>? PreviewRequested;

    // ---------------- 自适应网格 ----------------

    private static readonly int[] SnapDivisions = { 1, 2, 4, 8, 16, 32 };
    private const double MinSnapPixels = 14.0;

    /// <summary>根据当前缩放选择最合适的吸附精度（1/1 ~ 1/32）</summary>
    private (long Step, int Division) ComputeSnap()
    {
        long ppq = Document?.Ppq ?? MidiDocument.DefaultPpq;
        double pxPerTick = PxPerTick;

        for (int i = SnapDivisions.Length - 1; i >= 0; i--)
        {
            int division = SnapDivisions[i];
            double step = ppq * 4.0 / division;
            if (step * pxPerTick >= MinSnapPixels)
                return (Math.Max(1, (long)Math.Round(step)), division);
        }

        return (Math.Max(1, ppq * 4), 1);
    }

    public long SnapStepTicks
    {
        get
        {
            var (step, _) = ComputeSnap();
            return step;
        }
    }

    public string SnapLabel
    {
        get
        {
            if (!SnapEnabled) return "关闭";
            var (_, division) = ComputeSnap();
            return division == 1 ? "1/1" : $"1/{division}";
        }
    }

    public long DisplayPositionTicks =>
        Session?.IsPlaying == true
            ? (long)Math.Round(_displayTicks)
            : (Session?.PositionTicks ?? 0);

    // 供下面的力度 / 踏板编辑器对齐使用
    public float LaneOffsetX => KeyWidth;
    public float TimeToPixel(long tick) => TickToX(tick);
    public long  PixelToTick(float x) => XToTick(x);
    public float PixelsPerTick => (float)PxPerTick;

    /// <summary>鼓编辑器只显示鼓通道（通道 10 / 索引 9）的音符，钢琴窗反之</summary>
    public bool IsNoteVisibleInMode(EditorNote note) =>
        DrumMode ? note.Channel == 9 : note.Channel != 9;

    /// <summary>记住最近编辑过的音符长度 / 力度，用于下一个新音符</summary>
    public void RememberNoteTemplate(EditorNote note)
    {
        _templateLength = Math.Max(1, note.LengthTicks);
        _templateVelocity = Math.Clamp(note.Velocity, 1, 127);
    }

    // ---------------- 拖动状态 ----------------
    private DragMode _drag;
    private float _downX, _downY;
    private float _lastX, _lastY;
    private long _moveAnchorTick;
    private int _moveAnchorNote;
    private EditorNote? _activeNote;
    private readonly List<(EditorNote Note, long Start, int Pitch)> _moveOrigins = new();
    private SKRect _marquee;
    private readonly HashSet<EditorNote> _marqueeBase = new();
    private readonly Dictionary<int, long> _previewHighlights = new();
    private EditorNote? _hoverNote;
    private bool _dragMoved;
    private bool _undoPushed;

    // ---------------- 音符增删动画 ----------------
    private const int NoteAnimMs = 180;
    private readonly Dictionary<EditorNote, long> _noteBirth = new();
    private readonly List<GhostNote> _noteGhosts = new();

    private sealed class GhostNote
    {
        public long StartTicks;
        public long LengthTicks;
        public int NoteNumber;
        public int Channel;
        public bool WasSelected;
        public long StartMs;
    }

    private long _templateLength;
    private int  _templateVelocity = 100;
    private int  _lastPreviewPitch = -1;
    private long _lastPreviewAt;

    public static readonly string[] NoteNames =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static readonly string[] ScaleNames =
        { "大调", "自然小调", "和声小调", "大调五声", "小调五声", "布鲁斯", "多利亚", "混合利底亚" };

    private static readonly int[][] ScaleIntervals =
    {
        new[] { 0, 2, 4, 5, 7, 9, 11 },
        new[] { 0, 2, 3, 5, 7, 8, 10 },
        new[] { 0, 2, 3, 5, 7, 8, 11 },
        new[] { 0, 2, 4, 7, 9 },
        new[] { 0, 3, 5, 7, 10 },
        new[] { 0, 3, 5, 6, 7, 10 },
        new[] { 0, 2, 3, 5, 7, 9, 10 },
        new[] { 0, 2, 4, 5, 7, 9, 10 }
    };

    private static readonly string[] DrumNames =
    {
        "Kick", "Kick2", "Stick", "Snare", "Clap", "Snare2", "Tom L", "CHH",
        "Tom L2", "PHH", "Tom M", "OHH", "Tom M2", "Tom H", "Crash", "Tom H2",
        "Ride", "China", "Bell", "Tamb", "Splash", "Cowbell", "Crash2", "Vibraslap",
        "Ride2", "Bongo H", "Bongo L", "Conga H", "Conga O", "Conga L", "Timbale H",
        "Timbale L", "Agogo H", "Agogo L", "Cabasa", "Maracas", "Whistle S", "Whistle L",
        "Guiro S", "Guiro L", "Claves", "Block H", "Block L", "Cuica M", "Cuica O",
        "Tri M", "Tri O"
    };

    public PianoRollControl()
    {
        Focusable = true;
        Content = _sk;
        Cursor = Cursors.Cross;

        _fontSmall = new SKFont(SKTypeface.Default, 10f);
        _fontTiny  = new SKFont(SKTypeface.Default, 8.5f);

        _sk.PaintSurface += OnPaint;
        _sk.MouseLeftButtonDown  += OnMouseLeftDown;
        _sk.MouseLeftButtonUp    += OnMouseLeftUp;
        _sk.MouseRightButtonDown += OnMouseRightDown;
        _sk.MouseRightButtonUp   += OnMouseRightUp;
        _sk.MouseMove            += OnMouseMove;
        _sk.MouseLeave           += OnMouseLeave;
        _sk.MouseWheel           += OnMouseWheel;

        Loaded   += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    public void Refresh() => _sk.InvalidateVisual();

    public void ResetView()
    {
        _scrollTicks = 0;
        PxPerQuarter = 64;
        RowHeight = DrumMode ? 16 : 14;
        _scrollRows = DrumMode ? Math.Max(0, 127 - 84) : Math.Max(0, 127 - 100);
        _displayReady = false;
        _lastRawPosition = -1;
        _noteBirth.Clear();
        _noteGhosts.Clear();
        ClearSelection();
        ViewChanged?.Invoke();
        Refresh();
    }

    // ---------------- 坐标换算 ----------------

    private double PxPerTick => PxPerQuarter / Math.Max(1, Document?.Ppq ?? MidiDocument.DefaultPpq);

    private float TickToX(long tick) =>
        KeyWidth + (float)((tick - _scrollTicks) * PxPerTick);

    private long XToTick(float x)
    {
        var doc = Document;
        if (doc == null) return 0;
        return (long)Math.Round(_scrollTicks + (x - KeyWidth) * doc.Ppq / PxPerQuarter);
    }

    private float NoteTopY(int note) =>
        RulerHeight + (float)((127 - note - _scrollRows) * RowHeight);

    private int YToNote(float y) =>
        Math.Clamp(127 - (int)Math.Floor((y - RulerHeight) / RowHeight + _scrollRows), 0, 127);

    private SKRect NoteRect(EditorNote note)
    {
        float left = TickToX(note.StartTicks);
        float right = TickToX(note.EndTicks);
        float top = NoteTopY(note.NoteNumber);
        return new SKRect(left, top, Math.Max(right, left + 2f), top + (float)RowHeight);
    }

    private long SnapTick(long tick)
    {
        var doc = Document;
        if (doc == null) return Math.Max(0, tick);
        long step = SnapEnabled ? SnapStepTicks : 1;
        if (step <= 1) return Math.Max(0, tick);
        return Math.Max(0, (long)Math.Round(tick / (double)step) * step);
    }

    private void ClampScrollHorizontal()
    {
        if (_scrollTicks < 0) _scrollTicks = 0;
    }

    private void ClampScrollVertical()
    {
        double h = _sk.ActualHeight;
        if (h <= 0) h = 600;
        double visibleRows = Math.Max(1, (h - RulerHeight) / RowHeight);
        double max = Math.Max(0, 128 - visibleRows);
        _scrollRows = Math.Clamp(_scrollRows, 0, max);
    }

    // ---------------- 绘制 ----------------

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width;
        int h = e.Info.Height;
        canvas.Clear(BgColor);

        var doc = Document;
        if (doc == null || w <= 0 || h <= 0) return;

        float gridW = w - KeyWidth;
        if (gridW <= 0) return;

        long animNow = Environment.TickCount64;
        PruneNoteAnimations(animNow);

        int firstRow = (int)Math.Floor(_scrollRows);
        int visibleRows = (int)Math.Ceiling((h - RulerHeight) / RowHeight) + 1;

        // 1. 行底色 / 音阶辅助
        for (int i = 0; i < visibleRows; i++)
        {
            int note = 127 - (firstRow + i);
            if (note < 0 || note > 127) continue;

            float y = RulerHeight + (float)((firstRow + i - _scrollRows) * RowHeight);
            bool black = IsBlackKey(note);

            _fillPaint.Color = black ? LaneBlack : LaneWhite;
            canvas.DrawRect(KeyWidth, y, gridW, (float)RowHeight, _fillPaint);

            if (ScaleAssist && !DrumMode)
            {
                _fillPaint.Color = IsInScale(note) ? ScaleTint : OutOfScale;
                canvas.DrawRect(KeyWidth, y, gridW, (float)RowHeight, _fillPaint);
            }

            _linePaint.Color = RowLine;
            canvas.DrawRect(KeyWidth, y, gridW, 1, _linePaint);
        }

        // 2. 竖向网格（自适应）
        long beatTicks = doc.BeatTicks;
        long barTicks = doc.BarTicks;
        double pxBeat = beatTicks * PxPerTick;
        double pxBar = barTicks * PxPerTick;

        long viewStart = Math.Max(0, _scrollTicks);
        long viewEnd = XToTick(w) + beatTicks;

        if (SnapEnabled)
        {
            // 网格细分跟随当前吸附精度，看到的就是会吸附到的位置
            long snapStep = SnapStepTicks;
            double pxSnap = snapStep * PxPerTick;
            if (snapStep > 1 && snapStep != beatTicks && pxSnap >= 6)
            {
                _linePaint.Color = GridSub;
                long start = (viewStart / snapStep) * snapStep;
                for (long t = start; t <= viewEnd; t += snapStep)
                {
                    if (t % barTicks == 0) continue;
                    if (snapStep < beatTicks && t % beatTicks == 0) continue;
                    float x = TickToX(t);
                    canvas.DrawRect((int)x, RulerHeight, 1, h - RulerHeight, _linePaint);
                }
            }
        }
        else
        {
            // 关闭吸附时仍然按缩放显示 1/8、1/16 辅助线
            int subDiv = pxBeat >= 200 ? 4 : pxBeat >= 100 ? 2 : 1;
            long subStep = Math.Max(1, beatTicks / subDiv);
            double pxSub = subStep * PxPerTick;

            if (pxSub >= 6 && subDiv > 1)
            {
                _linePaint.Color = GridSub;
                long start = (viewStart / subStep) * subStep;
                for (long t = start; t <= viewEnd; t += subStep)
                {
                    float x = TickToX(t);
                    canvas.DrawRect((int)x, RulerHeight, 1, h - RulerHeight, _linePaint);
                }
            }
        }

        if (pxBeat >= 9)
        {
            _linePaint.Color = GridBeat;
            long start = (viewStart / beatTicks) * beatTicks;
            for (long t = start; t <= viewEnd; t += beatTicks)
            {
                float x = TickToX(t);
                canvas.DrawRect((int)x, RulerHeight, 1, h - RulerHeight, _linePaint);
            }
        }

        if (pxBar >= 5)
        {
            _linePaint.Color = GridBar;
            long start = (viewStart / barTicks) * barTicks;
            for (long t = start; t <= viewEnd; t += barTicks)
            {
                float x = TickToX(t);
                canvas.DrawRect((int)x, RulerHeight, 1, h - RulerHeight, _linePaint);
            }
        }

        // 3. 音符
        canvas.Save();
        canvas.ClipRect(new SKRect(KeyWidth, RulerHeight, w, h));

        foreach (var note in doc.Notes)
        {
            if (!IsNoteVisibleInMode(note)) continue;

            var r = NoteRect(note);
            if (r.Right < KeyWidth || r.Left > w) continue;
            if (r.Bottom < RulerHeight || r.Top > h) continue;

            // 新音符：上下收缩、左右轻微展开、透明度淡入，180ms 后恢复普通绘制
            float ease = BirthEase(note, animNow);
            float inset = (1f - ease) * (float)RowHeight * 0.32f;
            float widthScale = 0.58f + 0.42f * ease;
            float width = Math.Max(2f, r.Width * widthScale);
            float left = r.Left + (r.Width - width) * 0.5f;
            float top = r.Top + 1 + inset;
            float bottom = Math.Max(top + 2, r.Bottom - 1 - inset);
            byte alpha = (byte)Math.Clamp(255f * (0.35f + 0.65f * ease), 0f, 255f);

            if (note.IsSelected)
            {
                _notePaint.Color = Accent.WithAlpha(alpha);
                canvas.DrawRect(left, top, width, bottom - top, _notePaint);

                _notePaint.Color = AccentSoft.WithAlpha(alpha);
                canvas.DrawRect(left, top, width, 1, _notePaint);
                canvas.DrawRect(left, bottom - 1, width, 1, _notePaint);
            }
            else
            {
                float v = 0.62f + 0.38f * Math.Clamp(note.Velocity / 127f, 0f, 1f);
                var color = ReferenceEquals(note, _hoverNote)
                    ? NoteHoverColor
                    : new SKColor(
                        (byte)(NoteColor.Red * v),
                        (byte)(NoteColor.Green * v),
                        (byte)(NoteColor.Blue * v));
                _notePaint.Color = color.WithAlpha(alpha);
                canvas.DrawRect(left, top, width, bottom - top, _notePaint);
            }

            // 竖直放大时，在音符内部画一条代表力度的细条
            if (RowHeight >= 18)
            {
                float barH = Math.Max(2f, (float)RowHeight * 0.16f);
                float barW = Math.Max(2f, (width - 2) * Math.Clamp(note.Velocity / 127f, 0.05f, 1f));
                _fillPaint.Color = note.IsSelected
                    ? new SKColor(0xFF, 0xFF, 0xFF, 0x70)
                    : new SKColor(0x00, 0x00, 0x00, 0x55);
                canvas.DrawRect(left + 1, bottom - barH - 1, barW, barH, _fillPaint);
            }
        }

        // 被删除的音符先留下一个快速缩小、淡出的残影
        DrawNoteGhosts(canvas, animNow, w, h);

        canvas.Restore();

        // 4. 键盘 / 鼓名称（播放中 / 试听的琴键会亮起）
        Array.Clear(_litKeys, 0, _litKeys.Length);

        var nowMs = Environment.TickCount64;
        foreach (var key in _previewHighlights.Keys.ToList())
            if (_previewHighlights[key] < nowMs) _previewHighlights.Remove(key);

        bool playing = Session?.IsPlaying == true;
        long playTicks = playing ? DisplayPositionTicks : (Session?.PositionTicks ?? 0);
        foreach (var note in doc.Notes)
        {
            if (!IsNoteVisibleInMode(note)) continue;
            if ((playing && playTicks >= note.StartTicks && playTicks <= note.EndTicks)
                || _previewHighlights.ContainsKey(note.NoteNumber))
            {
                _litKeys[note.NoteNumber] = true;
            }
        }

        _fillPaint.Color = PanelBg;
        canvas.DrawRect(0, RulerHeight, KeyWidth, h - RulerHeight, _fillPaint);

        for (int i = 0; i < visibleRows; i++)
        {
            int note = 127 - (firstRow + i);
            if (note < 0 || note > 127) continue;

            float y = RulerHeight + (float)((firstRow + i - _scrollRows) * RowHeight);
            bool black = IsBlackKey(note);

            _fillPaint.Color = _litKeys[note]
                ? Accent
                : (DrumMode ? WhiteKeyBg : (black ? BlackKeyBg : WhiteKeyBg));
            canvas.DrawRect(0, y, KeyWidth - 1, (float)RowHeight, _fillPaint);

            _linePaint.Color = new SKColor(0x00, 0x00, 0x00, 0x66);
            canvas.DrawRect(0, y, KeyWidth - 1, 1, _linePaint);

            if (DrumMode)
            {
                _textPaint.Color = TextDim;
                canvas.DrawText(DrumName(note), 6, y + (float)RowHeight - 4,
                    SKTextAlign.Left, _fontTiny, _textPaint);
            }
            else
            {
                int pitchClass = note % 12;
                if (pitchClass == 0)
                {
                    _textPaint.Color = _litKeys[note] ? new SKColor(0xFF, 0xFF, 0xFF, 0xE6) : TextColor;
                    canvas.DrawText($"{NoteNames[pitchClass]}{note / 12 - 1}", 6,
                        y + (float)RowHeight - 4, SKTextAlign.Left, _fontSmall, _textPaint);
                }
            }
        }

        // 5. 顶部小节标尺
        _fillPaint.Color = PanelBg;
        canvas.DrawRect(0, 0, w, RulerHeight, _fillPaint);

        _linePaint.Color = GridBar;
        canvas.DrawRect(0, RulerHeight - 1, w, 1, _linePaint);

        if (pxBar >= 5)
        {
            long startBar = (viewStart / barTicks);
            for (long bar = startBar; ; bar++)
            {
                long t = bar * barTicks;
                float x = TickToX(t);
                if (x > w) break;

                _linePaint.Color = GridBar;
                canvas.DrawRect((int)x, RulerHeight - 7, 1, 7, _linePaint);

                if (pxBar >= 40)
                {
                    _textPaint.Color = TextDim;
                    canvas.DrawText((bar + 1).ToString(), x + 4, 12,
                        SKTextAlign.Left, _fontSmall, _textPaint);
                }
            }
        }

        // 6. 播放头（播放时使用平滑位置）
        var position = Session?.IsPlaying == true
            ? DisplayPositionTicks
            : (Session?.PositionTicks ?? 0);
        float px = TickToX(position);
        if (px >= KeyWidth && px <= w)
        {
            _playheadPaint.Color = PlayheadColor;
            canvas.DrawRect((int)px, 0, 1, h, _playheadPaint);
            canvas.DrawRect((int)px - 3, 0, 7, 3, _playheadPaint);
        }

        // 7. 框选框
        if (_drag == DragMode.Marquee && _marquee.Width > 1 && _marquee.Height > 1)
        {
            _fillPaint.Color = new SKColor(0x8B, 0x5C, 0xF6, 0x22);
            canvas.DrawRect(_marquee, _fillPaint);

            _dashPaint.Color = AccentSoft;
            _dashPaint.PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0);
            canvas.DrawRect(_marquee, _dashPaint);
            _dashPaint.PathEffect?.Dispose();
            _dashPaint.PathEffect = null;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        long animNow = Environment.TickCount64;
        PruneNoteAnimations(animNow);
        bool noteAnimating = _noteGhosts.Count > 0 || _noteBirth.Count > 0;

        var doc = Document;
        var session = Session;
        bool playing = session?.IsPlaying == true;
        long raw = session?.PositionTicks ?? 0;
        long now = Stopwatch.GetTimestamp();

        if (doc != null && session != null)
        {
            if (!playing || !_displayReady)
            {
                _displayTicks = raw;
                _displayReady = true;
                _displayLastStamp = now;
            }
            else
            {
                // 音频时钟是分块采样的，直接拿它推动画会一顿一顿的。
                // 这里用高精度计时器在两帧之间平滑推进，再慢慢校正到真实播放位置。
                double dt = (now - _displayLastStamp) / (double)Stopwatch.Frequency;
                _displayLastStamp = now;
                if (dt > 0.1) dt = 0.1;

                double ticksPerSecond = Math.Max(1, doc.Ppq * doc.Bpm / 60.0);
                _displayTicks += dt * ticksPerSecond;

                double error = raw - _displayTicks;
                double maxDrift = Math.Max(doc.Ppq * 0.1, ticksPerSecond * 0.04);
                if (Math.Abs(error) > maxDrift)
                    _displayTicks = raw;
                else
                    _displayTicks += error * 0.15;

                double end = Math.Max(doc.ContentEndTicks, doc.LastNoteEnd);
                if (_displayTicks < 0) _displayTicks = 0;
                if (_displayTicks > end + doc.Ppq) _displayTicks = end + doc.Ppq;
            }
        }

        if (doc != null && playing)
        {
            long tick = (long)Math.Round(_displayTicks);
            float x = TickToX(tick);
            float w = (float)_sk.ActualWidth;
            float viewW = w - KeyWidth;

            if (viewW > 0)
            {
                // 播放头走到画面中间后，让它一直停在中间，实现平滑自动滚动
                float targetX = KeyWidth + viewW * 0.5f;
                long newScroll = _scrollTicks;

                if (x > targetX)
                    newScroll = Math.Max(0, tick - (long)((targetX - KeyWidth) / PxPerTick));
                else if (x < KeyWidth + 4)
                    newScroll = Math.Max(0, tick - (long)(24 / PxPerTick));

                if (newScroll != _scrollTicks) _scrollTicks = newScroll;
            }
        }

        bool positionChanged = raw != _lastRawPosition;
        _lastRawPosition = raw;

        // 空闲时不要每帧重绘整张钢琴窗；播放、试听高亮、拖动时才持续刷新。
        if (playing || positionChanged || _previewHighlights.Count > 0
            || _drag != DragMode.None || noteAnimating)
            _sk.InvalidateVisual();
    }

    // ---------------- 鼠标 ----------------

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var doc = Document;
        if (doc == null) return;

        var p = e.GetPosition(_sk);
        float x = (float)p.X, y = (float)p.Y;
        _downX = _lastX = x;
        _downY = _lastY = y;
        _dragMoved = false;
        _undoPushed = true;

        // 顶部标尺：移动播放头
        if (y < RulerHeight && x > KeyWidth)
        {
            _drag = DragMode.Seek;
            _sk.CaptureMouse();
            SeekFromX(x);
            return;
        }

        // 左侧键盘：试听音高
        if (x < KeyWidth && y >= RulerHeight)
        {
            int note = YToNote(y);
            Preview(note, DrumMode ? 9 : 0);
            return;
        }

        if (x < KeyWidth || y < RulerHeight) return;

        // Shift：框选（在已有选中的基础上叠加）
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            _drag = DragMode.Marquee;
            _marquee = new SKRect(x, y, x, y);
            _marqueeBase.Clear();
            foreach (var n in doc.Notes.Where(n => n.IsSelected)) _marqueeBase.Add(n);
            _sk.CaptureMouse();
            return;
        }

        var hit = HitTest(x, y, out bool nearEnd);

        if (hit != null)
        {
            if (nearEnd)
            {
                if (!hit.IsSelected) SelectOnly(hit);
                RememberNoteTemplate(hit);
                _activeNote = hit;
                _drag = DragMode.Resize;
                _undoPushed = false;
                _resizeAnchorLength = hit.LengthTicks;
                _resizeOrigins.Clear();
                foreach (var n in doc.Notes)
                {
                    if (!n.IsSelected || !IsNoteVisibleInMode(n)) continue;
                    _resizeOrigins.Add((n, n.LengthTicks));
                }
                if (_resizeOrigins.Count == 0) _resizeOrigins.Add((hit, hit.LengthTicks));
                _sk.CaptureMouse();
                return;
            }

            if (!hit.IsSelected) SelectOnly(hit);
            RememberNoteTemplate(hit);

            _undoPushed = false;
            _activeNote = hit;
            _drag = DragMode.Move;
            _moveAnchorTick = SnapTick(XToTick(x));
            _moveAnchorNote = YToNote(y);
            _lastPreviewPitch = hit.NoteNumber;
            _lastPreviewAt = Environment.TickCount64;
            _moveOrigins.Clear();
            foreach (var n in doc.Notes.Where(n => n.IsSelected))
                _moveOrigins.Add((n, n.StartTicks, n.NoteNumber));

            if (SelectedCount() == 1) Preview(hit.NoteNumber, hit.Channel);
            _sk.CaptureMouse();
            return;
        }

        // 空白处：取消选中并创建新音符
        ClearSelection();
        doc.PushUndo();

        long start = SnapTick(XToTick(x));
        long defaultLength = SnapEnabled ? SnapStepTicks : Math.Max(1, doc.SnapTicks);
        long length = Math.Max(1, _templateLength > 0 ? _templateLength : defaultLength);
        var created = new EditorNote
        {
            NoteNumber = YToNote(y),
            StartTicks = start,
            LengthTicks = length,
            Velocity = Math.Clamp(_templateVelocity, 1, 127),
            Channel = DrumMode ? 9 : 0,
            IsSelected = true
        };
        doc.Notes.Add(created);
        _noteBirth[created] = Environment.TickCount64;
        doc.MarkDirty();
        RememberNoteTemplate(created);

        _activeNote = created;
        _drag = DragMode.Create;
        _sk.CaptureMouse();

        Preview(created.NoteNumber, created.Channel);
        RaiseEdited();
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;

        _sk.ReleaseMouseCapture();
        _drag = DragMode.None;
        _dragMoved = false;
        _marqueeBase.Clear();
        UpdateHover((float)e.GetPosition(_sk).X, (float)e.GetPosition(_sk).Y);
        SelectionChanged?.Invoke();
        _sk.InvalidateVisual();
    }

    private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var doc = Document;
        if (doc == null) return;

        var p = e.GetPosition(_sk);
        float x = (float)p.X, y = (float)p.Y;
        if (x < KeyWidth || y < RulerHeight) return;

        _downX = _lastX = x;
        _downY = _lastY = y;
        _drag = DragMode.Delete;
        _undoPushed = false;
        DeleteAt(x, y);
        _sk.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag != DragMode.Delete) return;
        _sk.ReleaseMouseCapture();
        _drag = DragMode.None;
        _sk.InvalidateVisual();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var doc = Document;
        if (doc == null) return;

        var p = e.GetPosition(_sk);
        float x = (float)p.X, y = (float)p.Y;

        switch (_drag)
        {
            case DragMode.Seek:
                SeekFromX(x);
                return;

            case DragMode.Marquee:
                _marquee = new SKRect(Math.Min(_downX, x), Math.Min(_downY, y),
                                      Math.Max(_downX, x), Math.Max(_downY, y));
                ApplyMarqueeSelection();
                Refresh();
                return;

            case DragMode.Move:
                DragMove(x, y);
                return;

            case DragMode.Create:
                DragCreateResize(x);
                return;

            case DragMode.Resize:
                DragResize(x);
                return;

            case DragMode.Delete:
                DeletePath(x, y);
                return;

            default:
                UpdateHover(x, y);
                return;
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            _hoverNote = null;
            Refresh();
        }
    }

    private void DragMove(float x, float y)
    {
        var doc = Document;
        if (doc == null || _moveOrigins.Count == 0) return;

        long target = SnapEnabled ? SnapTick(XToTick(x)) : XToTick(x);
        long deltaTicks = target - _moveAnchorTick;
        int deltaPitch = YToNote(y) - _moveAnchorNote;

        if (deltaTicks == 0 && deltaPitch == 0 && !_dragMoved) return;
        _dragMoved = true;

        if (!_undoPushed) { doc.PushUndo(); _undoPushed = true; }

        long minStart = _moveOrigins.Min(o => o.Start);
        int minPitch = _moveOrigins.Min(o => o.Pitch);
        int maxPitch = _moveOrigins.Max(o => o.Pitch);

        if (deltaTicks < -minStart) deltaTicks = -minStart;
        deltaPitch = Math.Clamp(deltaPitch, -minPitch, 127 - maxPitch);

        foreach (var (note, start, pitch) in _moveOrigins)
        {
            note.StartTicks = start + deltaTicks;
            note.NoteNumber = pitch + deltaPitch;
        }

        doc.MarkDirty();

        // 单个音符移动时试听新高度（多选拖动保持安静）。
        // 只有音高真的变了才出声，并且限制最小间隔，避免一路抖动乱响。
        if (_moveOrigins.Count == 1)
        {
            var only = _moveOrigins[0].Note;
            if (only.NoteNumber != _lastPreviewPitch &&
                Environment.TickCount64 - _lastPreviewAt >= 70)
            {
                _lastPreviewPitch = only.NoteNumber;
                _lastPreviewAt = Environment.TickCount64;
                Preview(only.NoteNumber, only.Channel);
            }
        }

        RaiseEdited();
    }

    private void DragCreateResize(float x)
    {
        var doc = Document;
        if (doc == null || _activeNote == null) return;

        long end = SnapEnabled ? SnapTick(XToTick(x)) : XToTick(x);
        long minLen = SnapEnabled ? SnapStepTicks : 1;
        long newLen = Math.Max(minLen, end - _activeNote.StartTicks);
        if (newLen == _activeNote.LengthTicks) return;

        _activeNote.LengthTicks = newLen;
        doc.MarkDirty();
        RememberNoteTemplate(_activeNote);
        _dragMoved = true;
        RaiseEdited();
    }

    private void DragResize(float x)
    {
        var doc = Document;
        if (doc == null || _activeNote == null) return;

        long end = SnapEnabled ? SnapTick(XToTick(x)) : XToTick(x);
        long minLen = SnapEnabled ? SnapStepTicks : 1;
        long newLen = Math.Max(minLen, end - _activeNote.StartTicks);
        long delta = newLen - _resizeAnchorLength;

        // 没有真正动过，且还没有产生任何长度变化时直接返回
        if (!_dragMoved && delta == 0) return;

        if (!_undoPushed) { doc.PushUndo(); _undoPushed = true; }

        // 多选时所有选中音符按同一个增量改变时值
        foreach (var (note, originalLength) in _resizeOrigins)
            note.LengthTicks = Math.Max(minLen, originalLength + delta);

        _activeNote.LengthTicks = newLen;
        doc.MarkDirty();
        RememberNoteTemplate(_activeNote);
        _dragMoved = true;
        RaiseEdited();
    }

    private void ApplyMarqueeSelection()
    {
        var doc = Document;
        if (doc == null) return;

        foreach (var note in doc.Notes)
        {
            if (!IsNoteVisibleInMode(note)) continue;
            note.IsSelected = _marqueeBase.Contains(note) || NoteRect(note).IntersectsWith(_marquee);
        }

        SelectionChanged?.Invoke();
    }

    private bool DeleteAt(float x, float y)
    {
        var doc = Document;
        if (doc == null) return false;
        if (x < KeyWidth || y < RulerHeight) return false;

        var hit = HitTest(x, y, out _);
        if (hit == null) return false;

        if (!_undoPushed)
        {
            doc.PushUndo();
            _undoPushed = true;
        }

        AddDeleteGhost(hit);
        doc.Notes.Remove(hit);
        doc.MarkDirty();
        if (ReferenceEquals(hit, _hoverNote)) _hoverNote = null;
        RaiseEdited();
        return true;
    }

    /// <summary>右键拖动时沿路径删除，快速滑动也不会漏掉音符</summary>
    private void DeletePath(float x, float y)
    {
        float dx = x - _lastX;
        float dy = y - _lastY;
        float distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
        int steps = Math.Max(1, (int)(distance / 4f));

        for (int i = 1; i <= steps; i++)
            DeleteAt(_lastX + dx * i / steps, _lastY + dy * i / steps);

        _lastX = x;
        _lastY = y;
    }

    private void SeekFromX(float x)
    {
        var doc = Document;
        if (_drag != DragMode.Seek || doc == null) return;

        long tick = Math.Max(0, XToTick(x));
        tick = Math.Min(tick, doc.ContentEndTicks);
        Session?.SeekTicks(tick);
        Refresh();
    }

    // ---------------- 滚轮 ----------------

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var doc = Document;
        if (doc == null) return;

        var p = e.GetPosition(_sk);
        int dir = e.Delta > 0 ? 1 : -1;
        var mods = Keyboard.Modifiers;

        if ((mods & ModifierKeys.Control) != 0)
            ZoomHorizontal((float)p.X, dir);
        else if ((mods & ModifierKeys.Alt) != 0)
            ZoomVertical((float)p.Y, dir);
        else if ((mods & ModifierKeys.Shift) != 0)
            ScrollHorizontal(dir, doc);
        else
            ScrollVertical(dir);

        Refresh();
        e.Handled = true;
    }

    private void ScrollVertical(int dir)
    {
        _scrollRows -= dir * 3;
        ClampScrollVertical();
    }

    private void ScrollHorizontal(int dir, MidiDocument doc)
    {
        _scrollTicks -= dir * doc.BeatTicks * 2;
        ClampScrollHorizontal();
        ViewChanged?.Invoke();
    }

    private void ZoomHorizontal(float anchorX, int dir)
    {
        var doc = Document;
        if (doc == null) return;

        double factor = dir > 0 ? 1.15 : 1 / 1.15;
        double newPx = Math.Clamp(PxPerQuarter * factor, 12, 900);
        if (Math.Abs(newPx - PxPerQuarter) < 0.001) return;

        long anchorTick = XToTick(anchorX);
        PxPerQuarter = newPx;
        _scrollTicks = Math.Max(0, anchorTick - (long)((anchorX - KeyWidth) * doc.Ppq / PxPerQuarter));
        ViewChanged?.Invoke();
    }

    private void ZoomVertical(float anchorY, int dir)
    {
        double factor = dir > 0 ? 1.15 : 1 / 1.15;
        double newH = Math.Clamp(RowHeight * factor, 6, 40);
        if (Math.Abs(newH - RowHeight) < 0.001) return;

        double anchorRow = (anchorY - RulerHeight) / RowHeight + _scrollRows;
        RowHeight = newH;
        _scrollRows = anchorRow - (anchorY - RulerHeight) / RowHeight;
        ClampScrollVertical();
    }

    // ---------------- 编辑操作（由外部快捷键调用） ----------------

    public void MoveSelectedNotes(int semitones)
    {
        var doc = Document;
        if (doc == null) return;

        var selected = doc.Notes.Where(n => n.IsSelected).ToList();
        if (selected.Count == 0) return;

        int minPitch = selected.Min(n => n.NoteNumber);
        int maxPitch = selected.Max(n => n.NoteNumber);
        semitones = Math.Clamp(semitones, -minPitch, 127 - maxPitch);
        if (semitones == 0) return;

        doc.PushUndo();
        foreach (var note in selected) note.NoteNumber += semitones;
        doc.MarkDirty();

        if (selected.Count == 1) Preview(selected[0].NoteNumber, selected[0].Channel);
        RaiseEdited();
    }

    public int DeleteSelectedNotes()
    {
        var doc = Document;
        if (doc == null) return 0;

        var deleting = doc.Notes.Where(n => n.IsSelected).ToList();
        if (deleting.Count == 0) return 0;

        doc.PushUndo();
        foreach (var note in deleting) AddDeleteGhost(note);
        doc.Notes.RemoveAll(n => n.IsSelected);
        doc.MarkDirty();
        RaiseEdited();
        SelectionChanged?.Invoke();
        return deleting.Count;
    }

    // ---------------- 复制 / 剪切 / 粘贴 ----------------

    /// <summary>
    /// 跨文档剪贴板用“拍”保存：把 tick 按源文档 PPQ 归一化，
    /// 粘贴到 PPQ 不同的 MIDI 时也能保持正确的音乐时值。
    /// </summary>
    private sealed class ClipboardNote
    {
        public double StartBeats;
        public double LengthBeats;
        public int NoteNumber;
        public int Velocity;
        public int Channel;
    }

    private static readonly List<ClipboardNote> NoteClipboard = new();

    private List<EditorNote> GetSelectedVisibleNotes()
    {
        var doc = Document;
        if (doc == null) return new List<EditorNote>();
        return doc.Notes.Where(n => n.IsSelected && IsNoteVisibleInMode(n)).ToList();
    }

    private static void StoreClipboard(MidiDocument doc, IReadOnlyList<EditorNote> notes)
    {
        NoteClipboard.Clear();
        double ppq = Math.Max(1, doc.Ppq);
        foreach (var note in notes)
        {
            NoteClipboard.Add(new ClipboardNote
            {
                StartBeats = note.StartTicks / ppq,
                LengthBeats = note.LengthTicks / ppq,
                NoteNumber = note.NoteNumber,
                Velocity = Math.Clamp(note.Velocity, 1, 127),
                Channel = Math.Clamp(note.Channel, 0, 15)
            });
        }
    }

    /// <summary>复制当前模式里选中的音符，返回复制数量。</summary>
    public int CopySelectedNotes()
    {
        var doc = Document;
        if (doc == null) return 0;

        var notes = GetSelectedVisibleNotes();
        if (notes.Count == 0) return 0;

        StoreClipboard(doc, notes);
        return notes.Count;
    }

    /// <summary>剪切当前模式里选中的音符，返回剪切数量。</summary>
    public int CutSelectedNotes()
    {
        var doc = Document;
        if (doc == null) return 0;

        var notes = GetSelectedVisibleNotes();
        if (notes.Count == 0) return 0;

        StoreClipboard(doc, notes);
        var set = new HashSet<EditorNote>(notes);

        doc.PushUndo();
        foreach (var note in notes)
        {
            AddDeleteGhost(note);
            _noteBirth.Remove(note);
        }
        doc.Notes.RemoveAll(set.Contains);
        doc.MarkDirty();

        if (_activeNote != null && set.Contains(_activeNote)) _activeNote = null;
        if (_hoverNote != null && set.Contains(_hoverNote)) _hoverNote = null;

        RaiseEdited();
        SelectionChanged?.Invoke();
        return notes.Count;
    }

    /// <summary>
    /// 粘贴到当前播放头位置。保留音符之间的相对时间和音高；
    /// 鼓编辑器统一贴到通道 10，钢琴窗保留原通道（鼓通道会转成通道 1）。
    /// </summary>
    public int PasteNotes()
    {
        var doc = Document;
        if (doc == null || NoteClipboard.Count == 0) return 0;

        long anchor = SnapTick(Session?.PositionTicks ?? 0);
        double firstStart = NoteClipboard.Min(n => n.StartBeats);
        long now = Environment.TickCount64;
        bool animate = NoteClipboard.Count <= 256;

        doc.PushUndo();
        foreach (var note in doc.Notes) note.IsSelected = false;

        var pasted = new List<EditorNote>(NoteClipboard.Count);
        foreach (var clip in NoteClipboard)
        {
            long start = anchor + (long)Math.Round((clip.StartBeats - firstStart) * doc.Ppq);
            long length = Math.Max(1, (long)Math.Round(clip.LengthBeats * doc.Ppq));
            int channel = DrumMode ? 9 : (clip.Channel == 9 ? 0 : clip.Channel);

            var note = new EditorNote
            {
                NoteNumber = Math.Clamp(clip.NoteNumber, 0, 127),
                StartTicks = Math.Max(0, start),
                LengthTicks = length,
                Velocity = Math.Clamp(clip.Velocity, 1, 127),
                Channel = channel,
                IsSelected = true
            };

            doc.Notes.Add(note);
            if (animate) _noteBirth[note] = now;
            pasted.Add(note);
        }

        doc.MarkDirty();
        if (pasted.Count > 0) RememberNoteTemplate(pasted[^1]);

        RaiseEdited();
        SelectionChanged?.Invoke();
        return pasted.Count;
    }

    public int SelectedCount() => Document?.Notes.Count(n => n.IsSelected) ?? 0;

    public void SelectOnly(EditorNote note)
    {
        var doc = Document;
        if (doc == null) return;
        foreach (var n in doc.Notes) n.IsSelected = ReferenceEquals(n, note);
        if (note != null) RememberNoteTemplate(note);
        SelectionChanged?.Invoke();
        Refresh();
    }

    public void ClearSelection()
    {
        var doc = Document;
        if (doc == null) return;
        foreach (var n in doc.Notes) n.IsSelected = false;
        SelectionChanged?.Invoke();
    }

    public void ScrollPlayheadIntoView()
    {
        var doc = Document;
        var session = Session;
        if (doc == null) return;

        long tick = session?.PositionTicks ?? 0;
        float w = (float)_sk.ActualWidth;
        if (w <= KeyWidth) return;

        float x = TickToX(tick);
        if (x < KeyWidth || x > w - 20)
        {
            _scrollTicks = Math.Max(0, tick - (long)((w - KeyWidth) * 0.3 / PxPerTick));
            ClampScrollHorizontal();
            ViewChanged?.Invoke();
        }
        Refresh();
    }

    // ---------------- 辅助 ----------------

    private EditorNote? HitTest(float x, float y, out bool nearEnd)
    {
        nearEnd = false;
        var doc = Document;
        if (doc == null) return null;

        for (int i = doc.Notes.Count - 1; i >= 0; i--)
        {
            var note = doc.Notes[i];
            if (!IsNoteVisibleInMode(note)) continue;

            var r = NoteRect(note);
            if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) continue;

            nearEnd = r.Width > 10 && x > r.Right - Math.Min(7f, r.Width * 0.32f);
            return note;
        }
        return null;
    }

    private void UpdateHover(float x, float y)
    {
        EditorNote? hit = null;
        bool nearEnd = false;

        if (x > KeyWidth && y > RulerHeight)
            hit = HitTest(x, y, out nearEnd);

        if (!ReferenceEquals(hit, _hoverNote))
        {
            _hoverNote = hit;
            if (hit != null) RememberNoteTemplate(hit);
            Refresh();
        }

        Cursor cursor;
        if (y < RulerHeight && x > KeyWidth) cursor = Cursors.Hand;
        else if (x < KeyWidth) cursor = Cursors.Hand;
        else if (hit == null) cursor = Cursors.Cross;
        else cursor = nearEnd ? Cursors.SizeWE : Cursors.SizeAll;

        Cursor = cursor;
        _sk.Cursor = cursor;
    }

    private void Preview(int note, int channel)
    {
        note = Math.Clamp(note, 0, 127);
        _previewHighlights[note] = Environment.TickCount64 + 280;
        PreviewRequested?.Invoke(note, channel);
    }

    private static float EaseOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    /// <summary>新音符的 0-1 出现进度；到期后自动从动画表里移除。</summary>
    private float BirthEase(EditorNote note, long nowMs)
    {
        if (!_noteBirth.TryGetValue(note, out long start)) return 1f;

        float t = (nowMs - start) / (float)NoteAnimMs;
        if (t >= 1f)
        {
            _noteBirth.Remove(note);
            return 1f;
        }
        return EaseOutCubic(t);
    }

    /// <summary>删除前记录一个残影，用于淡出动画。</summary>
    private void AddDeleteGhost(EditorNote note)
    {
        _noteBirth.Remove(note);
        if (!IsNoteVisibleInMode(note)) return;
        if (_noteGhosts.Count >= 256) return;   // 一次删太多时不再堆残影，避免掉帧

        _noteGhosts.Add(new GhostNote
        {
            StartTicks = note.StartTicks,
            LengthTicks = note.LengthTicks,
            NoteNumber = note.NoteNumber,
            Channel = note.Channel,
            WasSelected = note.IsSelected,
            StartMs = Environment.TickCount64
        });
    }

    private void PruneNoteAnimations(long nowMs)
    {
        for (int i = _noteGhosts.Count - 1; i >= 0; i--)
        {
            if (nowMs - _noteGhosts[i].StartMs >= NoteAnimMs)
                _noteGhosts.RemoveAt(i);
        }

        if (_noteBirth.Count == 0) return;

        List<EditorNote>? expired = null;
        foreach (var kv in _noteBirth)
        {
            if (nowMs - kv.Value >= NoteAnimMs)
                (expired ??= new List<EditorNote>()).Add(kv.Key);
        }
        if (expired != null)
            foreach (var note in expired) _noteBirth.Remove(note);
    }

    private void DrawNoteGhosts(SKCanvas canvas, long nowMs, float w, float h)
    {
        foreach (var ghost in _noteGhosts)
        {
            float t = (nowMs - ghost.StartMs) / (float)NoteAnimMs;
            if (t >= 1f) continue;
            if (DrumMode ? ghost.Channel != 9 : ghost.Channel == 9) continue;

            float left = TickToX(ghost.StartTicks);
            float right = TickToX(ghost.StartTicks + ghost.LengthTicks);
            float top = NoteTopY(ghost.NoteNumber);
            float bottom = top + (float)RowHeight;
            if (right < KeyWidth || left > w || bottom < RulerHeight || top > h) continue;

            float ease = Math.Clamp(t, 0f, 1f);
            float insetY = ease * ease * (float)RowHeight * 0.28f;
            float drawTop = top + 1 + insetY;
            float drawBottom = Math.Max(drawTop + 1, bottom - 1 - insetY);
            float insetX = ease * Math.Min(Math.Max(0f, right - left) * 0.18f, 5f);
            float drawLeft = left + insetX;
            float drawRight = Math.Max(drawLeft + 1, right - insetX);
            byte alpha = (byte)Math.Clamp(210f * (1f - t), 0f, 210f);

            var color = ghost.WasSelected ? Accent : NoteColor;
            _notePaint.Color = color.WithAlpha(alpha);
            canvas.DrawRect(drawLeft, drawTop, drawRight - drawLeft, drawBottom - drawTop, _notePaint);
        }
    }

    private void RaiseEdited()
    {
        DocumentEdited?.Invoke();
        _sk.InvalidateVisual();
    }

    private static bool IsBlackKey(int note)
    {
        int n = note % 12;
        return n == 1 || n == 3 || n == 6 || n == 8 || n == 10;
    }

    private bool IsInScale(int note)
    {
        int kind = Math.Clamp(ScaleKind, 0, ScaleIntervals.Length - 1);
        int intervals = (note % 12 - ScaleRoot % 12 + 12) % 12;
        return ScaleIntervals[kind].Contains(intervals);
    }

    private static string DrumName(int note) =>
        note is >= 35 and <= 81 ? DrumNames[note - 35] : $"N{note}";
}
