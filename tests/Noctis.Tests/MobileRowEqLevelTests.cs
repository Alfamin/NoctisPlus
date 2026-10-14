using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.Views;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's playing-row equalizer driven by the audio heard, as the desktop's EqVisualizer
/// is: the level math held to the desktop's (live mix, smoothing, height range), the band split
/// (bass left, treble right), the beat bounce and its decay, the shared per-frame read and its
/// demand window (the Android tap meters only while a bar reads), and the bars themselves
/// following a fed source on the frame clock, then settling flat on pause.
/// </summary>
public class MobileRowEqLevelTests
{
    private const int Rate = 48000;
    private const int Channels = 2;

    /// <summary>A beat meter and a spectrum meter on one fake clock, fed like the Android tap.</summary>
    private sealed class Rig
    {
        public double ClockMs;
        public readonly RowEqLevels Levels;
        private readonly float[] _buffer = new float[Rate / 100 * Channels];
        private long _frame;

        public Rig(int latencyMs = 0)
        {
            LatencyMs = latencyMs;
            Levels = new RowEqLevels(new BeatMeter(() => ClockMs), new SpectrumMeter(() => ClockMs));
        }

        public int LatencyMs { get; }

        /// <summary>Feeds <paramref name="seconds"/> of audio in 10 ms buffers, the clock in step.</summary>
        public void Feed(Func<double, float> sampleAt, double seconds)
        {
            var frames = _buffer.Length / Channels;
            for (var n = 0; n < (int)Math.Round(seconds * 100); n++)
            {
                for (var f = 0; f < frames; f++)
                {
                    var s = sampleAt((_frame + f) / (double)Rate);
                    _buffer[f * Channels] = s;
                    _buffer[f * Channels + 1] = s;
                }
                _frame += frames;
                Levels.Beat.Feed(_buffer, 0, _buffer.Length, Channels, Rate, LatencyMs);
                Levels.Spectrum.Feed(_buffer, 0, _buffer.Length, Channels, Rate, LatencyMs);
                ClockMs += 10;
            }
        }

        public double Seconds => _frame / (double)Rate;
    }

    /// <summary>120 BPM: a 60 Hz kick, an 80 ms decaying burst, on every half second.</summary>
    private static float Kicks(double t)
    {
        var inBeat = t % 0.5;
        return inBeat > 0.08 ? 0f : (float)(0.8 * Math.Exp(-inBeat / 0.03) * Math.Sin(2 * Math.PI * 60 * t));
    }

    private static Func<double, float> Sine(double hz, double amplitude = 0.5) =>
        t => (float)(amplitude * Math.Sin(2 * Math.PI * hz * t));

    // ---- Level math --------------------------------------------------------------------------

    /// <summary>The live mix, its smoothing and the level-to-height range are the desktop's own:
    /// same constants, same output for the same inputs.</summary>
    [Fact]
    public void LevelMath_IsTheDesktopRowIndicators()
    {
        double Const(string name) => (double)typeof(EqVisualizer).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(Const("LiveRest"), PlayingBars.LiveRest);
        Assert.Equal(Const("LiveSway"), PlayingBars.LiveSway);
        Assert.Equal(Const("LiveBounce"), PlayingBars.LiveBounce);
        Assert.Equal(Const("LiveTone"), PlayingBars.LiveTone);
        Assert.Equal(Const("LiveAttackMs"), PlayingBars.LiveAttackMs);
        Assert.Equal(Const("LiveReleaseMs"), PlayingBars.LiveReleaseMs);

        var random = new Random(7);
        var bands = new float[5];
        var phone = new float[5];
        var desktop = new float[5];
        for (var n = 0; n < 200; n++)
        {
            for (var i = 0; i < 5; i++) bands[i] = (float)random.NextDouble();
            var t = random.NextDouble() * 10;
            var pulse = random.NextDouble();
            PlayingBars.LiveLevels(t, pulse, bands, phone);
            EqVisualizer.LiveLevels(t, pulse, bands, desktop);
            Assert.Equal(desktop, phone);
            Assert.Equal(EqVisualizer.HeightForLevel(pulse), PlayingBars.HeightForLevel(pulse));

            var shownPhone = bands.ToArray();
            var shownDesktop = bands.ToArray();
            var dt = random.NextDouble() * 50;
            PlayingBars.Smooth(shownPhone, desktop, dt, PlayingBars.LiveAttackMs, PlayingBars.LiveReleaseMs);
            SpectrumVisualizer.Smooth(shownDesktop, desktop, dt, PlayingBars.LiveAttackMs, PlayingBars.LiveReleaseMs);
            Assert.Equal(shownDesktop, shownPhone);
        }
    }

    /// <summary>On the beat every bar lifts by the bounce; the spectrum tilts bars against the
    /// mean (a bass-heavy sound stands the left bar above the right).</summary>
    [Fact]
    public void LiveLevels_BounceOnTheBeat_AndTiltByTheSpectrum()
    {
        var flat = new float[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f };
        var off = new float[5];
        var on = new float[5];
        PlayingBars.LiveLevels(0.3, 0, flat, off);
        PlayingBars.LiveLevels(0.3, 0.5, flat, on);
        for (var i = 0; i < 5; i++) Assert.Equal(off[i] + PlayingBars.LiveBounce * 0.5, on[i], 5);

        var bass = new float[] { 0.9f, 0.6f, 0.4f, 0.2f, 0.1f };
        var tilted = new float[5];
        PlayingBars.LiveLevels(0.3, 0, bass, tilted);
        Assert.True(tilted[0] - off[0] > 0.2f, $"bass bar {tilted[0]} vs flat {off[0]}");
        Assert.True(tilted[4] < off[4], $"treble bar {tilted[4]} vs flat {off[4]}");
        Assert.All(tilted, l => Assert.InRange(l, 0f, 1f));
    }

    /// <summary>A fast rise and a slower fall: one attack time constant covers 63 % of a jump up,
    /// one release time constant 63 % of a drop, and the release leaves room to fall between beats
    /// at 120 BPM (500 ms is over four time constants: under 2 % left).</summary>
    [Fact]
    public void Smooth_RisesFast_FallsBetweenBeats()
    {
        var shown = new float[] { 0 };
        PlayingBars.Smooth(shown, new float[] { 1 }, PlayingBars.LiveAttackMs, PlayingBars.LiveAttackMs, PlayingBars.LiveReleaseMs);
        Assert.Equal(1 - Math.Exp(-1), shown[0], 4);

        shown[0] = 1;
        PlayingBars.Smooth(shown, new float[] { 0 }, PlayingBars.LiveReleaseMs, PlayingBars.LiveAttackMs, PlayingBars.LiveReleaseMs);
        Assert.Equal(Math.Exp(-1), shown[0], 4);

        shown[0] = 1;
        for (var ms = 0; ms < 500; ms += 16)
            PlayingBars.Smooth(shown, new float[] { 0 }, 16, PlayingBars.LiveAttackMs, PlayingBars.LiveReleaseMs);
        Assert.InRange(shown[0], 0f, 0.02f);
    }

    // ---- The shared source -------------------------------------------------------------------

    /// <summary>The five bands are the desktop meter's log split, bass left: a 60 Hz tone lights
    /// the first bar's band most, an 8 kHz tone the last's.</summary>
    [Fact]
    public void Bands_SplitBassLeftTrebleRight()
    {
        var bands = new float[RowEqLevels.BandCount];

        var low = new Rig();
        low.Feed(Sine(60), 0.5);
        Assert.True(low.Levels.TryRead(low.ClockMs, out _, bands));
        Assert.Equal(0, Array.IndexOf(bands, bands.Max()));

        var high = new Rig();
        high.Feed(Sine(8000), 0.5);
        Assert.True(high.Levels.TryRead(high.ClockMs, out _, bands));
        Assert.Equal(4, Array.IndexOf(bands, bands.Max()));
    }

    /// <summary>The pulse rides the kicks: near 1 just after each, under 0.1 by the next one's
    /// eve, and it lands on the heard time, the tap's latency later than the feed.</summary>
    [Fact]
    public void Pulse_FollowsTheBeat_AtTheHeardTime()
    {
        var rig = new Rig(latencyMs: 200);
        rig.Feed(Kicks, 2.04);   // eight beats, the ninth's onset 40 ms ago
        Assert.True(rig.Levels.TryRead(rig.ClockMs, out var early, new float[5]));
        // Fed but not yet heard: 200 ms ahead, so the ear is 160 ms before the onset, 340 ms past the last.
        Assert.InRange(early, 0.0, 0.2);

        var heard = rig.ClockMs + rig.LatencyMs - 40 + 20;   // 20 ms after the ninth kick reaches the ear
        Assert.True(rig.Levels.TryRead(heard, out var onBeat, new float[5]));
        Assert.InRange(onBeat, 0.8, 1.0);
        rig.Feed(Kicks, 0.4);   // up to 2.44 s: the tenth kick (2.5 s) not yet fed
        Assert.True(rig.Levels.TryRead(heard + 400, out var offBeat, new float[5]));
        Assert.InRange(offBeat, 0.0, 0.1);
    }

    /// <summary>Nothing fed: not live (the bars fall back to their own motion). Fed, then the
    /// feed stops (paused): live for the meter's short window, then not.</summary>
    [Fact]
    public void Levels_AreLiveOnlyWhileAudioFlows()
    {
        var rig = new Rig();
        var bands = new float[5];
        Assert.False(rig.Levels.TryRead(rig.ClockMs, out var pulse, bands));
        Assert.Equal(0, pulse);

        rig.Feed(Kicks, 1.0);
        Assert.True(rig.Levels.TryRead(rig.ClockMs + 5, out _, bands));
        Assert.False(rig.Levels.TryRead(rig.ClockMs + BeatMeter.LiveWindowMs + 50, out pulse, bands));
        Assert.Equal(0, pulse);
        Assert.All(bands, b => Assert.Equal(0f, b));
    }

    /// <summary>The tap meters only while a bar reads: wanted for a second after a read, then not.</summary>
    [Fact]
    public void Demand_LastsASecondAfterTheLastRead()
    {
        var rig = new Rig();
        Assert.False(rig.Levels.IsWanted(rig.ClockMs));
        rig.Levels.TryRead(1000, out _, new float[5]);
        Assert.True(rig.Levels.IsWanted(1000 + RowEqLevels.DemandWindowMs));
        Assert.False(rig.Levels.IsWanted(1000 + RowEqLevels.DemandWindowMs + 1));
    }

    /// <summary>Reads in one frame share one computation: a second bar in the same frame gets the
    /// first one's answer even though more audio arrived in between.</summary>
    [Fact]
    public void ReadsInOneFrame_ShareOneComputation()
    {
        var rig = new Rig();
        rig.Feed(_ => 0f, 1.0);
        var first = new float[5];
        var second = new float[5];
        rig.Levels.TryRead(rig.ClockMs, out var p1, first);
        rig.Levels.Spectrum.Feed(Enumerable.Repeat(0.9f, 4096).ToArray(), 0, 4096, Channels, Rate, 0);
        rig.Levels.TryRead(rig.ClockMs + 1, out var p2, second);
        Assert.Equal(p1, p2);
        Assert.Equal(first, second);
    }

    // ---- The bars ----------------------------------------------------------------------------

    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);   // the frame clock is the wall clock
        }
    }

    /// <summary>
    /// The bars follow a fed source: tall on the beat, low between beats, back up on the next; and
    /// with nothing fed they run the sines. Paused they settle flat and stop asking for frames,
    /// and stop asking for levels too (the tap goes idle).
    /// </summary>
    [AvaloniaFact]
    public void Bars_FollowAFedLevelSource_AndSettleOnPause()
    {
        var rig = new Rig();
        var bars = new PlayingBars { Fill = Brushes.White, Levels = rig.Levels, IsPlaying = true };
        var window = new Window { Width = 100, Height = 100, Content = bars };
        window.Show();

        // Nothing fed yet: the desktop's no-tap motion, sines inside their range.
        Pump(6);
        Assert.True(bars.IsRunning);
        Assert.All(bars.BarHeights, h => Assert.InRange(h, PlayingBars.BarMin, PlayingBars.BarMax));

        // A kick just heard: every bar stands at least rest + bounce·pulse high.
        rig.Feed(Kicks, 2.02);
        Pump(8);   // the clock stands still, so the pulse holds while the bars ease up to it
        var onBeat = bars.BarHeights.ToArray();
        Assert.True(rig.Levels.Beat.TryRead(rig.ClockMs, out var pulse) && pulse > 0.8, $"pulse {pulse}");
        Assert.All(onBeat, h => Assert.True(h >= PlayingBars.HeightForLevel(PlayingBars.LiveRest + PlayingBars.LiveBounce * 0.8 - PlayingBars.LiveTone * 0.5),
            $"on the beat: {string.Join(", ", onBeat)}"));

        // 400 ms on, between beats: the pulse has decayed and the bars came down.
        rig.Feed(Kicks, 0.4);
        Pump(20);
        var offBeat = bars.BarHeights.ToArray();
        Assert.True(offBeat.Average() < onBeat.Average() - 2, $"off {string.Join(", ", offBeat)} vs on {string.Join(", ", onBeat)}");

        // The next kick: up again.
        rig.Feed(Kicks, 0.12);
        Pump(8);
        Assert.True(bars.BarHeights.Average() > offBeat.Average() + 2, $"next beat {string.Join(", ", bars.BarHeights)}");
        Assert.True(rig.Levels.IsWanted(rig.ClockMs));

        // Pause: flat within the desktop's 420 ms ease, then no frames and no more reads.
        bars.IsPlaying = false;
        Pump(40);
        Assert.All(bars.BarHeights, h => Assert.Equal(PlayingBars.FlatHeight, h, 3));
        Assert.False(bars.IsRunning);
        Assert.False(rig.Levels.IsWanted(rig.ClockMs + RowEqLevels.DemandWindowMs + 1));
        window.Close();
    }
}
