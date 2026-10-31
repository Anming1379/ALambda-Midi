using System;
using System.Collections.Generic;
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
/// 钢琴窗下方的力度 / 踏板编辑区。
/// 水平时间轴与 PianoRollControl 完全同步。
///
/// 力度页：拖动竖条修改力度。
/// 踏板页：左键拖动添加踏板，右键拖动删除踏板；单击则只改一个点。
/// </summary>
public class MidiLaneControl : ContentControl
{
    private static readonly SKColor BgColor    = new(0x0D, 0x0D, 0x12);
    private static readonly SKColor PanelBg    = new(0x14, 0x14, 0x19);
    private static readonly SKColor GridColor  = new SKColor(0xFF, 0xFF, 0xFF, 0x14);
    private static readonly SKColor TextColor  = new(0xA1, 0xA1, 0xAA);
    private static readonly SKColor DimColor   = new(0x71, 0x71, 0x7A);
    private static readonly SKColor VelColor   = new(0xB4, 0xAE, 0xC6);
    private static readonly SKColor VelHover   = new(0xE8, 0xE4, 0xF5);
    private static readonly SKColor Accent     = new(0x8B, 0x5C, 0xF6);
    private static readonly SKColor AccentSoft = new(0xC4, 0xB5, 0xFD);
    private static readonly SKColor PedalFill  = new SKColor(0xFF, 0xFF, 0xFF, 0x0C);
    private static readonly SKColor PedalLine  = new SKColor(0x8B, 0x5C, 0xF6, 0xC0);
    private static readonly SKColor Playhead   = new(0xFA, 0xFA, 0xFA);

    private readonly SKElement _sk = new() { IgnorePixelScaling = true };
    private readonly SKPaint _fillPaint = new() { IsAntialias = false };
    private readonly SKPaint _linePaint = new() { IsAntialias = false };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKFont _fontTiny;

    private MidiDocument? _doc;

    // 拖动状态
    private bool _dragging;
    private bool _pedalDrag;
    private bool _pedalAdd;          // true = 左键添加，false = 右键删除
    private bool _pedalMoved;
    private float _pedalStartX;
    private long _pedalStart;
    private List<PedalEvent>? _pedalBefore;
    private bool _pedalUndoPushed;
    private bool _pedalChanged;

    private readonly List<EditorNote> _velocityNotes = new();
    private bool _velocityUndoPushed;
    private EditorNote? _hoverNote;
    private long _lastPaintPosition = -1;

    public MidiLaneControl()
    {
        Focusable = true;
        Content = _sk;
        _fontTiny = new SKFont(SKTypeface.Default, 8.5f);

        _sk.PaintSurface += OnPaint;
        _sk.MouseLeftButtonDown += OnMouseDown;
        _sk.MouseRightButtonDown += OnMouseRightDown;
        _sk.MouseMove += OnMouseMove;
        _sk.MouseLeftButtonUp += OnMouseUp;
        _sk.MouseRightButtonUp += OnMouseRightUp;
        _sk.MouseLeave += (_, _) => { if (!_dragging) { _hoverNote = null; Refresh(); } };

        Loaded   += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    public MidiDocument? Document
    {
        get => _doc;
        set { _doc = value; Refresh(); }
    }

    public MidiEditorSession? Session { get; set; }
    public PianoRollControl? Roll { get; set; }
    public bool PedalMode { get; set; }

    public event Action? DocumentEdited;
    public event Action? SelectionChanged;

    public void Refresh() => _sk.InvalidateVisual();

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        bool playing = Session?.IsPlaying == true;
        long position = Roll != null ? Roll.DisplayPositionTicks : (Session?.PositionTicks ?? 0);
        bool changed = position != _lastPaintPosition;
        _lastPaintPosition = position;

        // 空闲时不重复重绘力度 / 踏板条；播放或定位变化时才刷新
        if (playing || changed || _dragging) _sk.InvalidateVisual();
    }

    // ---------------- 坐标 ----------------

    private float OffsetX => Roll?.LaneOffsetX ?? 66f;
    private float TickToX(long tick) => Roll?.TimeToPixel(tick) ?? 0;
    private long XToTick(float x) => Roll?.PixelToTick(x) ?? 0;

    private static float VelocityY(float top, float bottom, int velocity) =>
        bottom - (bottom - top) * (Math.Clamp(velocity, 0, 127) / 127f);

    private static int VelocityFromY(float top, float bottom, float y)
    {
        if (bottom <= top) return 100;
        int v = (int)Math.Round((bottom - y) / (bottom - top) * 127);
        return Math.Clamp(v, 1, 127);
    }

    // ---------------- 绘制 ----------------

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width;
        int h = e.Info.Height;
        canvas.Clear(BgColor);

        var doc = _doc;
        var roll = Roll;
        if (doc == null || roll == null || w <= 0 || h <= 0) return;

        float offset = OffsetX;
        if (offset >= w) return;

        float top = 8f;
        float bottom = h - 9f;
        float usable = bottom - top;
        if (usable < 6) return;

        // 左侧标签栏
        _fillPaint.Color = PanelBg;
        canvas.DrawRect(0, 0, offset, h, _fillPaint);

        // 小节线
        double pxBar = doc.BarTicks * roll.PxPerQuarter / doc.Ppq;
        if (pxBar >= 6)
        {
            _linePaint.Color = GridColor;
            long viewStart = Math.Max(0, XToTick(offset));
            long viewEnd = XToTick(w);
            long startBar = (viewStart / doc.BarTicks) * doc.BarTicks;
            for (long t = startBar; t <= viewEnd; t += doc.BarTicks)
                canvas.DrawRect((int)TickToX(t), 0, 1, h, _linePaint);
        }

        if (PedalMode) DrawPedal(canvas, doc, w, h, top, bottom);
        else DrawVelocity(canvas, doc, roll, w, h, top, bottom);

        // 左侧文字
        _textPaint.Color = DimColor;
        canvas.DrawText(PedalMode ? "踏板" : "力度", 8, h / 2f + 3,
            SKTextAlign.Left, _fontTiny, _textPaint);

        _textPaint.Color = TextColor;
        if (PedalMode)
        {
            canvas.DrawText("ON", 8, top + 8, SKTextAlign.Left, _fontTiny, _textPaint);
            canvas.DrawText("OFF", 8, bottom, SKTextAlign.Left, _fontTiny, _textPaint);
        }
        else
        {
            canvas.DrawText("127", 8, top + 8, SKTextAlign.Left, _fontTiny, _textPaint);
            canvas.DrawText("0", 8, bottom, SKTextAlign.Left, _fontTiny, _textPaint);
        }

        // 播放头（播放时使用钢琴窗的平滑位置）
        long position = Session?.IsPlaying == true && Roll != null
            ? Roll.DisplayPositionTicks
            : (Session?.PositionTicks ?? 0);
        float px = TickToX(position);
        if (px >= offset && px <= w)
        {
            _linePaint.Color = Playhead;
            canvas.DrawRect((int)px, 0, 1, h, _linePaint);
        }
    }

    private void DrawVelocity(SKCanvas canvas, MidiDocument doc, PianoRollControl roll,
                              int w, int h, float top, float bottom)
    {
        foreach (var note in doc.Notes)
        {
            if (!roll.IsNoteVisibleInMode(note)) continue;

            float x = TickToX(note.StartTicks);
            if (x < OffsetX - 3) continue;
            if (x > w + 3) continue;

            float y = VelocityY(top, bottom, note.Velocity);
            bool hover = ReferenceEquals(note, _hoverNote);

            _fillPaint.Color = note.IsSelected
                ? Accent
                : (hover ? VelHover : VelColor);

            canvas.DrawRect(x - 1.5f, y, 3, Math.Max(1, bottom - y), _fillPaint);

            // 顶部加粗一小段，方便抓取
            _fillPaint.Color = note.IsSelected ? AccentSoft : _fillPaint.Color;
            canvas.DrawRect(x - 3, y - 1, 6, 2, _fillPaint);
        }
    }

    private void DrawPedal(SKCanvas canvas, MidiDocument doc, int w, int h, float top, float bottom)
    {
        float offset = OffsetX;
        long viewStart = Math.Max(0, XToTick(offset));
        int value = doc.GetPedalValue(viewStart);
        float prevX = offset;
        float prevY = VelocityY(top, bottom, value);

        foreach (var pedal in doc.Pedal)
        {
            if (pedal.Tick < viewStart) continue;

            float x = TickToX(pedal.Tick);
            if (x > w) break;

            if (value >= 64 && x > prevX)
            {
                _fillPaint.Color = PedalFill;
                canvas.DrawRect(prevX, top, x - prevX, bottom - top, _fillPaint);
            }

            _linePaint.Color = PedalLine;
            canvas.DrawRect(prevX, prevY, Math.Max(1, x - prevX), 1, _linePaint);
            canvas.DrawRect(x, Math.Min(prevY, VelocityY(top, bottom, pedal.Value)), 1,
                Math.Abs(VelocityY(top, bottom, pedal.Value) - prevY) + 1, _linePaint);

            _fillPaint.Color = AccentSoft;
            canvas.DrawRect(x - 2, VelocityY(top, bottom, pedal.Value) - 2, 4, 4, _fillPaint);

            prevX = x;
            prevY = VelocityY(top, bottom, pedal.Value);
            value = pedal.Value;
        }

        if (value >= 64 && w > prevX)
        {
            _fillPaint.Color = PedalFill;
            canvas.DrawRect(prevX, top, w - prevX, bottom - top, _fillPaint);
        }

        _linePaint.Color = PedalLine;
        canvas.DrawRect(prevX, prevY, Math.Max(1, w - prevX), 1, _linePaint);
    }

    // ---------------- 鼠标 ----------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        var doc = _doc;
        var roll = Roll;
        if (doc == null || roll == null) return;

        var p = e.GetPosition(_sk);
        float x = (float)p.X;
        if (x < OffsetX) return;

        if (PedalMode)
        {
            BeginPedalAdd(x);
        }
        else
        {
            var note = HitNote(x);
            if (note == null) return;

            _velocityUndoPushed = false;
            if (!note.IsSelected)
            {
                foreach (var n in doc.Notes) n.IsSelected = false;
                note.IsSelected = true;
                SelectionChanged?.Invoke();
            }

            _velocityNotes.Clear();
            foreach (var n in doc.Notes)
                if (n.IsSelected && roll.IsNoteVisibleInMode(n)) _velocityNotes.Add(n);
            if (_velocityNotes.Count == 0) _velocityNotes.Add(note);

            _dragging = true;
            _pedalDrag = false;
            _sk.CaptureMouse();
            ApplyVelocity((float)p.Y);
        }
    }

    private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        if (!PedalMode) return;

        Focus();
        var p = e.GetPosition(_sk);
        float x = (float)p.X;
        if (x < OffsetX) return;

        BeginPedalErase(x);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_sk);
        float x = (float)p.X;
        float y = (float)p.Y;

        if (!_dragging)
        {
            var note = PedalMode ? null : HitNote(x);
            if (!ReferenceEquals(note, _hoverNote))
            {
                _hoverNote = note;
                Refresh();
            }
            Cursor = PedalMode ? Cursors.Hand : (note != null ? Cursors.SizeNS : Cursors.Arrow);
            _sk.Cursor = Cursor;
            return;
        }

        if (_pedalDrag) UpdatePedalDrag(x);
        else ApplyVelocity(y);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();
    private void OnMouseRightUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging) return;

        _sk.ReleaseMouseCapture();
        _dragging = false;
        bool wasPedal = _pedalDrag;
        bool pedalChanged = _pedalChanged;

        _pedalDrag = false;
        _pedalAdd = false;
        _pedalMoved = false;
        _pedalBefore = null;
        _pedalUndoPushed = false;
        _pedalChanged = false;
        _velocityUndoPushed = false;

        if (_doc != null && wasPedal && pedalChanged)
        {
            _doc.NormalizePedal();
            _doc.MarkDirty();
            DocumentEdited?.Invoke();
        }
        Refresh();
    }

    // ---------------- 力度 ----------------

    private EditorNote? HitNote(float x)
    {
        var doc = _doc;
        var roll = Roll;
        if (doc == null || roll == null) return null;

        EditorNote? best = null;
        float bestDistance = 12f;

        foreach (var note in doc.Notes)
        {
            if (!roll.IsNoteVisibleInMode(note)) continue;

            float distance = Math.Abs(TickToX(note.StartTicks) - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = note;
            }
        }
        return best;
    }

    private void ApplyVelocity(float y)
    {
        var doc = _doc;
        if (doc == null || _velocityNotes.Count == 0) return;

        int value = VelocityFromY(8f, (float)_sk.ActualHeight - 9f, y);
        if (_velocityNotes.All(n => n.Velocity == value)) return;

        if (!_velocityUndoPushed)
        {
            doc.PushUndo();
            _velocityUndoPushed = true;
        }

        foreach (var note in _velocityNotes) note.Velocity = value;

        Roll?.RememberNoteTemplate(_velocityNotes[0]);
        doc.MarkDirty();
        DocumentEdited?.Invoke();
        Refresh();
    }

    // ---------------- 踏板 ----------------

    /// <summary>左键：从点击处开始添加踏板；拖动则形成一段踩下区域</summary>
    private void BeginPedalAdd(float x)
    {
        var doc = _doc;
        if (doc == null) return;

        long tick = Math.Max(0, XToTick(x));
        _pedalBefore = SnapshotPedal(doc);
        _pedalStart = tick;
        _pedalStartX = x;
        _pedalMoved = false;
        _pedalAdd = true;
        _pedalDrag = true;
        _dragging = true;
        _pedalUndoPushed = false;
        _pedalChanged = false;

        // 单击：在点击处插一个“踩下”点
        if (ValueAt(_pedalBefore, tick) < 64)
        {
            EnsurePedalUndo();
            doc.AddPedalPoint(tick, 127);
            doc.MarkDirty();
            _pedalChanged = true;
            DocumentEdited?.Invoke();
        }

        _sk.CaptureMouse();
        Refresh();
    }

    /// <summary>右键：从点击处开始擦除踏板；拖动则擦掉一段踩下区域</summary>
    private void BeginPedalErase(float x)
    {
        var doc = _doc;
        if (doc == null) return;

        long tick = Math.Max(0, XToTick(x));
        _pedalBefore = SnapshotPedal(doc);
        _pedalStart = tick;
        _pedalStartX = x;
        _pedalMoved = false;
        _pedalAdd = false;
        _pedalDrag = true;
        _dragging = true;
        _pedalUndoPushed = false;
        _pedalChanged = false;

        bool onEvent = _pedalBefore.Any(p => p.Tick == tick && p.Value >= 64);
        bool hasEvent = _pedalBefore.Any(p => p.Tick == tick);
        bool isOn = ValueAt(_pedalBefore, tick) >= 64;

        // 只有“当前是踩下状态”或“点击处正好有一个踩下事件”才需要擦除；
        // 点在已经是 OFF 的边界上不会产生无意义的修改。
        if (onEvent || (isOn && !hasEvent))
        {
            EnsurePedalUndo();
            doc.Pedal.RemoveAll(p => p.Tick == tick);
            doc.AddPedalPoint(tick, 0);
            doc.NormalizePedal();
            doc.MarkDirty();
            _pedalChanged = true;
            DocumentEdited?.Invoke();
        }

        _sk.CaptureMouse();
        Refresh();
    }

    private void UpdatePedalDrag(float x)
    {
        var doc = _doc;
        if (doc == null || _pedalBefore == null) return;

        if (!_pedalMoved)
        {
            if (Math.Abs(x - _pedalStartX) < 3f) return;
            _pedalMoved = true;
        }

        long tick = Math.Max(0, XToTick(x));
        long lo = Math.Min(_pedalStart, tick);
        long hi = Math.Max(_pedalStart, tick);

        EnsurePedalUndo();

        // 拖动终点原本的踏板状态：松手后恢复，避免把后面的踏板一起改掉
        int valueAtEnd = ValueAt(_pedalBefore, hi);

        doc.Pedal.Clear();
        foreach (var pedal in _pedalBefore)
        {
            if (pedal.Tick < lo || pedal.Tick > hi)
                doc.Pedal.Add(new PedalEvent { Tick = pedal.Tick, Value = pedal.Value });
        }

        doc.AddPedalPoint(lo, _pedalAdd ? 127 : 0);
        if (hi > lo) doc.AddPedalPoint(hi, valueAtEnd);

        doc.NormalizePedal();
        doc.MarkDirty();
        _pedalChanged = true;
        DocumentEdited?.Invoke();
        Refresh();
    }

    private void EnsurePedalUndo()
    {
        if (_pedalUndoPushed || _doc == null) return;
        _doc.PushUndo();
        _pedalUndoPushed = true;
    }

    private static List<PedalEvent> SnapshotPedal(MidiDocument doc)
    {
        var snapshot = doc.Pedal
            .Select(p => new PedalEvent { Tick = p.Tick, Value = p.Value })
            .ToList();
        snapshot.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        return snapshot;
    }

    private static int ValueAt(List<PedalEvent> events, long tick)
    {
        int value = 0;
        foreach (var pedal in events)
        {
            if (pedal.Tick > tick) break;
            value = pedal.Value;
        }
        return value;
    }
}
