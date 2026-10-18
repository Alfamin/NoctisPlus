using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-18: "the lyrics flow like Apple Music ... it does it to the entire lyric". On an
/// Apple Music capture every sung word floats up ~0.055 em along a ~1 s ease-out and stays
/// up until its line ends. These pin the curve (<see cref="WordFloat"/>) and how the
/// timeline drives it (<see cref="WordTiming.Lift"/>).
/// </summary>
public class WordFloatTests
{
    private static readonly TimeSpan Lookahead = TimeSpan.FromMilliseconds(80);

    private const string Sample =
        "[00:15.87]<00:15.87>Where <00:16.12>you <00:16.29>and <00:16.47>I <00:17.17>had <00:17.41>to <00:17.60>say <00:18.15>good<00:18.39>bye<00:19.84>\n" +
        "[00:20.48]<00:20.48>And <00:20.72>I <00:21.10>don't <00:21.31>know<00:24.25>\n";

    private static List<LyricLine> Parse(string lrc) => LyricsViewModel.ParseLrcContent(lrc);

    // ── The curve ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    // cubic-bezier(0,0,0.58,1) reference points (s = 0.55 → x 0.4031, y 0.5749; s = 0.85 → x 0.8028, y 0.9393).
    [InlineData(0.40306, 0.57488)]
    [InlineData(0.80283, 0.93925)]
    public void EaseOut_IsCssEaseOut(double x, double y) =>
        Assert.Equal(y, WordFloat.EaseOut(x), precision: 3);

    [Fact]
    public void EaseOut_IsMonotonicAndClamped()
    {
        Assert.Equal(0, WordFloat.EaseOut(-1));
        Assert.Equal(1, WordFloat.EaseOut(2));
        double prev = 0;
        for (int i = 1; i <= 1000; i++)
        {
            var v = WordFloat.EaseOut(i / 1000.0);
            Assert.True(v >= prev - 1e-9, $"dips at {i / 1000.0}");
            prev = v;
        }
    }

    /// <summary>Dense 60 fps tracking of the Apple capture ("Where", "and", "had", averaged):
    /// ~8% up after 0.1 s, ~19% after 0.2 s, ~48% after 0.4 s, ~84% after 0.8 s, settled by
    /// ~1.2 s. The old CSS ease-out was already 16% up at 0.1 s and 31% at 0.2 s.</summary>
    [Theory]
    [InlineData(100, 0.075)]
    [InlineData(200, 0.19)]
    [InlineData(400, 0.48)]
    [InlineData(600, 0.72)]
    [InlineData(800, 0.835)]
    public void Rise_MatchesTheMeasuredAppleCurve_ForAShortWord(double ms, double apple)
    {
        const double word = 240; // "had": 17.17 → 17.41
        Assert.InRange(WordFloat.Rise(ms, word), apple - 0.06, apple + 0.06);
    }

    [Fact]
    public void Rise_EasesIn_AndSettles()
    {
        Assert.Equal(0, WordFloat.Rise(0, 240));
        // Soft start: the first frame moves far less than a linear ramp would.
        Assert.True(WordFloat.Rise(16, 240) < 16 / WordFloat.MinDurationMs);
        Assert.Equal(1, WordFloat.Rise(WordFloat.MinDurationMs, 240));
        double prev = 0;
        for (int ms = 1; ms <= 1250; ms++)
        {
            var v = WordFloat.Rise(ms, 240);
            Assert.True(v >= prev - 1e-9, $"dips at {ms} ms");
            prev = v;
        }
    }

    [Theory]
    [InlineData(0.5, 0.5)]   // symmetric control points: the midpoint maps to itself
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    public void CubicBezier_HitsItsEndpointsAndSymmetry(double x, double y)
        => Assert.Equal(y, WordFloat.CubicBezier(x, 0.42, 0, 0.58, 1), precision: 4);

    [Fact]
    public void Rise_TakesTheWordsLength_WhenLongerThanTheFloor()
    {
        // A 2 s held word rises over its whole hold, not in the first second.
        Assert.True(WordFloat.Rise(1000, 2000) < 0.9);
        Assert.Equal(WordFloat.Rise(1000, 1250), WordFloat.Rise(1600, 2000), precision: 9);
        Assert.Equal(1, WordFloat.Rise(2000, 2000));
    }

    [Fact]
    public void Rise_IsZeroBeforeTheWordAndForNaN()
    {
        Assert.Equal(0, WordFloat.Rise(-50, 300));
        Assert.Equal(0, WordFloat.Rise(double.NaN, 300));
        Assert.Equal(WordFloat.MinDurationMs, WordFloat.RiseMs(double.NaN));
    }

    [Fact]
    public void Fall_SettlesFromWhereTheWordWas()
    {
        Assert.Equal(0.8, WordFloat.Fall(0.8, 0));
        Assert.Equal(0.4, WordFloat.Fall(0.8, WordFloat.FallMs / 2), precision: 9);
        Assert.Equal(0, WordFloat.Fall(0.8, WordFloat.FallMs));
        double prev = 1;
        for (int i = 0; i <= 60; i++)
        {
            var v = WordFloat.Fall(1, i * WordFloat.FallMs / 60);
            Assert.True(v <= prev + 1e-12);
            prev = v;
        }
    }

    // ── The held-note glow ──────────────────────────────────────────────────────────

    [Fact]
    public void Glow_IsOffAtRest_AndStrongerTheLongerTheNoteIsHeld()
    {
        Assert.Equal(0, EmphasisGlow.Opacity(double.NaN, 1700, true, 7));

        static double Peak(double heldMs)
        {
            double best = 0;
            for (double t = 0; t < 6000; t += 10)
                best = Math.Max(best, EmphasisGlow.Opacity(t, heldMs, false, 5));
            return best;
        }
        var short1 = Peak(1000);
        var mid = Peak(1700);
        var long1 = Peak(3000);
        Assert.True(short1 > 0 && short1 < mid && mid < long1, $"{short1} {mid} {long1}");
        Assert.True(Peak(20000) <= 1);
    }

    /// <summary>Measured on "goodbye" (1.69 s, last word): ~85% of the halo 0.45 s in, full
    /// by ~0.6 s, no decline while the note is held, gone soon after it ends.</summary>
    [Fact]
    public void Glow_RisesWithTheWord_HoldsWhileHeld_AndLetsGo()
    {
        const double held = 1690;
        double At(double t) => EmphasisGlow.Opacity(t, held, true, 7);
        var full = EmphasisGlow.PeakOpacity * EmphasisGlow.Strength(held, true);
        Assert.Equal(1, EmphasisGlow.Strength(held, true), precision: 9);
        Assert.Equal(0, At(0));
        Assert.InRange(At(450) / full, 0.8, 0.95);
        Assert.Equal(full, At(EmphasisGlow.RiseMs), precision: 9);
        Assert.Equal(full, At(held), precision: 9);
        Assert.True(At(held + EmphasisGlow.FadeMs / 2) is > 0 and < 0.75 * EmphasisGlow.PeakOpacity);
        Assert.Equal(0, At(held + EmphasisGlow.FadeMs));
        // Always dark by the time the held-note clock stops (EmphasisMs -> NaN): no pop.
        foreach (var h in new[] { 1000.0, 1300, 2500, 6000 })
            foreach (var n in new[] { 1, 3, 7 })
                Assert.Equal(0, EmphasisGlow.Opacity(
                    EmphasisMotion.EndMs(EmphasisMotion.For(h, false), n) - 1e-6, h, false, n), precision: 6);
    }

    [Theory]
    [InlineData(46.0)]
    [InlineData(25.0)]
    [InlineData(15.0)]
    public void GlowBlur_ScalesWithTheFontSize(double fontSize)
    {
        var effect = Assert.IsType<Avalonia.Media.BlurEffect>(
            new Noctis.Converters.EmphasisGlowBlurConverter().Convert(fontSize, typeof(object), null,
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(fontSize * Noctis.Converters.EmphasisGlowBlurConverter.RadiusEm, effect.Radius, precision: 1);
    }

    // ── The timeline ────────────────────────────────────────────────────────────────

    private static void At(LyricsTimeline t, double seconds) =>
        t.Update(TimeSpan.FromSeconds(seconds) - Lookahead);

    [Fact]
    public void SungWords_FloatUp_InOrder_AndUnsungWordsStayDown()
    {
        var lines = Parse(Sample);
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        var w = lines[0].Words!;

        At(t, 17.30); // "had" just started, "I" (16.47) 830 ms in, "Where" 1.43 s in
        Assert.Equal(1, w[0].Lift);                              // Where: settled
        Assert.True(w[3].Lift is > 0.85 and < 1, $"I {w[3].Lift}");
        Assert.True(w[4].Lift is > 0 and < 0.35, $"had {w[4].Lift}");
        Assert.Equal(0, w[5].Lift);                              // to: not sung yet
        Assert.Equal(0, w[^1].Lift);

        // Lift only grows while the line is sung: one smooth wave, no hops back down.
        var prev = w.Select(x => x.Lift).ToArray();
        for (double s = 17.30; s < 19.80; s += 1 / 60.0)
        {
            At(t, s);
            for (int i = 0; i < w.Count; i++)
            {
                Assert.True(w[i].Lift >= prev[i] - 1e-12, $"word {i} dipped at {s:F3}");
                prev[i] = w[i].Lift;
            }
        }
    }

    [Fact]
    public void FinishedLine_SettlesBack_AndTheClockKeepsRunningUntilItHas()
    {
        // No held notes here, so only the float can keep the frame clock alive.
        var lines = Parse(
            "[00:10.00]<00:10.00>So <00:10.20>we <00:10.40>go<00:10.60>\n" +
            "[00:11.00]<00:11.00>on <00:11.20>and<00:11.40>\n");
        Assert.DoesNotContain(lines.SelectMany(l => l.Words!), x => x.IsEmphasis);
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        var so = lines[0].Words![0];
        var go = lines[0].Words![2];

        At(t, 10.95);
        var soAtEnd = so.Lift;
        var goAtEnd = go.Lift;
        Assert.True(soAtEnd > 0.9 && goAtEnd is > 0.5 and < 1, $"{soAtEnd} {goAtEnd}");
        Assert.False(t.HasEmphasisInFlight);

        At(t, 11.05); // line 2 took over: the fall starts from where each word was
        Assert.False(lines[0].IsActive);
        Assert.True(so.Lift > 0.9);
        Assert.True(t.HasEmphasisInFlight);

        At(t, 11.05 + WordFloat.FallMs / 2000);
        Assert.True(so.Lift is > 0.2 and < 0.8, $"mid-fall {so.Lift}");
        Assert.True(go.Lift < so.Lift);

        At(t, 11.05 + WordFloat.FallMs / 1000 + 0.05);
        Assert.All(lines[0].Words!, x => Assert.Equal(0, x.Lift));
        Assert.False(t.HasEmphasisInFlight);
    }

    [Fact]
    public void SeekBack_IntoTheLine_DrivesItsWordsAgain()
    {
        var lines = Parse(Sample);
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        var w = lines[0].Words!;
        At(t, 20.60);
        t.Rewind();
        At(t, 16.20); // "you" just started
        Assert.True(lines[0].IsActive);
        Assert.True(w[0].Lift is > 0 and < 1);
        Assert.Equal(0, w[4].Lift);
        At(t, 16.20 + 1.0);
        Assert.True(w[0].Lift > 0.9);
    }

    [Fact]
    public void WordMotionOff_KeepsEveryWordStill_AndSettlesLiftedOnesAtOnce()
    {
        var lines = Parse(Sample);
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        var w = lines[0].Words!;
        At(t, 18.80); // mid "goodbye", a held note
        var goodbye = w.First(x => x.IsEmphasis);
        Assert.False(double.IsNaN(goodbye.EmphasisMs));
        Assert.True(w[0].Lift > 0);

        t.WordMotion = false;
        Assert.All(w, x => Assert.Equal(0, x.Lift));
        Assert.True(double.IsNaN(goodbye.EmphasisMs));

        for (double s = 18.80; s < 21.5; s += 0.05)
        {
            At(t, s);
            Assert.All(lines.SelectMany(l => l.Words!), x => Assert.Equal(0, x.Lift));
            Assert.All(lines.SelectMany(l => l.Words!), x => Assert.True(double.IsNaN(x.EmphasisMs)));
        }
        // The sweep itself is untouched.
        Assert.True(lines[1].Words![0].Progress > 0);

        t.WordMotion = true;
        At(t, 21.5);
        Assert.True(lines[1].Words![0].Lift > 0);
    }

    [Fact]
    public void Reset_PutsEveryWordBackAtRest()
    {
        var lines = Parse(Sample);
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        At(t, 18.0);
        Assert.Contains(lines[0].Words!, x => x.Lift > 0);
        t.Reset();
        Assert.All(lines[0].Words!, x => Assert.Equal(0, x.Lift));
        Assert.False(t.HasEmphasisInFlight);
    }

    /// <summary>Settings → Lyrics "Glow &amp; Lift" (default on) reaches the running line engine.</summary>
    // On the UI thread: the view model subscribes to app-wide accent changes and builds
    // brushes, which must belong to the dispatcher thread the later tests run on.
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void GlowAndLiftSetting_DefaultsOn_AndDrivesTheLyricsEngine()
    {
        Assert.True(new AppSettings().LyricsGlowAndLift);
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        Assert.True(player.LyricsGlowAndLift);
        var vm = new LyricsViewModel(player, new NullLrcLib(), new NullNetEase(), new NullMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
        var timeline = (LyricsTimeline)typeof(LyricsViewModel)
            .GetField("_timeline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(vm)!;
        Assert.True(timeline.WordMotion);
        player.LyricsGlowAndLift = false;
        Assert.False(timeline.WordMotion);
        player.LyricsGlowAndLift = true;
        Assert.True(timeline.WordMotion);
    }

    private sealed class NullLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class NullNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class NullMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    /// <summary>Line-synced (non word-synced) lyrics have no words to float: the timeline
    /// runs as before and nothing is in flight to keep the frame clock alive.</summary>
    [Fact]
    public void LineSyncedLyrics_AreUnaffected()
    {
        var lines = Parse("[00:10.00]First line\n[00:12.00]Second line\n[00:14.00]Third line\n");
        Assert.All(lines, l => Assert.False(l.HasWords));
        var t = new LyricsTimeline(lines, Lookahead, Lookahead);
        foreach (var s in new[] { 10.5, 12.5, 14.5, 20.0 })
        {
            var step = t.Update(TimeSpan.FromSeconds(s));
            Assert.NotNull(step.ActiveLine);
            Assert.False(t.HasEmphasisInFlight);
        }
        Assert.Same(lines[2], t.ActiveLine);
    }
}
