using System;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace MidiPlayer.Controls;

public class HorizontalRollControl : RollControlBase
{
    private const int LowNote = 21;
    private const int HighNote = 108;
    private const int NoteRange = HighNote - LowNote + 1;
    private const float TopPad = 24;
    private const float BottomPad = 24;
    private const float VisibleSecondsRight = 1.0f;

    private const float HoldDuration = 0.5f;
    private const float FadeDuration = 0.5f;

    private readonly SKPaint _playheadPaint  = new();
    private readonly SKPaint _notePaint      = new() { IsAntialias = true };
    private readonly SKPaint _gridWeakPaint  = new();
    private readonly SKPaint _gridStrongPaint = new();

protected override void OnPaint(SKPaintSurfaceEventArgs e)
{
    var canvas = e.Surface.Canvas;
    int w = e.Info.Width;
    int h = e.Info.Height;
    canvas.Clear(ToSK(BackgroundColor));

    if (w <= 0 || h <= 0) return;

    float playheadX = w * 0.5f;
    float pps = playheadX / VisibleSecondsRight;
    float laneH = (h - TopPad - BottomPad) / NoteRange;

    var now = CurrentTimeProvider?.Invoke() ?? TimeSpan.Zero;
    float nowSec = (float)now.TotalSeconds;

// 1. 网格线
if (ShowGrid)
{
    _gridWeakPaint.Color   = ToSK(GridWeakColor);
    _gridStrongPaint.Color = ToSK(GridStrongColor);

    foreach (var beat in BeatLines)
    {
        float x = playheadX + ((float)beat.Time.TotalSeconds - nowSec) * pps;
        if (x < 0) continue;
        if (x > w) break;

        int xi = (int)MathF.Round(x);
        if (xi < 0 || xi > w) continue;

        canvas.DrawRect(xi, 0, 1, h,
            beat.IsMeasureStart ? _gridStrongPaint : _gridWeakPaint);
    }
}

    // 2. 音符
    foreach (var note in Notes)
    {
        float startSec = (float)note.Start.TotalSeconds;
        float endSec = startSec + (float)note.Duration.TotalSeconds;

        float xStart = playheadX + (startSec - nowSec) * pps;
        float xEnd   = playheadX + (endSec   - nowSec) * pps;

        if (xEnd < 0) continue;
        if (xStart > w) continue;

        float xLeft  = Math.Max(xStart, 0);
        float xRight = Math.Min(xEnd, w);
        if (xRight - xLeft < 1) continue;

        int i = HighNote - note.NoteNumber;
        float y = TopPad + i * laneH;

        _notePaint.Color = ComputeNoteColor(nowSec, startSec);
        canvas.DrawRect(xLeft, y + 1, xRight - xLeft, laneH - 2, _notePaint);
    }

    // 3. 判定线
    _playheadPaint.Color = ToSK(PlayheadColor);
    canvas.DrawRect((int)playheadX - 1, 0, 2, h, _playheadPaint);
}

    private SKColor ComputeNoteColor(float nowSec, float startSec)
    {
        float dt = nowSec - startSec;
        if (dt < 0) return ToSK(NoteInactiveColor);
        if (dt < HoldDuration) return ToSK(NoteActiveColor);
        if (dt < HoldDuration + FadeDuration)
        {
            float t = (dt - HoldDuration) / FadeDuration;
            return Lerp(NoteActiveColor, NoteInactiveColor, t);
        }
        return ToSK(NoteInactiveColor);
    }
}