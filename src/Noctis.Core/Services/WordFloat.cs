namespace Noctis.Services;

/// <summary>
/// The Apple Music word float: every word of a word-synced line rises a little as it is
/// sung and stays up until the line ends, so a sung line drifts upward word by word in one
/// smooth wave instead of standing still under the sweep.
///
/// Measured on an iOS Apple Music capture ("Die With A Smile", 1320 px wide, em ~97 px):
/// each word lifts 5-6 px (~0.055 em) from the moment the front reaches it. Dense 60 fps
/// tracking of "Where", "and" and "had" (ink centroid per frame) gives a soft start, not an
/// ease-out: ~8% up after 0.1 s, ~19% after 0.2 s, ~48% after 0.4 s, ~84% after 0.8 s,
/// settled by ~1.2 s. cubic-bezier(0.15, 0, 0.4, 1) over 1.25 s fits that (summed squared
/// error 0.004 against 0.060 for the CSS ease-out over 1 s this used, which popped a word
/// up 16% in its first 0.1 s). Words held longer rise over their hold. Pure and frame-exact
/// (unit-tested).
/// </summary>
public static class WordFloat
{
    /// <summary>Full lift, in em of the word's font size.</summary>
    public const double LiftEm = 0.055;

    /// <summary>The rise never runs faster than this, however short the word.</summary>
    public const double MinDurationMs = 1250;

    /// <summary>A line that stops being sung lets its words settle back over this long.</summary>
    public const double FallMs = 600;

    /// <summary>Length of a word's rise for a word sung over <paramref name="wordMs"/>.</summary>
    public static double RiseMs(double wordMs) =>
        double.IsNaN(wordMs) || wordMs < MinDurationMs ? MinDurationMs : wordMs;

    /// <summary>How far up a word is (0 at rest, 1 fully lifted) <paramref name="sinceStartMs"/>
    /// after it started, for a word sung over <paramref name="wordMs"/>.</summary>
    public static double Rise(double sinceStartMs, double wordMs)
    {
        if (double.IsNaN(sinceStartMs) || sinceStartMs <= 0) return 0;
        return Ease(sinceStartMs / RiseMs(wordMs));
    }

    /// <summary>The rise curve, cubic-bezier(0.15, 0, 0.4, 1), at <paramref name="x"/> (clamped
    /// to 0..1): eases in over the first ~10% so a word starts to float instead of jumping.</summary>
    public static double Ease(double x) => CubicBezier(x, 0.15, 0, 0.4, 1);

    /// <summary>Lift of a word that was at <paramref name="from"/> when its line stopped being
    /// sung, <paramref name="sinceFallMs"/> later: back to rest along an ease-in-out.</summary>
    public static double Fall(double from, double sinceFallMs)
    {
        if (double.IsNaN(sinceFallMs) || sinceFallMs <= 0) return from;
        if (sinceFallMs >= FallMs) return 0;
        var x = sinceFallMs / FallMs;
        var eased = x * x * (3 - 2 * x);
        return from * (1 - eased);
    }

    /// <summary>CSS <c>ease-out</c>, cubic-bezier(0, 0, 0.58, 1), at <paramref name="x"/>
    /// (clamped to 0..1).</summary>
    public static double EaseOut(double x) => CubicBezier(x, 0, 0, 0.58, 1);

    /// <summary>A CSS cubic-bezier(<paramref name="p1x"/>, <paramref name="p1y"/>,
    /// <paramref name="p2x"/>, <paramref name="p2y"/>) timing function at <paramref name="x"/>
    /// (clamped to 0..1). No allocation: a few Newton steps on x(s), bisection as a guard.</summary>
    public static double CubicBezier(double x, double p1x, double p1y, double p2x, double p2y)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        // Bezier with endpoints (0,0) and (1,1): B(s) = 3(1-s)^2 s p1 + 3(1-s) s^2 p2 + s^3.
        double ax = 1 + 3 * p1x - 3 * p2x, bx = 3 * p2x - 6 * p1x, cx = 3 * p1x;
        double ay = 1 + 3 * p1y - 3 * p2y, by = 3 * p2y - 6 * p1y, cy = 3 * p1y;
        double Bx(double s) => ((ax * s + bx) * s + cx) * s;
        double By(double s) => ((ay * s + by) * s + cy) * s;
        double Dx(double s) => (3 * ax * s + 2 * bx) * s + cx;

        var t = x;
        for (int i = 0; i < 8; i++)
        {
            var err = Bx(t) - x;
            if (Math.Abs(err) < 1e-7) return By(t);
            var d = Dx(t);
            if (Math.Abs(d) < 1e-6) break;
            t -= err / d;
            if (t is < 0 or > 1) break;
        }

        double lo = 0, hi = 1;
        t = x;
        for (int i = 0; i < 40; i++)
        {
            var v = Bx(t);
            if (Math.Abs(v - x) < 1e-7) break;
            if (v < x) lo = t; else hi = t;
            t = (lo + hi) / 2;
        }
        return By(t);
    }
}
