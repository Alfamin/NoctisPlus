using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The lyrics clock used to re-anchor on every fresh raw position and hold when the
/// raw value landed behind its extrapolation: at every poll the word sweep either
/// froze for a few frames or jumped forward. These pin the replacement — a rate-locked
/// clock whose per-frame advance stays within a narrow band of real time while the
/// raw source it follows steps coarsely — and keep a copy of the old integrator to
/// prove the bound actually excludes the old behaviour.
/// </summary>
public class LyricsPlaybackClockTests
{
    private readonly ITestOutputHelper _output;
    public LyricsPlaybackClockTests(ITestOutputHelper output) => _output = output;

    private const double FrameMs = 1000.0 / 60;
    private const double PollMs = 100;

    [Fact]
    public void RapidPauseResumeRetainsTheDisplayedSweepWithoutAddingPausedTime()
    {
        var clock = new LyricsPlaybackClock();
        clock.Sample(1000, 0);
        var shown = clock.Sample(1000, 70);
        for (var i = 0; i < 12; i++)
        {
            clock.Pause();
            Assert.Equal(shown, clock.Sample(1000, 1000 + i * 500));
            clock.Resume();
            Assert.Equal(shown, clock.Sample(1000, 1100 + i * 500));
            var next = clock.Sample(1000, 1116 + i * 500);
            Assert.InRange(next - shown, 16 * 0.92 - 0.01, 16 * 1.08 + 0.01);
            shown = next;
        }
        clock.Pause(); clock.Reset();
        Assert.Equal(200, clock.Sample(200, 20000));
    }

    [Fact]
    public void RapidPauseResume_DoesNotStackALagBehindThePlayer()
    {
        // Seen in the real app (2026-10-17): after a pause the player still reports ~100 ms more
        // than the frozen picture. Eight quick toggles left the lyrics ~400 ms late with only the
        // ±8% bend. After each resume the clock must catch up smoothly: never backwards, at most
        // 1.5x speed, and in sync again well within half a second.
        var clock = new LyricsPlaybackClock();
        double now = 0, raw = 5000, shown = clock.Sample(raw, now);
        void Play(double ms)
        {
            for (var t = 0.0; t < ms; t += FrameMs)
            {
                now += FrameMs; raw += FrameMs;
                var next = clock.Sample(Math.Floor(raw / PollMs) * PollMs, now);
                Assert.InRange(next - shown, -0.001, FrameMs * (1 + LyricsPlaybackClock.ResumeCatchUpRate) + 0.01);
                shown = next;
            }
        }
        Play(500);
        for (var i = 0; i < 4; i++)
        {
            clock.Pause();
            raw += 100; // the output's tail after the pause
            now += 180; clock.Sample(Math.Floor(raw / PollMs) * PollMs, now);
            clock.Resume();
            Play(180);
        }
        Play(450);
        Assert.InRange(raw - shown, -60, 60);
    }

    /// <summary>
    /// Models the real chain: the audio layer's position advances per decoded block,
    /// a 100ms timer polls it, and the dispatcher delivers it some jittery time later.
    /// Yields (nowMs, rawMsAsSeenByTheLyricsClock, truthMs) per rendered frame.
    /// </summary>
    private static IEnumerable<(double Now, double Raw, double Truth)> Frames(
        double blockMs, double maxDispatchJitterMs, double seconds, int seed)
    {
        var rng = new Random(seed);
        var nextPollAt = 0.0;
        var pendingRaw = 0.0;
        var pendingDeliverAt = double.MaxValue;
        var raw = 0.0;
        for (var now = 0.0; now < seconds * 1000; now += FrameMs)
        {
            while (nextPollAt <= now)
            {
                var truthAtPoll = nextPollAt;
                pendingRaw = Math.Floor(truthAtPoll / blockMs) * blockMs;
                pendingDeliverAt = nextPollAt + rng.NextDouble() * maxDispatchJitterMs;
                nextPollAt += PollMs;
            }
            if (pendingDeliverAt <= now)
            {
                raw = pendingRaw;
                pendingDeliverAt = double.MaxValue;
            }
            yield return (now, raw, now);
        }
    }

    /// <summary>Verbatim port of the anchor-and-hold integrator this clock replaced.</summary>
    private sealed class LegacyAnchorHoldClock
    {
        private long _rawMs = -1;
        private long _anchorMs;
        private double _anchorAt;
        private double _lastMs;

        public double Sample(double rawMsIn, double nowMs)
        {
            var rawMs = (long)rawMsIn;
            var rawMovedBack = _rawMs >= 0 && rawMs < _rawMs;
            if (rawMs != _rawMs)
            {
                _rawMs = rawMs;
                _anchorMs = rawMs;
                _anchorAt = nowMs;
            }
            var elapsed = Math.Min(1000, nowMs - _anchorAt);
            var estimate = _anchorMs + elapsed;
            if (!rawMovedBack && estimate < _lastMs && _lastMs - estimate < 300)
                estimate = _lastMs;
            _lastMs = estimate;
            return estimate;
        }
    }

    private static (double MinStep, double MaxStep, double MaxAbsError) Run(
        Func<double, double, double> sample, double blockMs, double jitterMs, int seed)
    {
        double prev = double.NaN, minStep = double.MaxValue, maxStep = double.MinValue, maxErr = 0;
        foreach (var (now, raw, truth) in Frames(blockMs, jitterMs, seconds: 12, seed))
        {
            var est = sample(raw, now);
            if (now >= 1500)   // past warm-up
            {
                if (!double.IsNaN(prev))
                {
                    var step = est - prev;
                    minStep = Math.Min(minStep, step);
                    maxStep = Math.Max(maxStep, step);
                }
                maxErr = Math.Max(maxErr, Math.Abs(est - truth));
            }
            prev = est;
        }
        return (minStep, maxStep, maxErr);
    }

    [Theory]
    [InlineData(50, 30, 1)]     // legacy VLC Time: ~50ms decoder blocks, dispatch jitter
    [InlineData(200, 30, 2)]    // worst-case VLC refresh (~150-300ms observed in the field)
    [InlineData(10, 40, 3)]     // gapless sink: 10ms WASAPI periods, jittery delivery
    public void RateLockedClock_NeverFreezesOrJumps_WhileTrackingTheSource(double blockMs, double jitterMs, int seed)
    {
        var clock = new LyricsPlaybackClock();
        var (minStep, maxStep, maxErr) = Run(clock.Sample, blockMs, jitterMs, seed);
        _output.WriteLine($"block={blockMs} jitter={jitterMs}: step {minStep:F2}..{maxStep:F2} ms/frame (frame {FrameMs:F2}), max |err| {maxErr:F0} ms");

        var lo = FrameMs * (1 - LyricsPlaybackClock.MaxRateError) - 0.01;
        var hi = FrameMs * (1 + LyricsPlaybackClock.MaxRateError) + 0.01;
        Assert.True(minStep >= lo, $"clock stalled: smallest frame step {minStep:F2}ms < {lo:F2}ms");
        Assert.True(maxStep <= hi, $"clock jumped: largest frame step {maxStep:F2}ms > {hi:F2}ms");
        // Steady lag behind the truth is the poll + block quantization; it must be bounded
        // (a lookahead absorbs it), not growing.
        Assert.True(maxErr < blockMs + PollMs + jitterMs + 40, $"clock drifted {maxErr:F0}ms from the source");
    }

    [Theory]
    [InlineData(50, 30, 1)]
    [InlineData(200, 30, 2)]
    public void LegacyAnchorHoldClock_FailsTheSameBound(double blockMs, double jitterMs, int seed)
    {
        var legacy = new LegacyAnchorHoldClock();
        var (minStep, maxStep, _) = Run(legacy.Sample, blockMs, jitterMs, seed);
        _output.WriteLine($"legacy block={blockMs}: step {minStep:F2}..{maxStep:F2} ms/frame");

        var lo = FrameMs * (1 - LyricsPlaybackClock.MaxRateError);
        var hi = FrameMs * (1 + LyricsPlaybackClock.MaxRateError);
        // A bound both integrators pass is not a guard: the old one must visibly hold
        // (a zero-length frame step) or jump (a step several frames long).
        Assert.True(minStep < lo || maxStep > hi,
            "the legacy integrator passed the smoothness bound — the bound is too loose to prove anything");
    }

    [Fact]
    public void SeekBackwards_SnapsToTheNewPositionAtOnce()
    {
        var clock = new LyricsPlaybackClock();
        clock.Sample(60_000, 0);
        clock.Sample(60_000, 500);
        var est = clock.Sample(15_000, 516);
        Assert.Equal(15_000, est, 0.01);
    }

    [Fact]
    public void LargeForwardJump_Snaps_SmallOneSlews()
    {
        var clock = new LyricsPlaybackClock();
        clock.Sample(1000, 0);
        clock.Sample(1000, 100);
        // +2s beyond where the clock thinks it is: a forward seek — the estimate lands
        // on the raw value just observed, no slew.
        Assert.Equal(3100, clock.Sample(3100, 100 + FrameMs), 1.0);

        clock = new LyricsPlaybackClock();
        clock.Sample(1000, 0);
        clock.Sample(1000, 100);
        // +150ms disagreement: not a seek — the estimate keeps moving at ≤ 1+MaxRateError.
        var before = clock.Sample(1000, 200);
        var after = clock.Sample(1350, 200 + FrameMs);
        var step = after - before;
        Assert.InRange(step, FrameMs * (1 - LyricsPlaybackClock.MaxRateError) - 0.01,
                             FrameMs * (1 + LyricsPlaybackClock.MaxRateError) + 0.01);
    }

    [Fact]
    public void SourceStalls_ClockHoldsAfterOneSecond()
    {
        var clock = new LyricsPlaybackClock();
        clock.Sample(5000, 0);
        var at900 = clock.Sample(5000, 900);
        var at1200 = clock.Sample(5000, 1200);
        var at1500 = clock.Sample(5000, 1500);
        Assert.True(at900 > 5000, "clock should extrapolate through a short gap");
        Assert.Equal(at1200, at1500, 0.01);
    }

    [Fact]
    public void Reset_ReanchorsOnTheNextRawValue()
    {
        var clock = new LyricsPlaybackClock();
        clock.Sample(1000, 0);
        clock.Sample(1000, 300);
        clock.Reset();
        Assert.Equal(1000, clock.Sample(1000, 5000), 0.01);
    }
}
