using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Threading;
using MidiPlayer.Services;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using Color = System.Windows.Media.Color;

namespace MidiPlayer.Controls;

public abstract class RollControlBase : ContentControl
{
    protected readonly SKElement Sk = new() { IgnorePixelScaling = true };
    private readonly DispatcherTimer _timer;

    public IReadOnlyList<NoteInfo> Notes { get; set; } = Array.Empty<NoteInfo>();
    public IReadOnlyList<BeatLine> BeatLines { get; set; } = Array.Empty<BeatLine>();
    public Func<TimeSpan>? CurrentTimeProvider { get; set; }

    // 可配置颜色
    public Color BackgroundColor     { get; set; } = Color.FromRgb(0x0A, 0x0E, 0x14);
    public Color NoteInactiveColor   { get; set; } = Color.FromArgb(60, 0xFF, 0xFF, 0xFF);
    public Color NoteActiveColor     { get; set; } = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    public Color PlayheadColor       { get; set; } = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
public Color GridWeakColor       { get; set; } = Color.FromArgb(40, 0xFF, 0xFF, 0xFF);
public Color GridStrongColor     { get; set; } = Color.FromArgb(90, 0xFF, 0xFF, 0xFF);

// 是否显示网格线
public bool ShowGrid { get; set; } = true;
    protected RollControlBase()
    {
        Content = Sk;
        Sk.PaintSurface += (_, e) => OnPaint(e);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Sk.InvalidateVisual();
        _timer.Start();
    }

    protected abstract void OnPaint(SKPaintSurfaceEventArgs e);

    protected static SKColor ToSK(Color c) => new(c.R, c.G, c.B, c.A);

    protected static SKColor Lerp(Color a, Color b, float t)
    {
        byte r  = (byte)(a.R  + (b.R  - a.R)  * t);
        byte g  = (byte)(a.G  + (b.G  - a.G)  * t);
        byte bl = (byte)(a.B  + (b.B  - a.B)  * t);
        byte al = (byte)(a.A  + (b.A  - a.A)  * t);
        return new SKColor(r, g, bl, al);
    }
}