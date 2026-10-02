using System;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace MidiPlayer.Controls;

public class VerticalRollControl : RollControlBase
{
    private const int LowNote = 21;
    private const int HighNote = 108;
    private const int KeyCount = HighNote - LowNote + 1;
    private const float TopPad = 16;
    private const float KeyboardHeight = 50;
    private const float VisibleSeconds = 0.8f;

    private const float HoldDuration = 0.5f;
    private const float FadeDuration = 0.5f;

    private static readonly SKColor WhiteKeyColor  = new(0xB8, 0xC0, 0xCC);
    private static readonly SKColor BlackKeyColor  = new(0x14, 0x18, 0x1F);
    private static readonly SKColor ActiveKeyColor = new(0x3B, 0x82, 0xF6);

    private readonly SKPaint _playheadPaint  = new();
    private readonly SKPaint _notePaint      = new() { IsAntialias = true };
    private readonly SKPaint _gridWeakPaint  = new();
    private readonly SKPaint _gridStrongPaint = new();
    private readonly SKPaint _whiteKeyPaint  = new() { Color = WhiteKeyColor };
    private readonly SKPaint _blackKeyPaint  = new() { Color = BlackKeyColor };
    private readonly SKPaint _activeKeyPaint = new() { Color = ActiveKeyColor };

    private readonly bool[] _activeKeys = new bool[KeyCount];

protected override void OnPaint(SKPaintSurfaceEventArgs e)
{
    var canvas = e.Surface.Canvas;
    int w = e.Info.Width;
    int h = e.Info.Height;
    canvas.Clear(ToSK(BackgroundColor));

    if (w <= 0 || h <= 0) return;

    float keyW = (float)w / KeyCount;
    float playheadY = h - KeyboardHeight - 4;
    float pps = (playheadY - TopPad) / VisibleSeconds;

    var now = CurrentTimeProvider?.Invoke() ?? TimeSpan.Zero;
    float nowSec = (float)now.TotalSeconds;

// 1. 网格线
if (ShowGrid)
{
    _gridWeakPaint.Color   = ToSK(GridWeakColor);
    _gridStrongPaint.Color = ToSK(GridStrongColor);

    foreach (var beat in BeatLines)
    {
        float y = playheadY - ((float)beat.Time.TotalSeconds - nowSec) * pps;
        if (y < TopPad) break;
        if (y > playheadY) continue;

        int yi = (int)MathF.Round(y);
        if (yi < (int)TopPad || yi > (int)playheadY) continue;

        canvas.DrawRect(0, yi, w, 1,
            beat.IsMeasureStart ? _gridStrongPaint : _gridWeakPaint);
    }
}

    // 2. 音符
    Array.Clear(_activeKeys, 0, _activeKeys.Length);

    foreach (var note in Notes)
    {
        float startSec = (float)note.Start.TotalSeconds;
        float endSec = startSec + (float)note.Duration.TotalSeconds;

        if (nowSec >= startSec && nowSec <= endSec)
        {
            int idx = note.NoteNumber - LowNote;
            if (idx >= 0 && idx < KeyCount) _activeKeys[idx] = true;
        }

        float startY = playheadY - (startSec - nowSec) * pps;
        float endY   = playheadY - (endSec   - nowSec) * pps;

        if (endY > playheadY) continue;
        if (startY < TopPad) continue;

        float rectTop    = Math.Max(endY, TopPad);
        float rectBottom = Math.Min(startY, playheadY);
        if (rectBottom - rectTop < 1) continue;

        int i = note.NoteNumber - LowNote;
        if (i < 0 || i >= KeyCount) continue;
        float x = i * keyW;

        _notePaint.Color = ComputeNoteColor(nowSec, startSec);
        canvas.DrawRect(x + 1, rectTop, keyW - 2, rectBottom - rectTop, _notePaint);
    }

    // 3. 判定线
    _playheadPaint.Color = ToSK(PlayheadColor);
    canvas.DrawRect(0, (int)playheadY, w, 1, _playheadPaint);

    // 4. 键盘
    float kbTop = h - KeyboardHeight;
    for (int i = 0; i < KeyCount; i++)
    {
        int note = LowNote + i;
        float x = i * keyW;
        SKPaint paint = _activeKeys[i]
            ? _activeKeyPaint
            : (IsBlackKey(note) ? _blackKeyPaint : _whiteKeyPaint);
        canvas.DrawRect(x, kbTop, keyW, KeyboardHeight, paint);
    }
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

    private static bool IsBlackKey(int note)
    {
        int n = note % 12;
        return n == 1 || n == 3 || n == 6 || n == 8 || n == 10;
    }
}