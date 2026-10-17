using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-17: on ELRC lyrics a held (slowed) word "looks very choppy and very weird", and
/// the held-word effect lands on words that are not held / misses words that are. These pin
/// the timing side: one sweep front per line, clean edges next to rests and line ends, the
/// AMLL held-note rule, and the letter animation's clock.
/// </summary>
public class ElrcHeldWordTests
{
    private static readonly TimeSpan Lookahead = TimeSpan.FromMilliseconds(80);

    // Real ELRC lines (LRCLIB/Musixmatch style: every word's end is the next word's start,
    // the last word carries an end tag).
    internal const string Sample =
        "[00:37.87]<00:37.87>Now <00:38.14>I'm <00:38.67>left <00:39.27>in <00:39.55>the <00:39.92>cold, <00:40.56>let <00:40.81>my <00:41.23>story <00:41.91>be <00:42.53>told<00:43.90>\n" +
        "[00:43.23]<00:43.23>Let <00:43.50>my <00:43.92>story <00:44.59>be <00:45.20>told<00:47.62>\n" +
        "[00:47.89]<00:47.89>Oh, <00:50.63>oh<00:53.18>\n";

    private static List<LyricLine> Parse(string elrc) => LyricsViewModel.ParseLrcContent(elrc);

    private static int Len(WordTiming w) => Math.Max(1, w.SweepText.Trim().Length);

    private static bool IsLive(double p) => p > KaraokeSweep.InertFuture && p < KaraokeSweep.InertPast;

    /// <summary>
    /// The front each driven word implies, in characters from the line start, must be the
    /// same point. Each word used to run its own clock past its ends: after a held word the
    /// next word was fully lit while the held word's last letter was still dim.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    public void SweepFront_IsOnePointAcrossNeighbouringWords(int hz)
    {
        var lines = Parse(Sample + "[00:54.00]<00:54.00>I'm <00:54.25>the <00:54.60>one, <00:56.42>one<00:56.85>\n");
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        double worst = 0;
        foreach (var line in lines)
        {
            var words = line.Words!;
            var x = new double[words.Count];
            for (int i = 1; i < words.Count; i++) x[i] = x[i - 1] + Len(words[i - 1]);

            var from = line.Timestamp!.Value.TotalSeconds;
            var to = words[^1].End!.Value.TotalSeconds;
            for (var s = from; s < to; s += 1.0 / hz)
            {
                t.Update(TimeSpan.FromSeconds(s) - Lookahead);
                if (!line.IsActive || line.CurrentWordIndex < 0 || line.CurrentWordIndex >= words.Count) continue;
                var k = line.CurrentWordIndex;
                var front = x[k] + words[k].Progress * Len(words[k]);
                for (int j = Math.Max(0, k - 1); j <= Math.Min(words.Count - 1, k + 1); j++)
                {
                    if (j == k || !IsLive(words[j].Progress)) continue;
                    worst = Math.Max(worst, Math.Abs(x[j] + words[j].Progress * Len(words[j]) - front));
                }
            }
        }
        Assert.True(worst < 1e-9, $"neighbours disagree on the front by {worst:F3} characters");
    }

    /// <summary>
    /// The line's last word must be fully swept (its whole soft edge past the word) at its
    /// end time: the line deactivating snaps it to the inert "past" sentinel, so anything
    /// still half-lit there jumped to lit in one frame.
    /// </summary>
    [Theory]
    [InlineData(40, 60)]    // "told" at the lyrics page size
    [InlineData(40, 30)]    // a narrow word: the widest feather
    [InlineData(20, 200)]
    public void LastWord_IsFullySweptAtItsEnd_SoTheLineChangeDoesNotJump(double fontSize, double glyphWidth)
    {
        var lines = Parse(Sample);
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        var told = lines[1].Words![^1];
        Assert.True(told.SweepTrailOut);

        t.Update(told.End!.Value - Lookahead);
        var feather = KaraokeBand.Feather(glyphWidth, fontSize);
        var center = KaraokeBand.Center(told.Progress, feather, told.SweepLeadIn, told.SweepTrailOut);
        // The trailing edge of the band is at or past the word's end: every glyph is lit.
        Assert.True(center - feather >= 1 - 1e-9, $"band trailing edge at {center - feather:F3} of the word");
    }

    [Fact]
    public void FirstWord_IsNotLitBeforeItIsSung()
    {
        var lines = Parse(Sample);
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        var let = lines[1].Words![0];
        Assert.True(let.SweepLeadIn);

        // Line 2 is active (line 1's "told" overlaps it); its first word starts now.
        t.Update(let.Start - Lookahead);
        var feather = KaraokeBand.Feather(60, 40);
        var center = KaraokeBand.Center(let.Progress, feather, let.SweepLeadIn, let.SweepTrailOut);
        Assert.True(center + feather <= 1e-9, $"band leading edge at {center + feather:F3}: lit before the voice");
    }

    /// <summary>
    /// The sweep rides the rate-locked clock: with the raw position arriving in 100 ms steps,
    /// the front still advances every frame by close to the ideal amount across a held word.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    public void HeldWord_FrontAdvancesEveryFrame_FromACoarsePositionClock(int hz)
    {
        var lines = Parse(Sample);
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        var clock = new LyricsPlaybackClock();
        var told = lines[1].Words![^1];
        var startMs = 44_000.0;
        var frameMs = 1000.0 / hz;
        double prev = double.NaN, minRatio = double.MaxValue, maxRatio = 0;
        var ideal = frameMs / (told.End!.Value - told.Start).TotalMilliseconds;
        for (var now = 0.0; now < 3200; now += frameMs)
        {
            var truth = startMs + now;
            var raw = Math.Floor(truth / 100) * 100;            // coarse 100 ms position steps
            var pos = clock.Sample(raw, now);
            t.Update(TimeSpan.FromMilliseconds(pos));
            var p = told.Progress;
            if (now > 1500 && p is > 0.05 and < 0.95 && !double.IsNaN(prev) && prev is > 0 and < 1)
            {
                var ratio = (p - prev) / ideal;
                minRatio = Math.Min(minRatio, ratio);
                maxRatio = Math.Max(maxRatio, ratio);
            }
            prev = p;
        }
        Assert.True(minRatio > 0.85 && maxRatio < 1.15, $"per-frame advance {minRatio:F2}..{maxRatio:F2} of ideal");
    }

    // ── Which words are held notes ──

    private static WordTiming[] Words(string elrc) => Parse(elrc)[0].Words!.ToArray();

    private static string Emphasized(string elrc) =>
        string.Join(" ", Words(elrc).Where(w => w.IsEmphasis).Select(w => w.Text.Trim()));

    [Fact]
    public void HeldNote_RealLines_WhichWordsAreEmphasized()
    {
        // The line's last word carries its own end tag, so its length is real.
        Assert.Equal("told", Emphasized("[00:43.23]<00:43.23>Let <00:43.50>my <00:43.92>story <00:44.59>be <00:45.20>told<00:47.62>"));
        // A 1.16 s "brand" in a slow line: the old cadence gate (1.5x the line median)
        // asked 1.74 s of it; AMLL's 1 s rule emphasizes it.
        Assert.Equal("brand new", Emphasized("[03:41.19]<03:41.19>Be <03:41.65>brand <03:42.81>new<03:48.20>"));
        Assert.Equal("embers", Emphasized("[03:13.47]<03:13.47>Time <03:14.41>turns <03:15.23>flames <03:15.82>to <03:16.05>embers<03:17.10>"));
        // A comma then the next word's start: the span holds the breath after the comma
        // (on true word ends such words are sung for a median 80% of it).
        Assert.Equal("", Emphasized("[02:05.46]<02:05.46>Yeah, <02:06.49>you <02:06.69>know <02:06.88>what's <02:07.13>up<02:07.47>"));
        Assert.Equal("", Emphasized("[01:26.19]<01:26.19>I <01:26.57>push <01:26.92>my <01:27.15>luck, <01:28.29>it <01:29.15>shows<01:30.07>"));
        // Long enough to be a hold even with the breath in it.
        Assert.Equal("Oh, oh", Emphasized("[00:47.89]<00:47.89>Oh, <00:50.63>oh<00:53.18>"));
    }

    [Fact]
    public void PunctuationFloor_OnlyAppliesWhenTheEndIsTheNextStart()
    {
        // TTML-style true end with a rest after it: the 1.2 s is all singing.
        var line = new LyricLine
        {
            Timestamp = TimeSpan.Zero,
            Text = "t",
            Words = new List<WordTiming>
            {
                new() { Text = "luck, ", Start = TimeSpan.Zero, End = TimeSpan.FromMilliseconds(1200) },
                new() { Text = "it", Start = TimeSpan.FromMilliseconds(1600), End = TimeSpan.FromMilliseconds(1900) },
            },
        };
        Assert.True(line.Words![0].IsEmphasis);
        Assert.True(line.Words[0].SweepTrailOut);
        Assert.True(line.Words[1].SweepLeadIn);
    }

    [Fact]
    public void MergedWord_WithOneHeldSyllable_IsEmphasized()
    {
        // AMLL: a chunk is emphasized when any of its syllables is a held note by itself.
        var words = Words("[00:01.00]<00:01.00>Every<00:01.30>bo<00:01.50>dyyyy<00:03.00>");
        Assert.Single(words);
        Assert.Equal("Everybodyyyy", words[0].Text);
        Assert.True(words[0].IsEmphasis);
    }

    // ── The letter animation's clock ──

    [Fact]
    public void EmphasisClock_RunsThroughTheHold_AndFinishesAfterTheLineLeaves()
    {
        var lines = Parse(Sample);
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        var told = lines[1].Words![^1];
        Assert.True(told.IsEmphasis);

        t.Update(told.Start - Lookahead + TimeSpan.FromMilliseconds(1000));
        Assert.Equal(1000, told.EmphasisMs, 3);

        // Line 3 takes over at 47.89; the swell keeps playing out instead of dropping.
        t.Update(TimeSpan.FromSeconds(48.5) - Lookahead);
        Assert.False(lines[1].IsActive);
        Assert.True(t.HasEmphasisInFlight);
        Assert.Equal((48.5 - 45.2) * 1000, told.EmphasisMs, 3);

        var end = EmphasisMotion.EndMs(told.EmphasisParams, told.EmphasisCharCount);
        t.Update(told.Start + TimeSpan.FromMilliseconds(end + 50) - Lookahead);
        Assert.True(double.IsNaN(told.EmphasisMs));
        Assert.False(t.HasEmphasisInFlight);
    }

    [Fact]
    public void EmphasisClock_IsNaNForWordsThatAreNotHeld()
    {
        var lines = Parse(Sample);
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        t.Update(TimeSpan.FromSeconds(44.0));
        Assert.All(lines[1].Words!.Where(w => !w.IsEmphasis), w => Assert.True(double.IsNaN(w.EmphasisMs)));
    }

    [Fact]
    public void Reset_ClearsAnimationsInFlight()
    {
        var lines = Parse(Sample);
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        t.Update(TimeSpan.FromSeconds(46.0));
        t.Update(TimeSpan.FromSeconds(48.5));
        Assert.True(t.HasEmphasisInFlight);
        t.Reset();
        Assert.False(t.HasEmphasisInFlight);
        Assert.True(double.IsNaN(lines[1].Words![^1].EmphasisMs));
    }

    // ── AMLL letter animation math ──

    [Fact]
    public void EmphasisParams_MatchAmll()
    {
        // du = max(1000, d); amount = f(du/2000)*0.6; blur = f(du/3000)*0.5, f(x) = x^3 below 1,
        // sqrt above; the last word x1.6 / x1.5 / du x1.2; caps 1.2 / 0.8.
        var mid = EmphasisMotion.For(2420, isLastWord: false);
        Assert.Equal(2420, mid.DurationMs, 6);
        Assert.Equal(Math.Sqrt(1.21) * 0.6, mid.Amount, 6);
        Assert.Equal(Math.Pow(2420 / 3000.0, 3) * 0.5, mid.Blur, 6);

        var last = EmphasisMotion.For(2420, isLastWord: true);
        Assert.Equal(2420 * 1.2, last.DurationMs, 6);
        Assert.Equal(Math.Sqrt(1.21) * 0.6 * 1.6, last.Amount, 6);
        Assert.Equal(Math.Pow(2420 / 3000.0, 3) * 0.5 * 1.5, last.Blur, 6);

        Assert.Equal(1000, EmphasisMotion.For(400, false).DurationMs);
        Assert.Equal(1.2, EmphasisMotion.For(60_000, true).Amount);
        Assert.Equal(0.8, EmphasisMotion.For(60_000, true).Blur);
    }

    [Fact]
    public void LetterPoses_RiseOneAfterAnother_AndSettleBackToRest()
    {
        var p = EmphasisMotion.For(2420, isLastWord: true);
        const int n = 4;
        // At rest outside the window, and at NaN.
        foreach (var t in new[] { double.NaN, -EmphasisMotion.FloatLeadMs - 1, EmphasisMotion.EndMs(p, n) + 1 })
            for (int i = 0; i < n; i++)
                Assert.Equal(new EmphasisCharPose(1, 0, 0, 0), EmphasisMotion.CharPose(t, i, n, p));

        // Each letter's swell peaks half a bell after its own delay, later letters later.
        var peaks = Enumerable.Range(0, n).Select(i =>
            Enumerable.Range(0, 6000).Select(ms => (ms, EmphasisMotion.CharPose(ms, i, n, p).Scale))
                .MaxBy(x => x.Scale).ms).ToArray();
        for (int i = 0; i < n; i++)
            Assert.InRange(peaks[i], EmphasisMotion.CharDelayMs(p, i, n) + p.DurationMs / 2 - 30,
                EmphasisMotion.CharDelayMs(p, i, n) + p.DurationMs / 2 + 30);
        Assert.True(peaks.Zip(peaks.Skip(1)).All(x => x.First < x.Second));

        // Letters push away from the word's middle and lift (negative y).
        var mid = 1300.0;
        Assert.True(EmphasisMotion.CharPose(mid, 0, n, p).OffsetXEm < 0);
        Assert.True(EmphasisMotion.CharPose(mid, n - 1, n, p).OffsetXEm > 0);
        Assert.True(EmphasisMotion.CharPose(mid, 1, n, p).OffsetYEm < 0);
    }
}
