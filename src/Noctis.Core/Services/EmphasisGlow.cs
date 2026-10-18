namespace Noctis.Services;

/// <summary>
/// The held-note glow level over time, fitted on an Apple Music capture ("Die With A
/// Smile", iOS). The glow layer draws only the sung letters, so this is just how bright
/// the halo around them is:
/// <list type="bullet">
/// <item>it comes up with the word along an ease-out, ~85% after 0.45 s and full by
/// ~0.6 s ("goodbye": halo under "oo" +5.7 levels at 0.45 s, +6.6 plateau from 0.6 s);</item>
/// <item>it holds while the note is held (no decline across the rest of "goodbye") and
/// lets go once the word ends;</item>
/// <item>it is about as bright on "follow" (2.0 s) as on "goodbye" (1.7 s), while shorter
/// holds glow less: the strength is AMLL's hold curve (<see cref="EmphasisMotion.For"/>)
/// relative to "goodbye"'s, square-rooted and capped at 1.</item>
/// </list>
/// Pure and frame-exact (unit-tested).
/// </summary>
public static class EmphasisGlow
{
    /// <summary>Glow layer opacity at full strength. With the glow blur at 0.45 em this puts
    /// ~5% white at the glyph edge, fading out ~0.25 em away, as measured.</summary>
    public const double PeakOpacity = 0.14;

    /// <summary>The rise to full glow after the word starts.</summary>
    public const double RiseMs = 600;

    /// <summary>The fade once the note ends (shortened if the held-note clock stops sooner).</summary>
    public const double FadeMs = 400;

    /// <summary>AMLL glow strength of "goodbye" (1.69 s, last word of its line), the
    /// reference the capture was measured on.</summary>
    public static readonly double ReferenceStrength = EmphasisMotion.For(1690, true).Blur;

    /// <summary>0..1 strength for a note held <paramref name="heldMs"/>.</summary>
    public static double Strength(double heldMs, bool isLastWord)
    {
        var blur = EmphasisMotion.For(heldMs, isLastWord).Blur;
        return blur <= 0 ? 0 : Math.Min(1, Math.Sqrt(blur / ReferenceStrength));
    }

    /// <summary>Glow layer opacity <paramref name="tMs"/> after the word started (NaN: at rest).</summary>
    public static double Opacity(double tMs, double heldMs, bool isLastWord, int charCount)
    {
        if (double.IsNaN(tMs) || tMs <= 0) return 0;
        var strength = Strength(heldMs, isLastWord);
        if (strength <= 0) return 0;

        // Done by the time the held-note clock stops (EmphasisMs goes NaN there).
        var end = EmphasisMotion.EndMs(EmphasisMotion.For(heldMs, isLastWord), Math.Max(1, charCount));
        var fadeStart = Math.Max(0, Math.Min(heldMs, end - FadeMs));
        var fadeEnd = Math.Min(end, fadeStart + FadeMs);
        if (tMs >= fadeEnd) return 0;

        var level = WordFloat.EaseOut(tMs / RiseMs);
        if (tMs > fadeStart)
        {
            var x = (tMs - fadeStart) / (fadeEnd - fadeStart);
            level *= 1 - x * x * (3 - 2 * x);
        }
        return PeakOpacity * strength * level;
    }
}
