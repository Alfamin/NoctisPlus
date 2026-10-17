using System;

namespace Noctis.Controls;

/// <summary>
/// The karaoke sweep's soft edge, shared by <see cref="KaraokeWordText"/> and the
/// gradient-foreground converter: a constant-width band whose centre is the sweep front,
/// fully lit behind it and unsung ahead of it. All positions are fractions of the word's
/// glyph width.
/// </summary>
public static class KaraokeBand
{
    /// <summary>Half-width of the band when no layout metrics are known.</summary>
    public const double FallbackFeather = 0.06;

    /// <summary>
    /// Half-width of the band as a fraction of the word: a fixed ~0.25 em (AMLL's fade is
    /// a fixed fraction of the line height, not of the word), bounded so wide melisma words
    /// keep a visible soft edge and narrow single-character cells stay inside the
    /// sentinel contract.
    /// </summary>
    public static double Feather(double glyphWidth, double fontSize) =>
        glyphWidth > 0 && fontSize > 0 ? Math.Clamp(0.25 * fontSize / glyphWidth, 0.04, 0.45) : FallbackFeather;

    /// <summary>
    /// Band centre for a front at <paramref name="progress"/> through the word. Next to a
    /// rest the front has nothing to carry it in or out, so inside the word's own window
    /// (0..1) the band is stretched to start fully before the word (<paramref name="leadIn"/>)
    /// and/or end fully past it (<paramref name="trailOut"/>) — AMLL moves the first word's
    /// mask an extra 1.5 fades and the last word's 0.5. Values outside 0..1 come from a
    /// neighbouring word's front and pass through.
    /// </summary>
    public static double Center(double progress, double feather, bool leadIn, bool trailOut)
    {
        if (progress < 0 || progress > 1) return progress;
        var lead = leadIn ? feather : 0;
        var trail = trailOut ? feather : 0;
        return -lead + (1 + lead + trail) * progress;
    }

    /// <summary>How lit point <paramref name="x"/> is (1 behind the band, 0 ahead of it),
    /// for a band spanning <paramref name="lo"/>..<paramref name="hi"/> in the same units.</summary>
    public static double LitAt(double x, double lo, double hi) =>
        hi <= lo ? (x <= lo ? 1 : 0) : Math.Clamp((hi - x) / (hi - lo), 0, 1);
}
