using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using MediaColor = System.Windows.Media.Color;

namespace MidiPlayer.Controls;

public partial class ColorPickerWindow : Window
{
    private float _hue;        // 0-360
    private float _sat;        // 0-1
    private float _val = 1f;   // 0-1

    private bool _dragWheel;
    private bool _dragSv;
    private bool _dragH;
    private bool _dragS;
    private bool _dragV;

private const float WheelSize   = 300f;
private const float OuterR      = 128f;   // 142 → 128，外径缩小
private const float InnerR      = 96f;    // 100 → 96，内径微调
private const float SquareSize  = 128f;   // 156 → 128，正方形缩小到内切于内环


    private SKBitmap? _squareBmp;
    private float _squareCachedHue = -1f;

    public MediaColor SelectedColor { get; private set; }

    private static readonly string[] Presets =
    {
        "#FFFFFF", "#E4E4E7", "#A1A1AA", "#52525B", "#27272A", "#0A0E14",
        "#3B82F6", "#60A5FA", "#22D3EE", "#10B981", "#F59E0B", "#EF4444",
        "#8B5CF6", "#EC4899", "#F472B6", "#84CC16", "#FACC15", "#FB923C"
    };

    private readonly SKPaint _bmpPaint = new();

    public ColorPickerWindow(MediaColor initial)
    {
        InitializeComponent();
        SelectedColor = initial;

        RgbToHsv(initial.R / 255f, initial.G / 255f, initial.B / 255f,
                 out _hue, out _sat, out _val);

        // 标题栏拖动
        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        // 色轮
        WheelCanvas.PaintSurface += Wheel_PaintSurface;
        WheelCanvas.MouseDown    += Wheel_MouseDown;
        WheelCanvas.MouseMove    += Wheel_MouseMove;
        WheelCanvas.MouseUp      += Wheel_MouseUp;

        // H 滑条
        HueCanvas.PaintSurface += Hue_PaintSurface;
        HueCanvas.MouseDown += (_, e) => { _dragH = true; HueCanvas.CaptureMouse(); ApplyHuePoint(e.GetPosition(HueCanvas)); };
        HueCanvas.MouseMove += (_, e) => { if (_dragH) ApplyHuePoint(e.GetPosition(HueCanvas)); };
        HueCanvas.MouseUp   += (_, _) => { _dragH = false; HueCanvas.ReleaseMouseCapture(); };

        // S 滑条
        SatCanvas.PaintSurface += Sat_PaintSurface;
        SatCanvas.MouseDown += (_, e) => { _dragS = true; SatCanvas.CaptureMouse(); ApplySatPoint(e.GetPosition(SatCanvas)); };
        SatCanvas.MouseMove += (_, e) => { if (_dragS) ApplySatPoint(e.GetPosition(SatCanvas)); };
        SatCanvas.MouseUp   += (_, _) => { _dragS = false; SatCanvas.ReleaseMouseCapture(); };

        // V 滑条
        ValCanvas.PaintSurface += Val_PaintSurface;
        ValCanvas.MouseDown += (_, e) => { _dragV = true; ValCanvas.CaptureMouse(); ApplyValPoint(e.GetPosition(ValCanvas)); };
        ValCanvas.MouseMove += (_, e) => { if (_dragV) ApplyValPoint(e.GetPosition(ValCanvas)); };
        ValCanvas.MouseUp   += (_, _) => { _dragV = false; ValCanvas.ReleaseMouseCapture(); };

        // Hex
        HexBox.KeyDown   += (_, e) => { if (e.Key == Key.Enter) UpdateFromHex(); };
        HexBox.LostFocus += (_, _) => UpdateFromHex();

        // 按钮
        BtnClose.Click  += (_, _) => { DialogResult = false; Close(); };
        BtnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        BtnOk.Click     += (_, _) => { DialogResult = true;  Close(); };

        BuildPresets();
        UpdateAllUI();
    }

    // ---------------- 色轮 ----------------

    private void Wheel_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width, h = e.Info.Height;
        canvas.Clear(SKColors.Transparent);

        float cx = w / 2f, cy = h / 2f;

        // 1. 色相环
        DrawHueRing(canvas, cx, cy);

        // 2. 内部 SV 正方形
        float sqLeft = cx - SquareSize / 2f;
        float sqTop  = cy - SquareSize / 2f;
        DrawSvSquare(canvas, sqLeft, sqTop);

        // 3. 色相手柄（环上）
        double rad = _hue * Math.PI / 180.0;
        float ringMid = (OuterR + InnerR) / 2f;
        float hx = cx + (float)Math.Cos(rad) * ringMid;
        float hy = cy + (float)Math.Sin(rad) * ringMid;
        DrawRingHandle(canvas, hx, hy, 10);

        // 4. SV 手柄（正方形上）
        float sx = sqLeft + _sat * SquareSize;
        float sy = sqTop + (1f - _val) * SquareSize;
        DrawRingHandle(canvas, sx, sy, 7);
    }

private void DrawHueRing(SKCanvas canvas, float cx, float cy)
{
    using var paint = new SKPaint
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill
    };

    float outer = OuterR;
    float inner = InnerR;
    const int segments = 360;

    for (int i = 0; i < segments; i++)
    {
        float a1 = i * MathF.PI * 2f / segments;
        float a2 = (i + 1) * MathF.PI * 2f / segments;
        // 加一点点重叠，避免相邻段之间出现缝隙
        float a2Overlap = a2 + MathF.PI * 2f / segments * 0.5f;

        float hue = i * 360f / segments;
        paint.Color = SKColor.FromHsv(hue, 100, 100);

        using var path = new SKPath();
        path.MoveTo(cx + MathF.Cos(a1) * inner, cy + MathF.Sin(a1) * inner);
        path.LineTo(cx + MathF.Cos(a1) * outer, cy + MathF.Sin(a1) * outer);
        path.LineTo(cx + MathF.Cos(a2Overlap) * outer, cy + MathF.Sin(a2Overlap) * outer);
        path.LineTo(cx + MathF.Cos(a2Overlap) * inner, cy + MathF.Sin(a2Overlap) * inner);
        path.Close();

        canvas.DrawPath(path, paint);
    }
}

    private void DrawSvSquare(SKCanvas canvas, float left, float top)
    {
        int size = (int)SquareSize;
        if (_squareBmp == null || _squareBmp.Width != size
            || Math.Abs(_squareCachedHue - _hue) > 0.01f)
        {
            _squareBmp?.Dispose();
            _squareBmp = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
            for (int y = 0; y < size; y++)
            {
                float v = 1f - (float)y / (size - 1);
                for (int x = 0; x < size; x++)
                {
                    float s = (float)x / (size - 1);
                    _squareBmp.SetPixel(x, y, SKColor.FromHsv(_hue, s * 100f, v * 100f));
                }
            }
            _squareCachedHue = _hue;
        }
        canvas.DrawBitmap(_squareBmp, left, top, _bmpPaint);
    }

    private static void DrawRingHandle(SKCanvas canvas, float x, float y, float r)
    {
        using var outer = new SKPaint
        {
            Color = SKColors.White, IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 2
        };
        using var inner = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 100), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1
        };
        canvas.DrawCircle(x, y, r, outer);
        canvas.DrawCircle(x, y, r + 1.5f, inner);
    }

    // ---------------- 色轮鼠标 ----------------

    private void Wheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(WheelCanvas);
        float cx = (float)WheelCanvas.ActualWidth / 2f;
        float cy = (float)WheelCanvas.ActualHeight / 2f;
        float dx = (float)p.X - cx, dy = (float)p.Y - cy;
        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
        float sqLeft = cx - SquareSize / 2f;
        float sqTop  = cy - SquareSize / 2f;

        if (dist >= InnerR - 6 && dist <= OuterR + 6)
        {
            _dragWheel = true;
            WheelCanvas.CaptureMouse();
            ApplyRingPoint(dx, dy);
        }
        else if (p.X >= sqLeft && p.X <= sqLeft + SquareSize
              && p.Y >= sqTop  && p.Y <= sqTop  + SquareSize)
        {
            _dragSv = true;
            WheelCanvas.CaptureMouse();
            ApplySvPoint(p, sqLeft, sqTop);
        }
    }

    private void Wheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragWheel && !_dragSv) return;
        var p = e.GetPosition(WheelCanvas);
        float cx = (float)WheelCanvas.ActualWidth / 2f;
        float cy = (float)WheelCanvas.ActualHeight / 2f;
        float dx = (float)p.X - cx, dy = (float)p.Y - cy;
        float sqLeft = cx - SquareSize / 2f;
        float sqTop  = cy - SquareSize / 2f;

        if (_dragWheel) ApplyRingPoint(dx, dy);
        else if (_dragSv) ApplySvPoint(p, sqLeft, sqTop);
    }

    private void Wheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragWheel = false;
        _dragSv = false;
        WheelCanvas.ReleaseMouseCapture();
    }

    private void ApplyRingPoint(float dx, float dy)
    {
        _hue = (float)((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360);
        UpdateAllUI();
    }

    private void ApplySvPoint(Point p, float sqLeft, float sqTop)
    {
        _sat = Math.Clamp((float)(p.X - sqLeft) / SquareSize, 0f, 1f);
        _val = Math.Clamp(1f - (float)(p.Y - sqTop) / SquareSize, 0f, 1f);
        UpdateAllUI();
    }

    // ---------------- 三个滑条 ----------------

    private void Hue_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var colors = new SKColor[7];
        for (int i = 0; i < 7; i++) colors[i] = SKColor.FromHsv(i * 60f, 100, 100);
        DrawBar(e, colors, _hue / 360f);
    }

    private void Sat_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var colors = new[]
        {
            SKColor.FromHsv(_hue, 0, _val * 100f),
            SKColor.FromHsv(_hue, 100, _val * 100f)
        };
        DrawBar(e, colors, _sat);
    }

    private void Val_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var colors = new[]
        {
            SKColor.FromHsv(_hue, _sat * 100f, 0),
            SKColor.FromHsv(_hue, _sat * 100f, 100)
        };
        DrawBar(e, colors, _val);
    }

    private void DrawBar(SKPaintSurfaceEventArgs e, SKColor[] colors, float t)
    {
        var canvas = e.Surface.Canvas;
        int w = e.Info.Width, h = e.Info.Height;
        canvas.Clear(SKColors.Transparent);
        if (w <= 0 || h <= 0) return;

        float radius = h / 2f;
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, h / 2f), new SKPoint(w, h / 2f),
            colors, null, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        var rect = new SKRoundRect(new SKRect(0, 0, w, h), radius);
        canvas.DrawRoundRect(rect, paint);

        float hx = Math.Clamp(t * w, radius, w - radius);
        using var outer = new SKPaint
        {
            Color = SKColors.White, IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 2
        };
        using var inner = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 80), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1
        };
        canvas.DrawCircle(hx, h / 2f, radius + 1, outer);
        canvas.DrawCircle(hx, h / 2f, radius + 2, inner);
    }

    private void ApplyHuePoint(Point p) { _hue = Math.Clamp((float)p.X / 300f, 0f, 1f) * 360f; UpdateAllUI(); }
    private void ApplySatPoint(Point p) { _sat = Math.Clamp((float)p.X / Math.Max(1, (float)SatCanvas.ActualWidth), 0f, 1f); UpdateAllUI(); }
    private void ApplyValPoint(Point p) { _val = Math.Clamp((float)p.X / Math.Max(1, (float)ValCanvas.ActualWidth), 0f, 1f); UpdateAllUI(); }

    // ---------------- Hex ----------------

    private void UpdateFromHex()
    {
        var s = HexBox.Text.Trim().TrimStart('#');
        if (s.Length == 3)
            s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
        if (s.Length != 6) return;
        try
        {
            int r = Convert.ToInt32(s.Substring(0, 2), 16);
            int g = Convert.ToInt32(s.Substring(2, 2), 16);
            int b = Convert.ToInt32(s.Substring(4, 2), 16);
            RgbToHsv(r / 255f, g / 255f, b / 255f, out _hue, out _sat, out _val);
            UpdateAllUI();
        }
        catch { }
    }

    // ---------------- 预设 ----------------

    private void BuildPresets()
    {
        foreach (var hex in Presets)
        {
            var c = ParseHex(hex);
            var bd = new Border
            {
                Width = 24, Height = 24,
                Margin = new Thickness(0, 0, 4, 4),
                Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x50, 0x50, 0x55)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            bd.MouseLeftButtonDown += (_, _) =>
            {
                RgbToHsv(c.R / 255f, c.G / 255f, c.B / 255f, out _hue, out _sat, out _val);
                UpdateAllUI();
            };
            PresetPanel.Children.Add(bd);
        }
    }

    private static MediaColor ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return MediaColor.FromRgb(
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    // ---------------- 通用 ----------------

    private void UpdateAllUI()
    {
        var sk = SKColor.FromHsv(_hue, _sat * 100f, _val * 100f);
        SelectedColor = MediaColor.FromArgb(255, sk.Red, sk.Green, sk.Blue);

        PreviewBorder.Background = new SolidColorBrush(SelectedColor);

HexBox.Text = $"#{sk.Red:X2}{sk.Green:X2}{sk.Blue:X2}";

        LblH.Text = ((int)Math.Round(_hue)).ToString();
        LblS.Text = ((int)Math.Round(_sat * 100f)).ToString();
        LblV.Text = ((int)Math.Round(_val * 100f)).ToString();

        WheelCanvas.InvalidateVisual();
        HueCanvas.InvalidateVisual();
        SatCanvas.InvalidateVisual();
        ValCanvas.InvalidateVisual();
    }

    private static void RgbToHsv(float r, float g, float b, out float h, out float s, out float v)
    {
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        if (max == r)      h = 60f * (((g - b) / d) % 6f);
        else if (max == g) h = 60f * ((b - r) / d + 2f);
        else               h = 60f * ((r - g) / d + 4f);
        if (h < 0) h += 360f;
    }
}