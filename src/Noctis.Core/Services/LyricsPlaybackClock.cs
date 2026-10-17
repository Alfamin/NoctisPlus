using System;

namespace Noctis.Services;

/// <summary>
/// Rate-locked playback clock for the lyrics engine. The raw position reaches the
/// UI in coarse steps (the audio layer advances per decoded block or output chunk,
/// a 100ms poll timer samples it, a dispatcher post delivers it), so a clock that
/// re-anchors on every fresh raw value either freezes (raw behind the estimate) or
/// jumps (raw ahead) at every poll — a sawtooth the karaoke sweep inherits.
///
/// This clock never steps for small disagreements: it advances continuously and
/// bends its <em>rate</em> toward the raw source, bounded to ±<see cref="MaxRateError"/>,
/// so a stale or early raw reading becomes an invisible speed change instead of a
/// visible hold-and-jump. Large disagreements are real seeks or stall recoveries and
/// snap. Pure math, no Avalonia, unit-tested by simulation.
/// </summary>
public sealed class LyricsPlaybackClock
{
    /// <summary>Disagreement past this is a seek or a recovered stall — snap, don't slew.</summary>
    public const double SnapThresholdMs = 400;

    /// <summary>Largest speed bend used to close a small error (10% either way is
    /// invisible on a word sweep; a freeze or a 100ms jump is not).</summary>
    public const double MaxRateError = 0.08;

    /// <summary>Time constant of the error correction: rate = 1 + error / τ (then clamped).</summary>
    public const double CorrectionTauMs = 800;

    /// <summary>No fresh raw value for this long (buffering hiccup) → hold rather than run away.</summary>
    public const double StallHoldMs = 1000;

    /// <summary>
    /// Right after a resume the frozen picture can trail the player (the output keeps reporting
    /// progress for a moment after the pause). The ordinary ±8% bend would take seconds to close
    /// that, and rapid pause/resume stacked it into a visible lag; so until the clock has caught
    /// up it may run this much faster (never backwards, never a jump).
    /// </summary>
    public const double ResumeCatchUpRate = 0.5;

    /// <summary>Time constant of the catch-up after a resume.</summary>
    public const double ResumeTauMs = 200;

    private double _raw = double.NaN;   // last raw reading
    private double _rawAtMs;            // wall time it was observed
    private double _estimate = double.NaN;
    private double _estimateAtMs;
    private bool _paused, _resuming, _catchUp, _freshSinceResume;

    /// <summary>Freeze the last displayed position, including its sub-poll precision.</summary>
    public void Pause() { _paused = true; _resuming = false; }

    /// <summary>Exclude the pause's wall time without rewinding to a coarse raw poll.</summary>
    public void Resume() { if (_paused) _resuming = true; _paused = false; }

    /// <summary>Forget everything; the next sample re-anchors on the raw value.</summary>
    public void Reset()
    {
        _raw = double.NaN;
        _estimate = double.NaN;
        _paused = _resuming = _catchUp = _freshSinceResume = false;
    }

    /// <summary>
    /// Advances the clock to wall time <paramref name="nowMs"/> given the latest raw
    /// position <paramref name="rawMs"/> and returns the smooth estimate.
    /// </summary>
    public double Sample(double rawMs, double nowMs)
    {
        if (double.IsNaN(_estimate))
            return Anchor(rawMs, nowMs);

        if (_paused) return _estimate;
        if (_resuming)
        {
            _resuming = false;
            _raw = rawMs; _rawAtMs = _estimateAtMs = nowMs;
            if (Math.Abs(rawMs - _estimate) > SnapThresholdMs) return Anchor(rawMs, nowMs);
            _catchUp = true; _freshSinceResume = false;
            return _estimate;
        }

        if (rawMs != _raw)
        {
            // Raw below the previous raw is the player itself moving backwards (a
            // seek, or the slider steering its target) — never smoothed away.
            if (rawMs < _raw)
                return Anchor(rawMs, nowMs);
            _raw = rawMs;
            _rawAtMs = nowMs;
            _freshSinceResume = true;
        }

        var dt = Math.Max(0, nowMs - _estimateAtMs);
        _estimateAtMs = nowMs;

        // Stall guard: the source stopped publishing — hold instead of extrapolating
        // into audio that has not played.
        if (nowMs - _rawAtMs > StallHoldMs)
            return _estimate;

        // The raw reading was accurate when observed; where it says the player is now.
        var target = _raw + (nowMs - _rawAtMs);
        var error = target - _estimate;
        if (Math.Abs(error) > SnapThresholdMs)
        {
            _estimate = target;
            return _estimate;
        }

        double rate;
        if (_catchUp && (error > 0 || _freshSinceResume))
        {
            // Speeding up may start at once (the reading at resume is the player's own); slowing
            // down waits for a fresh reading, as the one at resume can be a stale poll.
            rate = Math.Clamp(1 + error / ResumeTauMs, 1 - MaxRateError, 1 + ResumeCatchUpRate);
            if (Math.Abs(error) < 4) _catchUp = false;
        }
        else rate = Math.Clamp(1 + error / CorrectionTauMs, 1 - MaxRateError, 1 + MaxRateError);
        _estimate += dt * rate;
        return _estimate;
    }

    private double Anchor(double rawMs, double nowMs)
    {
        _raw = rawMs;
        _rawAtMs = nowMs;
        _estimate = rawMs;
        _estimateAtMs = nowMs;
        return rawMs;
    }
}
