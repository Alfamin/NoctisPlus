using System;

namespace Noctis.Helpers;

/// <summary>
/// The one motion curve every lyric line change uses — the scroll glide that brings
/// the new line to the anchor AND the line's own scale transition (XAML
/// <see cref="LineMotionEase"/>) share it, so size and position arrive together
/// instead of the line popping to full size before the list has started to move.
///
/// Spring step response matched to Apple Music: a 120 fps capture of a line change
/// (iOS, "Die With A Smile") fits a spring with damping ratio <see cref="Zeta"/> = 0.77
/// and natural frequency <see cref="NaturalFrequency"/> = 8.25 rad/s to 0.7px RMS —
/// 5/50/90/99% of the travel at 48/181/348/448 ms, a ~2% overshoot peaking near
/// 560 ms, then a slow settle back. The old curve (ζ = 0.8 squeezed into 650 ms,
/// 0.5% overshoot) reached the same points 12–25 ms sooner and landed flat. The
/// curve runs in real time and is normalized to end exactly at 1 at
/// <see cref="DurationMs"/>, where the residual is 0.3% of the travel (under half a
/// pixel on a 131px line change). Pinned by LyricsMotionTests.
/// </summary>
public static class LineMotion
{
    /// <summary>Length of a line-change motion — long enough for the spring's overshoot
    /// to settle back. The XAML line transitions carry the same value ("0:0:1") — pinned
    /// by LyricsMotionTests.</summary>
    public const int DurationMs = 1000;

    /// <summary>Damping ratio (Apple Music fit). 1 = critically damped, lower = bouncier.</summary>
    public const double Zeta = 0.77;

    /// <summary>Natural frequency in rad/s (Apple Music fit).</summary>
    public const double NaturalFrequency = 8.25;

    /// <summary>Per-line stagger of the cascade: each line below the active one starts
    /// its glide this much later than the line above it. Measured on Apple Music (iOS,
    /// 120 fps capture, "Die With A Smile"): four consecutive lines below the active one
    /// started at exactly +50, +100, +150, +200 ms, each on the same curve — 35 ms read
    /// as the stack moving nearly as one slab. Shared by the lyrics page and side panel.</summary>
    public const double CascadeDelayPerLineMs = 50;

    // Natural frequency per unit of normalized progress (p = 1 at DurationMs).
    private static readonly double Wn = NaturalFrequency * DurationMs / 1000.0;
    private static readonly double Wd = Wn * Math.Sqrt(1 - Zeta * Zeta);
    private static readonly double Norm = Raw(1);

    private static double Raw(double p)
    {
        var env = Math.Exp(-Zeta * Wn * p);
        return 1 - env * (Math.Cos(Wd * p) + Zeta / Math.Sqrt(1 - Zeta * Zeta) * Math.Sin(Wd * p));
    }

    /// <summary>Normalized progress → eased fraction; 0 at 0, exactly 1 at 1, clamped outside.</summary>
    public static double Ease(double p)
    {
        if (p <= 0) return 0;
        if (p >= 1) return 1;
        return Raw(p) / Norm;
    }

    /// <summary>Time at which the motion has covered half its travel — the moment the
    /// eye reads the line as "arrived". The line-only lyric lead is derived from it.</summary>
    public static readonly double HalfTravelMs = SolveTravel(0.5);

    /// <summary>Time at which the motion has covered 90% of its travel. The scroll leads
    /// a word-timed line by this much, so the list has all but arrived when its first
    /// word starts (see LyricsTimeline.ScrollIndex).</summary>
    public static readonly double NinetyTravelMs = SolveTravel(0.9);

    /// <summary>
    /// Limits one cascade line's lag relative to the line above it. Lines below the
    /// active one lag the base glide by a per-line delay; the raw lag difference
    /// between neighbours peaks at this curve's steepest slope × the per-line delay
    /// (~21% of the travel at 50 ms), which exceeds a 16px hard clamp on an ordinary
    /// line change. A hard clamp is a velocity kink — the line rides the one above for a few
    /// frames, then snaps back to its own curve — right at peak speed.
    ///
    /// A <em>negative</em> step means the lower line is displaced toward the one above
    /// (the cascade is closing: a backward glide). That is the only direction lines can
    /// cross, so it keeps the hard bound. A positive step means the lines are spreading
    /// (forward advance): identity up to <paramref name="hardStepPx"/>, then a smooth
    /// saturation that never exceeds twice it — no kink, no fly-apart on multi-line skips.
    /// </summary>
    public static double CascadeStep(double rawLag, double prevLag, double hardStepPx)
    {
        var step = rawLag - prevLag;
        if (step <= hardStepPx)
            return prevLag + Math.Max(step, -hardStepPx);
        var excess = step - hardStepPx;
        return prevLag + hardStepPx + hardStepPx * Math.Tanh(excess / hardStepPx);
    }

    private static double SolveTravel(double fraction)
    {
        double lo = 0, hi = 1;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            if (Ease(mid) < fraction) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2 * DurationMs;
    }
}

/// <summary>XAML-usable easing over <see cref="LineMotion.Ease"/>.</summary>
public sealed class LineMotionEase : Avalonia.Animation.Easings.Easing
{
    public override double Ease(double progress) => LineMotion.Ease(progress);
}
