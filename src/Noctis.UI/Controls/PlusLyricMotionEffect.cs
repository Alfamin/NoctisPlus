using System;
using System.Collections.Generic;
using Noctis.Helpers;

namespace Noctis.Controls;

/// <summary>
/// Phone-sized native version of Noctis Plus Lyric Motion. It reuses the existing
/// KaraokeWordText renderer and supplies only deterministic, allocation-free poses.
/// Motion values are adapted from moshi/Lyric Motion 1.4.3 (MIT); desktop rendering is
/// intentionally independent and remains in the desktop app.
/// </summary>
public sealed class PlusLyricMotionEffect : IWordEffect
{
    private const int MaxLetters = 14;
    private const double HeldMs = 600;
    private const double SmoothTail = 0.5;
    private const double SettleStart = 1.1;
    private const double SettleSpread = 0.3;
    private const double SettleLength = 0.55;

    public WordDrawMode Mode(in WordPoseInput word)
    {
        if (word.Surface != WordSurface.Page) return WordDrawMode.Native;
        var count = word.LetterCount;
        if (!word.IsRightToLeft && count is > 1 and <= MaxLetters && Grow(word.DurationMs, word.IsLastInLine) >= 0.004)
            return WordDrawMode.Letters;
        return WordDrawMode.Whole;
    }

    public LetterPose Pose(in WordPoseInput word, int letter)
    {
        var duration = double.IsFinite(word.DurationMs) ? Math.Max(0, word.DurationMs) : 0;
        var progress = word.Progress;
        var lift = PlusMotionCurves.Clamp01(word.Lift);
        var floatEm = -0.05 * lift;
        var held = duration >= HeldMs;
        var glowPeak = HeldGlow(duration, word.IsLastInLine);
        if (!double.IsFinite(progress)) return new LetterPose(1, 0, floatEm, glowPeak);

        double grow;
        int count;
        if (Mode(word) == WordDrawMode.Letters)
        {
            count = Math.Max(1, word.LetterCount);
            letter = Math.Clamp(letter, 0, count - 1);
            grow = Grow(duration, word.IsLastInLine)
                   * Arrival(progress, letter, count)
                   * (1 - Settle(progress, letter, count));
        }
        else
        {
            count = 1;
            letter = 0;
            if (held)
                grow = HeldGrow(duration, word.IsLastInLine) * 0.7
                       * Arrival(progress, 0, 1) * (1 - Settle(progress, 0, 1));
            else
                grow = WordGrow(duration) * (progress is > -1.5 and < 2.5
                    ? PlusMotionCurves.Hill(progress / (1 + SmoothTail)) : 0);
        }

        var centre = (count - 1) / 2.0;
        var x = (letter - centre) * grow * 0.28;
        var y = floatEm - 0.5 * grow;
        var settle = Settle(progress, letter, count);
        var glow = glowPeak <= 0 ? 0 : glowPeak * (held ? Math.Max(0.35, 1 - settle) : 0);
        return new LetterPose(1 + grow, x, y, glow);
    }

    public void LineDirections(IReadOnlyList<string> lines, Span<bool> rightToLeft)
    {
        // LyricsTimeline already resolves direction for phone rows; this effect does not alter it.
    }

    private static double Grow(double duration, bool last) => duration >= HeldMs
        ? HeldGrow(duration, last) : WordGrow(duration);

    private static double WordGrow(double duration) => 0.018 * PlusMotionCurves.SmoothStep(160, 420, duration);

    private static double HeldGrow(double duration, bool last)
    {
        var t = PlusMotionCurves.SmoothStep(HeldMs, HeldMs + 1800, duration);
        var grow = (0.03 + (0.06 - 0.03) * t) * (last ? 1.1 : 1);
        return Math.Min(grow, 0.10);
    }

    private static double HeldGlow(double duration, bool last)
    {
        var t = PlusMotionCurves.SmoothStep(HeldMs, HeldMs + 2000, duration);
        var glow = (0.2 + (0.5 - 0.2) * t) * (last ? 1.1 : 1);
        return Math.Min(glow, 0.8);
    }

    private static double Arrival(double progress, int letter, int count)
    {
        if (progress <= -1.5) return 0;
        if (progress >= 2.5) return 1;
        count = Math.Max(1, count);
        var start = (double)letter / count;
        var width = Math.Min(1.0 / count + SmoothTail,
            SettleStart + SettleSpread * letter / count - start);
        return PlusMotionCurves.SmootherStep((progress - start) / width);
    }

    private static double Settle(double progress, int letter, int count)
    {
        if (progress <= -1.5) return 0;
        if (progress >= 2.5) return 1;
        count = Math.Max(1, count);
        var from = SettleStart + SettleSpread * letter / count;
        return PlusMotionCurves.SmootherStep((progress - from) / SettleLength);
    }
}
