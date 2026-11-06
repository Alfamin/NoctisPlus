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
/// visible hold-and-jump. Large disagreements are real seeks or stall recoveries: a
/// forward one snaps, an estimate far ahead of its source holds until the source catches
/// up, and only the source itself moving backwards (a seek) steps the estimate back.
/// Pure math, no Avalonia, unit-tested by simulation.
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
    /// Raw readings this far behind the previous one are noise, not a seek: the estimate
    /// holds (never steps back) until the source catches up. 0 (the default) treats any
    /// backward reading as a seek. The phone sets it: its position source can land a
    /// reading a few ms behind the last one, which used to throw the sweep backwards.
    /// </summary>
    public double BackwardToleranceMs { get; init; }

    /// <summary>
    /// Right after a resume the frozen picture can trail the player (the output keeps reporting
    /// progress for a moment after the pause). The ordinary ±8% bend would take seconds to close
    /// that, and rapid pause/resume stacked it into a visible lag; so until the clock has caught
    /// up it may run this much faster (never backwards, never a jump).
    /// </summary>
    public const double ResumeCatchUpRate = 0.5;

    /// <summary>Time constant of the catch-up after a resume.</summary>
    public const double ResumeTauMs = 200;

    /// <summary>
    /// Media milliseconds per wall millisecond: the player's playback speed (owner #211). The clock ran at 1x between
    /// readings and could only bend 8% toward a song played at 1.5x, so it fell behind until the 400 ms snap: the
    /// lyrics jumped about once a second. Set before each <see cref="Sample(double, double)"/>; 1 by default.
    /// </summary>
    public double Rate { get; set; } = 1;

    private double _raw = double.NaN;   // last raw reading
    private double _rawAtMs;            // wall time it was taken
    private double _estimate = double.NaN;
    private double _estimateAtMs;
    private bool _holding;              // waiting for the source after running ahead of it
    private bool _paused, _resuming, _catchUp, _freshSinceResume;

    /// <summary>Freeze the last displayed position, including its sub-poll precision.</summary>
    public void Pause() { _paused = true; _resuming = false; }

    /// <summary>Exclude the pause's wall time without rewinding to a coarse raw poll.</summary>
    public void Resume() { if (_paused) _resuming = true; _paused = false; }

    private double Speed => double.IsFinite(Rate) && Rate > 0 ? Rate : 1;

    /// <summary>Forget everything; the next sample re-anchors on the raw value.</summary>
    public void Reset()
    {
        _raw = double.NaN;
        _estimate = double.NaN;
        _holding = false;
        _paused = _resuming = _catchUp = _freshSinceResume = false;
    }

    /// <summary>
    /// Advances the clock to wall time <paramref name="nowMs"/> given the latest raw
    /// position <paramref name="rawMs"/> and returns the smooth estimate. The reading is
    /// taken to be from the moment it was first seen.
    /// </summary>
    public double Sample(double rawMs, double nowMs) => Sample(rawMs, nowMs, nowMs);

    /// <summary>
    /// As <see cref="Sample(double, double)"/>, with the wall time <paramref name="rawAtMs"/>
    /// the raw reading was actually taken. A reading delivered late (a dispatcher post that
    /// waited behind a long frame) then still says where the player is now, instead of
    /// reading as "behind" and bending the rate down, then up again at the next poll.
    /// </summary>
    public double Sample(double rawMs, double rawAtMs, double nowMs)
    {
        rawAtMs = Math.Min(rawAtMs, nowMs);
        if (double.IsNaN(_estimate))
            return Anchor(rawMs, rawAtMs, nowMs);

        if (_paused) return _estimate;
        if (_resuming)
        {
            _resuming = false;
            _raw = rawMs;
            _rawAtMs = rawAtMs;
            _estimateAtMs = nowMs;
            if (Math.Abs(rawMs - _estimate) > SnapThresholdMs) return Anchor(rawMs, rawAtMs, nowMs);
            _catchUp = true;
            _freshSinceResume = false;
            _holding = false;
            return _estimate;
        }

        if (rawMs != _raw)
        {
            // Raw below the previous raw is the player itself moving backwards (a
            // seek, or the slider steering its target) — never smoothed away. Within
            // BackwardToleranceMs it is a noisy reading: taken, but the estimate only holds.
            if (rawMs < _raw - BackwardToleranceMs || (rawMs < _raw && BackwardToleranceMs <= 0))
                return Anchor(rawMs, rawAtMs, nowMs);
            _raw = rawMs;
            _rawAtMs = rawAtMs;
            _freshSinceResume = true;
        }

        var dt = Math.Max(0, nowMs - _estimateAtMs);
        _estimateAtMs = nowMs;

        // Stall guard: the source stopped publishing — hold instead of extrapolating
        // into audio that has not played.
        if (nowMs - _rawAtMs > StallHoldMs)
            return _estimate;

        // The raw reading was accurate when taken; where it says the player is now,
        // compared with where the clock would be now running at speed 1. (Comparing with
        // the estimate of the previous frame counted the frame itself as error: a long
        // frame then bent the rate up for all of it and overshot.) Both run at the playback
        // speed (owner #211).
        var speed = Speed;
        var target = _raw + (nowMs - _rawAtMs) * speed;
        var predicted = _estimate + dt * speed;
        var error = target - predicted;
        if (error > SnapThresholdMs)
        {
            _estimate = target;
            _catchUp = false;
            return _estimate;
        }
        if (error < -SnapThresholdMs || (_holding && error < 0))
        {
            // Ahead of the source by more than a slew can close: a recovered stall. Hold
            // until it has caught up rather than jumping back (un-singing words), unless it
            // is so far ahead that holding would read as a freeze.
            if (error < -2 * StallHoldMs) return Anchor(_raw, _rawAtMs, nowMs);
            _holding = true;
            return _estimate;
        }
        _holding = false;

        if (_catchUp && (error > 0 || _freshSinceResume))
        {
            // After a resume: speeding up may start at once (the reading at resume is the
            // player's own) and close faster; slowing down waits for a fresh reading, as the
            // one at resume can be a stale poll. Still never past the target, never backwards.
            var catchUp = error * (1 - Math.Exp(-dt / ResumeTauMs));
            _estimate = predicted + Math.Clamp(catchUp, -dt * speed * MaxRateError, dt * speed * ResumeCatchUpRate);
            if (Math.Abs(target - _estimate) < 4) _catchUp = false;
            return _estimate;
        }

        // Close the error at the correction's rate, never past it, and never faster or
        // slower than MaxRateError: monotonic, no overshoot, invisible on a word sweep.
        var correction = error * (1 - Math.Exp(-dt / CorrectionTauMs));
        var bound = dt * speed * MaxRateError;
        _estimate = predicted + Math.Clamp(correction, -bound, bound);
        return _estimate;
    }

    private double Anchor(double rawMs, double rawAtMs, double nowMs)
    {
        _raw = rawMs;
        _rawAtMs = rawAtMs;
        _holding = false;
        // A reading taken a moment ago is already that much behind the player.
        _estimate = rawMs + Math.Clamp(nowMs - rawAtMs, 0, StallHoldMs) * Speed;
        _estimateAtMs = nowMs;
        return _estimate;
    }
}
