// Smooth Lyric Motion numbers adapted from moshi/Lyric Motion 1.4.3 (MIT).
namespace Noctis.Helpers;

/// <summary>Small allocation-free curves used by the phone's native Plus word effect.</summary>
internal static class PlusMotionCurves
{
    public static double Clamp01(double x) => !double.IsFinite(x) || x <= 0 ? 0 : x >= 1 ? 1 : x;

    public static double SmoothStep(double lo, double hi, double x)
    {
        var t = Clamp01((x - lo) / (hi - lo));
        return t * t * (3 - 2 * t);
    }

    public static double SmootherStep(double x)
    {
        var t = Clamp01(x);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    public static double Hill(double u)
    {
        var s = Math.Sin(Math.PI * Clamp01(u));
        return s * s;
    }
}
