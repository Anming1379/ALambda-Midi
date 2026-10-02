using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace MidiPlayer.Controls;

public class ProgressBarControl : ContentControl
{
    private readonly SKElement _sk = new() { IgnorePixelScaling = true };
    private readonly DispatcherTimer _timer;

    private static readonly SKColor TrackColor  = new(0x27, 0x27, 0x2A);
    private static readonly SKColor FillColor   = new(0x3B, 0x82, 0xF6);
    private static readonly SKColor HoverColor  = new(0x60, 0xA5, 0xFA);
    private static readonly SKColor HandleColor = new(0xFA, 0xFA, 0xFA);

    private TimeSpan _total;
    private TimeSpan _current;
    private bool _dragging;
    private bool _hover;

    public event Action<TimeSpan>? SeekRequested;

    public ProgressBarControl()
    {
        MinHeight = 16; 
        Content = _sk;
        _sk.PaintSurface += OnPaint;
        _sk.MouseEnter += (_, _) => { _hover = true; _sk.InvalidateVisual(); };
        _sk.MouseLeave += (_, _) => { _hover = false; _sk.InvalidateVisual(); };
        _sk.MouseLeftButtonDown += OnMouseDown;
        _sk.MouseMove += OnMouseMove;
        _sk.MouseLeftButtonUp += OnMouseUp;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => _sk.InvalidateVisual();
        _timer.Start();
    }

    public void SetProgress(TimeSpan current, TimeSpan total)
    {
        _current = current;
        _total = total;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_total <= TimeSpan.Zero) return;
        _dragging = true;
        _sk.CaptureMouse();
        EmitSeek(e.GetPosition(_sk));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging) EmitSeek(e.GetPosition(_sk));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _sk.ReleaseMouseCapture();
    }

    private void EmitSeek(System.Windows.Point p)
    {
        float w = (float)_sk.ActualWidth;
        if (w <= 0) return;
        float ratio = Math.Clamp((float)p.X / w, 0f, 1f);
        SeekRequested?.Invoke(TimeSpan.FromTicks((long)(_total.Ticks * ratio)));
    }

private void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
{
    var canvas = e.Surface.Canvas;
    int w = e.Info.Width;
    int h = e.Info.Height;
    canvas.Clear(SKColors.Transparent);
    if (w <= 0 || h <= 0 || _total <= TimeSpan.Zero) return;

    float centerY = h / 2f;
    float trackH = _hover || _dragging ? 4f : 2f;   // 悬停/拖动时变粗一点

    float ratio = Math.Clamp((float)(_current.Ticks / (double)_total.Ticks), 0f, 1f);
    float filledW = w * ratio;

    using var trackPaint = new SKPaint { Color = TrackColor, IsAntialias = false };
    using var fillPaint  = new SKPaint { Color = _hover || _dragging ? HoverColor : FillColor, IsAntialias = false };

    canvas.DrawRect(0, centerY - trackH / 2f, w, trackH, trackPaint);
    if (filledW > 0)
        canvas.DrawRect(0, centerY - trackH / 2f, filledW, trackH, fillPaint);

    // 当前位置：一根细竖线，不是圆点
    if (_hover || _dragging)
    {
        using var handlePaint = new SKPaint { Color = HandleColor, IsAntialias = false };
        canvas.DrawRect(filledW - 1, centerY - 6, 2, 12, handlePaint);
    }
}
}