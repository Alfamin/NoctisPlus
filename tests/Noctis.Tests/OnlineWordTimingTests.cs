using System.Net.Http;
using System.Text;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>#200 automatic online word timing (lrc.red, Unison; Kugou left out, owner #208): source order, word-synced only, same length, failures vs misses. No network.</summary>
public class OnlineWordTimingTests
{
    sealed class Source : ILrcRedLyricsService, IUnisonLyricsService
    {
        public Func<LrcLibResult?> Answer = () => null;
        public int Calls;
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Answer());
        }
        public Task<LrcRedMatch?> FindMatchAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) => Task.FromResult<LrcRedMatch?>(null);
        public Task<LrcLibResult> SyncWithModelAsync(string isrc, CancellationToken ct = default) => throw new InvalidOperationException("never automatic");
    }

    static string WordTtml(int words = 40)
    {
        var b = new StringBuilder("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div>");
        for (int l = 0; l < words / 8; l++)
        {
            b.Append($"<p begin=\"{10 + l * 4}.000\" end=\"{13 + l * 4}.000\">");
            for (int w = 0; w < 8; w++) b.Append($"<span begin=\"{10 + l * 4 + w * 0.3:0.000}\" end=\"{10 + l * 4 + w * 0.3 + 0.25:0.000}\">w{w}</span> ");
            b.Append("</p>");
        }
        return b.Append("</div></body></tt>").ToString();
    }

    const string LineTtml = "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"10.000\" end=\"12.000\">a line</p></div></body></tt>";

    static string WordElrc(int lines = 6)
    {
        var b = new StringBuilder();
        for (int l = 0; l < lines; l++)
        {
            var t = 10 + l * 4;
            b.Append($"[00:{t:00}.00]");
            for (int w = 0; w < 8; w++) b.Append($"<00:{t + w * 0.3:00.00}>w{w} ");
            b.Append($"<00:{t + 3:00.00}>\n");
        }
        return b.ToString();
    }

    static LrcLibResult Word(double duration = 200) => new() { Duration = duration, SyncedLyrics = "x", PlainLyrics = "x", OriginalTtml = WordTtml() };
    static LyricsProviderException Down() => new("test", new HttpRequestException("offline"));

    (OnlineWordTiming Timing, Source LrcRed, Source Unison) Make(bool lrcRed = true)
    {
        Source a = new(), b = new();
        return (new OnlineWordTiming(a, b, () => lrcRed), a, b);
    }

    [Fact]
    public async Task LrcRedFirstAndItsOwnDocument()
    {
        var (t, lrcRed, unison) = Make();
        var answer = Word();
        lrcRed.Answer = () => answer;
        unison.Answer = () => Word();
        var found = await t.FindAsync("A", "B", 200.4, CancellationToken.None);
        Assert.Equal(WordTimingSource.LrcRed, found.Source);
        Assert.Same(answer.OriginalTtml, found.Ttml);
        Assert.Equal(1, unison.Calls);   // asked at the same time; lrc.red still wins
    }

    [Fact]
    public async Task BothSourcesAreAskedAtOnce()
    {
        var slow = new TaskCompletionSource<LrcLibResult?>();
        var started = 0;
        var timing = new OnlineWordTiming(new Gate(slow, () => started++), new Gate(slow, () => started++), () => true);
        var find = timing.FindAsync("A", "B", 200, CancellationToken.None);
        Assert.Equal(2, started);        // nobody waited for the one before
        slow.SetResult(Word());
        Assert.Equal(WordTimingSource.LrcRed, (await find).Source);
    }

    [Fact]
    public async Task AReadyUnisonAnswerWaitsForASlowLrcRedOnlyAMoment()
    {
        // Owner #208: answers came 20-25 s late. A word-timed Unison answer is used once lrc.red has had PreferWait.
        var never = new TaskCompletionSource<LrcLibResult?>();
        var ready = new TaskCompletionSource<LrcLibResult?>(); ready.SetResult(Word());
        var timing = new OnlineWordTiming(new Gate(never, () => { }), new Gate(ready, () => { }), () => true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var found = await timing.FindAsync("A", "B", 200, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WordTimingSource.Unison, found.Source);
        Assert.InRange(clock.Elapsed.TotalSeconds, OnlineWordTiming.PreferWait.TotalSeconds - 0.2, OnlineWordTiming.PreferWait.TotalSeconds + 2);
    }

    sealed class Gate(TaskCompletionSource<LrcLibResult?> answer, Action started) : ILrcRedLyricsService, IUnisonLyricsService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) { started(); return answer.Task; }
        public Task<LrcRedMatch?> FindMatchAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) => Task.FromResult<LrcRedMatch?>(null);
        public Task<LrcLibResult> SyncWithModelAsync(string isrc, CancellationToken ct = default) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task LineTimedOrOtherLengthGoesToTheNextSource()
    {
        var (t, lrcRed, unison) = Make();
        lrcRed.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "[00:10.00]a line", OriginalTtml = LineTtml };
        unison.Answer = () => Word(duration: 199.1);
        var found = await t.FindAsync("A", "B", 200, CancellationToken.None);
        Assert.Equal(WordTimingSource.Unison, found.Source);
        Assert.False(found.Transient);
        unison.Answer = () => Word(duration: 201.5);
        Assert.False((await t.FindAsync("A", "B", 200, CancellationToken.None)).Found);
    }

    [Fact]
    public async Task AnElrcAnswerIsCarriedAsTtml_AndAFailedSourceIsTransient()
    {
        var (t, lrcRed, unison) = Make();
        lrcRed.Answer = () => throw Down();
        unison.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = WordElrc(), PlainLyrics = "x" };
        var found = await t.FindAsync("A", "B", 200, CancellationToken.None);
        Assert.Equal(WordTimingSource.Unison, found.Source);
        Assert.True(found.Transient);    // lrc.red could not answer: not a definitive miss for it
        var words = OnlineTimingFit.WordsOf(found.Ttml);
        Assert.Equal(48, words.Count);
        Assert.Equal(10.3, words[1].Start, 2);
    }

    [Fact]
    public async Task NothingAnywhereIsAMissAndAFailureIsNot()
    {
        var (t, lrcRed, _) = Make();
        var miss = await t.FindAsync("A", "B", 200, CancellationToken.None);
        Assert.False(miss.Found);
        Assert.False(miss.Transient);
        lrcRed.Answer = () => throw Down();
        var failed = await t.FindAsync("A", "B", 200, CancellationToken.None);
        Assert.False(failed.Found);
        Assert.True(failed.Transient);
    }

    [Fact]
    public async Task LrcRedOffIsNotAsked()
    {
        var (t, lrcRed, unison) = Make(lrcRed: false);
        lrcRed.Answer = () => Word();
        unison.Answer = () => Word();
        var found = await t.FindAsync("A", "B", 200, CancellationToken.None);
        Assert.Equal(WordTimingSource.Unison, found.Source);
        Assert.Equal(0, lrcRed.Calls);
    }

    [Fact]
    public void TooFewTimedWordsIsNotWordTiming()
    {
        Assert.Null(OnlineWordTiming.WordTimed(new LrcLibResult { Duration = 200, OriginalTtml = WordTtml(16) }, 200));
        Assert.Null(OnlineWordTiming.WordTimed(new LrcLibResult { Duration = 200, OriginalTtml = WordTtml(), Instrumental = true }, 200));
        Assert.NotNull(OnlineWordTiming.WordTimed(new LrcLibResult { Duration = 0, OriginalTtml = WordTtml() }, 200)); // length unknown: the fit check decides
    }

    [Fact]
    public void MissesAreRememberedForDaysAcrossStartsAndBounded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "noctis-misses-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "online-timing-misses.json");
        try
        {
            var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
            var key = OnlineTimingMisses.Key(" Drake ", "Nonstop", 238.6);
            Assert.Equal(OnlineTimingMisses.Key("drake", "NONSTOP", 239.2), key);
            var misses = new OnlineTimingMisses(path);
            misses.Remember(key, now);
            Assert.True(new OnlineTimingMisses(path).IsMiss(key, now.AddDays(6)));
            Assert.False(new OnlineTimingMisses(path).IsMiss(key, now.AddDays(8)));
            misses.Forget(key);
            Assert.False(new OnlineTimingMisses(path).IsMiss(key, now));
            File.WriteAllText(path, "{ not json");
            var damaged = new OnlineTimingMisses(path);
            Assert.False(damaged.IsMiss(key, now));
            damaged.Remember(key, now);
            Assert.True(new OnlineTimingMisses(path).IsMiss(key, now));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
