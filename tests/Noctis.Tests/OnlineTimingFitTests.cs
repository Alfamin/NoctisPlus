using System.Text.Json;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// #200 fit check: does an online word-timed TTML fit this recording? Synthetic frame signals with a known shift
/// (no audio, no ffmpeg), the TTML time shift, and an opt-in parity run against the measured songs
/// (FIT200_LIVE = fit200.jsonl from the scratch measurement; reads D:\music, writes nothing).
/// </summary>
public class OnlineTimingFitTests
{
    readonly ITestOutputHelper _out;
    public OnlineTimingFitTests(ITestOutputHelper output) => _out = output;

    static List<OnlineTimingFit.Word> Words(int seed, int count = 120)
    {
        var r = new Random(seed);
        var words = new List<OnlineTimingFit.Word>();
        double t = 8;
        for (int i = 0; i < count; i++)
        {
            var len = 0.15 + r.NextDouble() * 0.35;
            words.Add(new OnlineTimingFit.Word(t, t + len, i % 7 == 0));
            t += len + (i % 7 == 6 ? 1.0 + r.NextDouble() : 0.02 + r.NextDouble() * 0.12);
        }
        return words;
    }

    /// <summary>Flux bumps where the words really start (TTML + fluxShift); energy raised where they are really sung.</summary>
    static (List<double> Flux, List<double> Energy) Song(IReadOnlyList<OnlineTimingFit.Word> words, double fluxShift, double energyShift, int seed, double noise = 0.6)
    {
        var r = new Random(seed);
        int n = (int)((words[^1].End + 10) * 100);
        var flux = Enumerable.Range(0, n).Select(_ => r.NextDouble() * noise).ToList();
        var energy = Enumerable.Range(0, n).Select(_ => r.NextDouble() * noise).ToList();
        foreach (var w in words)
        {
            int a = (int)Math.Round((w.Start + fluxShift) * 100);
            for (int k = -2; k <= 2; k++) if (a + k is var i && i >= 0 && i < n) flux[i] += (w.First ? 2 : 1) * Math.Exp(-k * k / 2.0);
            int s = (int)Math.Round((w.Start + energyShift) * 100), e = (int)Math.Round((w.End + energyShift) * 100);
            for (int i = Math.Max(0, s); i < Math.Min(n, e); i++) energy[i] += 0.8;
        }
        return (flux, energy);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.03, 0.0)]   // inside the ignore band: left as it is
    [InlineData(0.10, 0.10)]  // SKINNY-sized
    [InlineData(0.48, 0.48)]  // Kiss Me More-sized
    [InlineData(-0.30, -0.30)]
    public void AKnownShiftIsFoundAndApplied(double real, double applied)
    {
        var words = Words(1);
        var (flux, energy) = Song(words, real, real, 2);
        var fit = OnlineTimingFit.Measure(flux, energy, words);
        Assert.NotNull(fit.Shift);
        Assert.Equal(applied, fit.Shift!.Value, 2);
    }

    [Fact]
    public void ABigShiftTheSecondMeasureDoesNotBackIsNoFit()
    {
        var words = Words(3);
        var (flux, energy) = Song(words, 0.5, 0.0, 4);
        var fit = OnlineTimingFit.Measure(flux, energy, words);
        Assert.Equal(0.5, fit.Estimate, 2);
        Assert.Null(fit.Shift);
    }

    [Fact]
    public void AudioWithNoRelationToTheWordsIsNoFit()
    {
        // Sung onsets at random moments (another recording, another arrangement), not on these words.
        var words = Words(5);
        var r = new Random(6);
        var other = Enumerable.Range(0, 300).Select(_ => r.NextDouble() * words[^1].End).Order()
            .Select((t, i) => new OnlineTimingFit.Word(t, t + 0.1 + r.NextDouble() * 0.4, i % 7 == 0)).ToList();
        for (int seed = 0; seed < 10; seed++)
        {
            var (flux, energy) = Song(other, 0, 0, seed);
            Assert.Null(OnlineTimingFit.Measure(flux, energy, words).Shift);
        }
    }

    [Fact]
    public void ShiftsAtTheEdgeOfTheSearchAreNoFit()
    {
        var words = Words(7);
        var (flux, energy) = Song(words, 1.2, 1.2, 8);
        Assert.Null(OnlineTimingFit.Measure(flux, energy, words).Shift);
    }

    [Fact]
    public void TooFewWordsOrTooLittleAudioIsNoFit()
    {
        var words = Words(9, count: OnlineTimingFit.MinWords - 1);
        var (flux, energy) = Song(words, 0, 0, 10);
        Assert.Null(OnlineTimingFit.Measure(flux, energy, words).Shift);
        var many = Words(11);
        var (f2, e2) = Song(many, 0, 0, 12);
        Assert.Null(OnlineTimingFit.Measure(f2.Take(1500).ToList(), e2.Take(1500).ToList(), many).Shift);
    }

    const string Ttml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\">" +
        "<head><metadata><ttm:agent type=\"person\" xml:id=\"v1\"/></metadata></head><body dur=\"3:05.000\">" +
        "<div begin=\"0:12.500\" end=\"1:02.250\"><p begin=\"0:12.500\" end=\"0:14.000\" ttm:agent=\"v1\">" +
        "<span begin=\"0:12.500\" end=\"0:13.000\">Hello</span> <span begin='13.1s' end='13.6s'>there</span> <span begin=\"13.700\" end=\"1:00:00.000\">friend</span>" +
        "<span ttm:role=\"x-bg\"><span begin=\"0:00.100\" end=\"0:00.300\">(oh)</span></span></p></div></body></tt>";

    [Fact]
    public void ShiftMovesEveryTimeAndKeepsEverythingElse()
    {
        var moved = OnlineTimingFit.Shift(Ttml, 0.49);
        Assert.Contains("<span begin=\"0:12.990\" end=\"0:13.490\">Hello</span>", moved);
        Assert.Contains("<span begin='13.590s' end='14.090s'>there</span>", moved);
        Assert.Contains("begin=\"14.190\" end=\"1:00:00.490\"", moved);
        Assert.Contains("<body dur=\"3:05.000\">", moved);          // a duration, not a time
        Assert.Contains("itunes:timing=\"Word\"", moved);
        Assert.Contains("<span begin=\"0:00.590\" end=\"0:00.790\">(oh)</span>", moved);
        var back = OnlineTimingFit.Shift(Ttml, -0.2);
        Assert.Contains("<span begin=\"0:00.000\" end=\"0:00.100\">(oh)</span>", back); // never before the start
        Assert.Same(Ttml, OnlineTimingFit.Shift(Ttml, 0));
    }

    [Fact]
    public void ShiftedDocumentReadsBackShiftedWords()
    {
        var before = OnlineTimingFit.WordsOf(Ttml);
        var after = OnlineTimingFit.WordsOf(OnlineTimingFit.Shift(Ttml, 0.49));
        Assert.Equal(3, before.Count);  // main voice only, background left out
        Assert.True(before[0].First);
        Assert.Equal(before.Select(w => Math.Round(w.Start + 0.49, 3)), after.Select(w => Math.Round(w.Start, 3)));
    }

    /// <summary>Same decisions as the scratch Python measurement on the owner's songs (opt-in; needs ffmpeg).</summary>
    [Fact]
    public async Task LiveParityWithTheMeasuredSongs()
    {
        var file = Environment.GetEnvironmentVariable("FIT200_LIVE");
        var ffmpeg = Environment.GetEnvironmentVariable("FIT200_FFMPEG");
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(ffmpeg)) return;
        var cache = @"C:\Users\Alfamine\lyrics-compare\lrcred-cache";
        var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(cache, "index.json"))).RootElement;
        int same = 0, total = 0;
        foreach (var line in File.ReadLines(file))
        {
            var row = JsonDocument.Parse(line).RootElement;
            var name = row.GetProperty("name").GetString()!;
            string? audio, ttml;
            if (name.StartsWith("C:"))
            {
                var dir = Path.Combine(@"C:\Users\Alfamine\lyrics-compare", name[2..]);
                audio = Directory.GetFiles(dir, "*[lrc.red].*").FirstOrDefault(p => !p.EndsWith(".ttml"));
                ttml = Directory.GetFiles(dir, "*[lrc.red].ttml").FirstOrDefault();
            }
            else
            {
                audio = new[] { ".flac", ".mp3", ".m4a" }.Select(e => Path.Combine(@"D:\music", name + e)).FirstOrDefault(File.Exists);
                ttml = Path.Combine(cache, index.GetProperty(name).GetProperty("ttml").GetString()!);
            }
            if (audio == null || ttml == null) continue;
            var started = DateTime.UtcNow;
            var fit = await OnlineTimingFit.CheckAsync(ffmpeg, audio, File.ReadAllText(ttml), CancellationToken.None);
            var py = row.GetProperty("flux-onset");
            var pyEst = py[0].GetDouble();
            total++;
            if (fit != null && Math.Abs(fit.Estimate - pyEst) <= 0.011) same++;
            _out.WriteLine($"{name,-50} ref {row.GetProperty("ref").GetDouble(),6:+0.00;-0.00} py {pyEst,6:+0.00;-0.00}/{py[2].GetDouble():0.00} " +
                           $"cs {fit?.Estimate,6:+0.00;-0.00}/{fit?.Sharpness:0.00} energy {fit?.EnergyEstimate:+0.00;-0.00} sig {fit?.Significance:0.0} py {row.GetProperty("sig").GetDouble():0.0} -> {(fit?.Shift is { } s ? s.ToString("+0.00;-0.00") : "no fit")} " +
                           $"{(DateTime.UtcNow - started).TotalSeconds:0.0}s");
        }
        _out.WriteLine($"same estimate as Python: {same}/{total}");
        Assert.True(same >= total * 0.9, $"{same}/{total}");
    }
}
