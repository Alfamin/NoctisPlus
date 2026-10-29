using System.Text.RegularExpressions;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #220: censored online word timing ("****" from lrc.red, "n**ga" from Kugou) gets its real words.</summary>
public class OnlineTimingUncensorTests(ITestOutputHelper output)
{
    static string Ttml(params string[] words)
    {
        var spans = string.Concat(words.Select((w, i) => $"<span begin=\"{i}.000\" end=\"{i}.500\">{w}</span> "));
        return $"<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"0.000\" end=\"{words.Length}.000\">{spans}</p></div></body></tt>";
    }

    static List<string> SpanWords(string ttml) =>
        Regex.Matches(ttml, @"<span\b[^>]*>([^<]*)</span>").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void AWholeWordMask_TakesTheCopysWordBetweenTheSameNeighbours_TimingUnchanged()
    {
        var ttml = Ttml("Pull", "up", "with", "my", "****,", "we", "gon'", "ride");
        var copy = OnlineTimingUncensor.WordsOf("[00:01.00]Pull up with my nigga, we gon' ride\n[00:05.00]Other words here");
        var (clean, filled, masked) = OnlineTimingUncensor.Apply(ttml, copy);
        Assert.Equal((1, 1), (filled, masked));
        Assert.Equal(["Pull", "up", "with", "my", "nigga,", "we", "gon'", "ride"], SpanWords(clean));
        Assert.Equal(Regex.Matches(ttml, "begin=\"[^\"]*\"").Select(m => m.Value), Regex.Matches(clean, "begin=\"[^\"]*\"").Select(m => m.Value));
    }

    [Fact]
    public void APartMask_IsReadFromItsLetters_WhenNoCopySettlesIt_AWholeMaskIsNeverGuessed()
    {
        var (clean, filled, masked) = OnlineTimingUncensor.Apply(Ttml("my", "N**gas", "on", "sh*t,", "****"), null);
        Assert.Equal((2, 3), (filled, masked));
        Assert.Equal(["my", "Niggas", "on", "shit,", "****"], SpanWords(clean));
    }

    [Fact]
    public void AWordTheCopyHasThatDoesNotFitTheMasksLetters_IsNotTaken()
    {
        // "f**k" must not become "nigga" because of the neighbours.
        var copy = OnlineTimingUncensor.WordsOf("I don't give a nigga about it");
        var (clean, _, _) = OnlineTimingUncensor.Apply(Ttml("I", "don't", "give", "a", "f**k", "about", "it"), copy);
        Assert.Equal("fuck", SpanWords(clean)[4]);
    }

    sealed class Copies(params LrcLibResult[] all) : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) =>
            Task.FromResult(all.FirstOrDefault());
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default) =>
            Task.FromResult(all.ToList());
    }

    [Fact]
    public async Task TheExplicitCopyWins_OverACleanVersionThatHidesWordsWithoutAsterisks()
    {
        // Lil Baby "Commercial": LRCLIB's exact match is a clean version ("(Whoa) talkin'"), the search has the real one.
        var clean = new LrcLibResult { Id = 1, Duration = 214, SyncedLyrics = "[00:11.71] Tay Keith, fuck these (whoa) up\n[01:39.11](Whoa) talkin' way too much" };
        var real = new LrcLibResult { Id = 2, Duration = 214, SyncedLyrics = "[00:11.71] Tay Keith, fuck these niggas up\n[01:39.13] Niggas talkin' way too much" };
        var stars = new LrcLibResult { Id = 3, Duration = 214, PlainLyrics = "these n****s up, n****s talkin'" };
        var words = await OnlineTimingUncensor.CopyAsync(new Copies(clean, stars, real), "Lil Baby, Lil Uzi Vert", "Commercial", 214, CancellationToken.None);
        Assert.Contains("niggas", words!);
        var (fixedTtml, filled, _) = OnlineTimingUncensor.Apply(Ttml("Tay", "Keith,", "fuck", "these", "****", "up", "****", "talkin'", "way", "too", "much"), words);
        Assert.Equal(2, filled);
        Assert.Equal("niggas", SpanWords(fixedTtml)[4]);
        Assert.Equal("Niggas", SpanWords(fixedTtml)[6]);
    }

    [Fact]
    public async Task LrclibSearchResultsWithNoLength_AreRead()
    {
        // A search result with "duration": null made the whole LRCLIB search fail (found by #220's check).
        var json = "[{\"id\":1,\"trackName\":\"beibs in the trap\",\"artistName\":\"Travis Scott\",\"duration\":null,\"instrumental\":false,\"plainLyrics\":\"x\"}," +
                   "{\"id\":2,\"trackName\":\"beibs in the trap\",\"artistName\":\"Travis Scott\",\"duration\":214.0,\"instrumental\":false,\"plainLyrics\":\"y\"}]";
        using var http = new HttpClient(new Answer(json));
        var found = await new LrcLibService(http).SearchLyricsAsync("Travis Scott", "beibs in the trap");
        Assert.Equal([0d, 214d], found.Select(r => r.Duration));
    }

    sealed class Answer(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public void ACleanTimingIsLeftAsItIs() =>
        Assert.False(OnlineTimingUncensor.Needs(Ttml("nothing", "masked", "here")));

    /// <summary>Opt-in: the owner's saved lrc.red / Kugou timings against LRCLIB. UNCENSOR_SURVEY = a folder of .ttml files.</summary>
    [Fact]
    public async Task Survey_SavedTimings()
    {
        var folder = Environment.GetEnvironmentVariable("UNCENSOR_SURVEY");
        if (string.IsNullOrEmpty(folder)) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var lrcLib = new LrcLibService(http);
        int files = 0, masked = 0, filled = 0, withCopy = 0;
        var report = Environment.GetEnvironmentVariable("UNCENSOR_OUT");
        void Report(string line) { output.WriteLine(line); if (report != null) File.AppendAllText(report, line + Environment.NewLine); }
        foreach (var file in Directory.EnumerateFiles(folder, "*.ttml", SearchOption.AllDirectories))
        {
            var ttml = File.ReadAllText(file);
            if (!OnlineTimingUncensor.Needs(ttml)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            var m = Regex.Match(name, @"^(?<artist>.+?) - (?<title>.+?)(?: \[[^\]]*\])*$");
            if (!m.Success) continue;
            var artist = m.Groups["artist"].Value.Split(',')[0].Trim();
            var title = m.Groups["title"].Value.Trim();
            LrcLibResult? copy = null;
            double seconds = 0;
            foreach (var ext in new[] { ".flac", ".mp3", ".m4a" })
                if (File.Exists(Path.ChangeExtension(file, ext)))
                    try { using var tags = TagLib.File.Create(Path.ChangeExtension(file, ext)); seconds = tags.Properties.Duration.TotalSeconds; artist = tags.Tag.FirstPerformer ?? artist; title = tags.Tag.Title ?? title; } catch { }
            var words = await OnlineTimingUncensor.CopyAsync(lrcLib, artist, title, seconds, CancellationToken.None) ?? [];
            if (Environment.GetEnvironmentVariable("UNCENSOR_DEBUG") is { } debug && name.Contains(debug, StringComparison.OrdinalIgnoreCase))
            {
                try { var g = await lrcLib.GetLyricsAsync(artist, title, seconds); Report($"  get({artist}|{title}|{seconds:0.0}) -> {(g == null ? "null" : g.Id + " " + g.Duration)}"); }
                catch (Exception e) { Report($"  get threw {e.GetType().Name}: {e.Message}"); }
                try { var s = await lrcLib.SearchLyricsAsync(artist, title); Report($"  search -> {s.Count}: {string.Join(", ", s.Select(r => r.Id + "/" + r.Duration + "/" + r.Instrumental))}"); }
                catch (Exception e) { Report($"  search threw {e.GetType().Name}: {e.Message}"); }
            }
            var (clean, f, n) = OnlineTimingUncensor.Apply(ttml, words.Count > 0 ? words : null);
            files++; masked += n; filled += f; if (words.Count > 0) withCopy++;
            var before = SpanWords(ttml); var after = SpanWords(clean);
            var samples = string.Join(", ", before.Select((w, i) => (w, a: after[i])).Where(p => p.w != p.a).Take(4).Select(p => $"{p.w}->{p.a}"));
            Report($"{f}/{n}  copy={(words.Count > 0 ? "yes" : "no ")}  {name}  {samples}");
        }
        Report($"TOTAL files={files} with LRCLIB copy={withCopy} masked={masked} filled={filled}");
    }
}
