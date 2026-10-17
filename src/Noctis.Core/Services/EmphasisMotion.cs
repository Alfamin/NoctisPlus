namespace Noctis.Services;

/// <summary>Per-word strength of the held-note animation (AMLL calculateEmphasizeParams).</summary>
/// <param name="DurationMs">Length of each letter's glow/swell bell.</param>
/// <param name="Amount">Swell/lift strength (0..1.2).</param>
/// <param name="Blur">Glow strength (0..0.8).</param>
public readonly record struct EmphasisParams(double DurationMs, double Amount, double Blur);

/// <summary>One letter's held-note pose at a point in time. Offsets are in em.</summary>
public readonly record struct EmphasisCharPose(double Scale, double OffsetXEm, double OffsetYEm, double Glow);

/// <summary>
/// The letter-by-letter held-note animation of Apple Music, ported from AMLL's
/// createEmphasizeAnimation (packages/core/src/lyric-player/dom/animation/emphasize).
/// Each letter of a held word runs the same bell — a swell, a slight push away from the
/// word's middle, a lift and a glow — one after another (a stagger of du/2.5 across the
/// word), plus a slower sine float that starts 400 ms ahead. Driven by the time since
/// the word started, so it is frame-exact and pure (unit-tested).
/// </summary>
public static class EmphasisMotion
{
    /// <summary>The float starts this much before the letter's glow (AMLL: wordDe - 400).</summary>
    public const double FloatLeadMs = 400;

    /// <summary>The float runs this many bell durations (AMLL: animateDu * 1.4).</summary>
    public const double FloatDurationFactor = 1.4;

    /// <summary>The letters' bells start spread over du / 2.5 (AMLL: du / 2.5 / charCount * i).</summary>
    public const double StaggerDivisor = 2.5;

    /// <summary>AMLL calculateEmphasizeParams: strength grows with the hold, the line's
    /// last word gets a stronger, longer swell.</summary>
    public static EmphasisParams For(double heldMs, bool isLastWord)
    {
        var du = Math.Max(1000, heldMs);
        var amount = Curve(du / 2000) * 0.6;
        var blur = Curve(du / 3000) * 0.5;
        if (isLastWord)
        {
            amount *= 1.6;
            blur *= 1.5;
            du *= 1.2;
        }
        return new EmphasisParams(du, Math.Min(1.2, amount), Math.Min(0.8, blur));

        static double Curve(double x) => x > 1 ? Math.Sqrt(x) : x * x * x;
    }

    /// <summary>Delay of letter <paramref name="index"/>'s bell after the word start.</summary>
    public static double CharDelayMs(EmphasisParams p, int index, int count) =>
        p.DurationMs / StaggerDivisor / Math.Max(1, count) * index;

    /// <summary>Time after the word start at which every letter is back at rest.</summary>
    public static double EndMs(EmphasisParams p, int count)
    {
        var lastDelay = CharDelayMs(p, Math.Max(0, count - 1), count);
        return lastDelay + Math.Max(p.DurationMs, FloatDurationFactor * p.DurationMs - FloatLeadMs);
    }

    /// <summary>True while some letter of the word is still moving at <paramref name="tMs"/>.</summary>
    public static bool IsInFlight(double tMs, EmphasisParams p, int count) =>
        tMs > -FloatLeadMs && tMs < EndMs(p, count);

    /// <summary>
    /// Pose of letter <paramref name="index"/> of <paramref name="count"/> at
    /// <paramref name="tMs"/> after the word start (AMLL generateGlowKeyframes +
    /// generateFloatKeyframes): scale 1 + bell·0.1·amount, x push −bell·0.03·amount·(n/2 − i) em,
    /// lift −bell·0.025·amount em plus the float −sin(πx)·0.05 em, glow bell·blur.
    /// </summary>
    public static EmphasisCharPose CharPose(double tMs, int index, int count, EmphasisParams p)
    {
        if (double.IsNaN(tMs) || count <= 0) return new EmphasisCharPose(1, 0, 0, 0);

        var delay = CharDelayMs(p, index, count);
        var bell = EmphasisBell.Envelope((tMs - delay) / p.DurationMs);

        var floatX = (tMs - (delay - FloatLeadMs)) / (FloatDurationFactor * p.DurationMs);
        var lift = floatX is > 0 and < 1 ? Math.Sin(floatX * Math.PI) * 0.05 : 0;

        return new EmphasisCharPose(
            1 + bell * 0.1 * p.Amount,
            -bell * 0.03 * p.Amount * (count / 2.0 - index),
            -bell * 0.025 * p.Amount - lift,
            bell * p.Blur);
    }

    /// <summary>Whole-word glow opacity: the mean of the letters' glow at <paramref name="tMs"/>
    /// (the word shares one blurred glow layer, AMLL glows each letter).</summary>
    public static double GlowOpacity(double tMs, int count, EmphasisParams p)
    {
        if (double.IsNaN(tMs) || count <= 0) return 0;
        double sum = 0;
        for (int i = 0; i < count; i++)
            sum += CharPose(tMs, i, count, p).Glow;
        return sum / count;
    }
}
