// Easing curves from moshi/Lyric Motion 1.4.3 (MIT).
namespace Noctis.Features.LyricMotion;
/// <summary>Easing curves and envelopes shared by the word and letter animations.</summary>
internal static class Curves
{
    public static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>0 below <paramref name="lo"/>, 1 above <paramref name="hi"/>, smooth in between.</summary>
    public static double SmoothStep(double lo, double hi, double x)
    {
        var t = Clamp01((x - lo) / (hi - lo));
        return t * t * (3 - 2 * t);
    }

    /// <summary>0 to 1 with no sudden start and no sudden stop (zero speed and zero acceleration at both ends).</summary>
    public static double SmootherStep(double x)
    {
        var t = Clamp01(x);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    /// <summary>
    /// The size of an ordinary word while it is sung: a single even swell, 0 at the
    /// start, largest in the middle, 0 again at the end, the way up mirroring the way down.
    /// </summary>
    public static double Hill(double u)
    {
        u = Clamp01(u);
        var s = Math.Sin(Math.PI * u);
        return s * s;
    }

    /// <summary>
    /// How far a held word that grows as a whole has come up, from the seconds since it
    /// began: unhurried (a quarter to half a second) and eased at both ends.
    /// </summary>
    public static double HeldAttack(double seconds, double durationSec)
    {
        if (seconds <= 0) return 0;
        return SmootherStep(seconds / Math.Clamp(0.4 * durationSec, 0.26, 0.48));
    }
}
