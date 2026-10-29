using System.Net.Http;
using System.Text;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>#200 lane: order of work, write only after a fit, remembered misses vs failures, the user's own timing
/// left alone. Temp folder, fake sources, fake fit check; no network, no ffmpeg.</summary>
public sealed class OnlineTimingLaneTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-lane-" + Guid.NewGuid().ToString("N"));

    sealed class Source : ILrcRedLyricsService, IUnisonLyricsService, IKugouLyricsService
    {
        public Func<LrcLibResult?> Answer = () => null;
        public int Calls;
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        { Calls++; return Task.FromResult(Answer()); }
        public Task<LrcRedMatch?> FindMatchAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default) => Task.FromResult<LrcRedMatch?>(null);
        public Task<LrcLibResult> SyncWithModelAsync(string isrc, CancellationToken ct = default) => throw new InvalidOperationException("never automatic");
    }

    readonly Source _lrcRed = new(), _unison = new(), _kugou = new();
    readonly HashSet<string> _userTimed = new(StringComparer.OrdinalIgnoreCase);
    string? _ffmpeg = "ffmpeg.exe";
    double? _fit = 0.48;
    int _checks;

    public OnlineTimingLaneTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static string WordTtml()
    {
        var b = new StringBuilder("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div>");
        for (int l = 0; l < 5; l++)
        {
            b.Append($"<p begin=\"{10 + l * 4}.000\" end=\"{13 + l * 4}.000\">");
            for (int w = 0; w < 8; w++) b.Append($"<span begin=\"{10 + l * 4 + w * 0.3:0.000}\" end=\"{10 + l * 4 + w * 0.3 + 0.25:0.000}\">w{w}</span> ");
            b.Append("</p>");
        }
        return b.Append("</div></body></tt>").ToString();
    }

    Track Song(string name, SourceType source = SourceType.Local)
    {
        var path = Path.Combine(_dir, name + ".mp3");
        File.WriteAllBytes(path, [1, 2, 3]);
        return new Track { FilePath = path, Title = name, Artist = "Artist", Duration = TimeSpan.FromSeconds(200), SourceType = source };
    }

    OnlineTimingLane Lane() => new(new OnlineWordTiming(_lrcRed, _unison, _kugou, () => true), new OnlineTimingMisses(Path.Combine(_dir, "misses.json")),
        new LyricsWriter(null!, null, new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache")),
        () => _ffmpeg, () => false, _userTimed.Contains)
    {
        Check = (_, _, ttml, _) =>
        {
            _checks++;
            return Task.FromResult<OnlineTimingFit.Result?>(new(_fit, _fit ?? 0, 0.3, _fit ?? 0, 3, OnlineTimingFit.WordsOf(ttml).Count));
        },
    };

    [Fact]
    public void PlayingThenQueueThenLibrary()
    {
        using var lane = Lane();
        Track a = Song("a"), b = Song("b"), c = Song("c"), d = Song("d"), stream = Song("s", SourceType.Jellyfin);
        lane.SetLibrary([d, a]);
        lane.SetPlaying(stream, [b, c]);
        Assert.Same(b, lane.Next(libraryOpen: false));   // a media-server stream is not a file to write beside
        lane.SetPlaying(c, [b]);
        Assert.Same(c, lane.Next(false));
        lane.SetPlaying(null, []);
        Assert.Null(lane.Next(false));                   // the library waits for its first delay
        Assert.Same(d, lane.Next(true));
    }

    [Fact]
    public async Task ASongWithWordTimingIsLeftAsItIs_NoLookup()
    {
        var song = Song("timed");
        File.WriteAllText(Path.ChangeExtension(song.FilePath, ".elrc"), "[00:10.00]<00:10.00>w0 <00:10.30>");
        _lrcRed.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        Assert.Equal(OnlineTimingLane.Outcome.AlreadyTimed, await Lane().ProcessAsync(song, CancellationToken.None));
        Assert.Equal(0, _lrcRed.Calls + _unison.Calls + _kugou.Calls);
        Assert.False(File.Exists(Path.ChangeExtension(song.FilePath, ".ttml")));
    }

    [Fact]
    public async Task AFittingTimingIsWrittenShiftedAndMarked_TheLrcStays()
    {
        using var lane = Lane();
        var song = Song("Kiss Me More");
        var lrc = Path.ChangeExtension(song.FilePath, ".lrc");
        File.WriteAllText(lrc, "[00:10.00]w0 w1");
        _lrcRed.Answer = () => new LrcLibResult { Duration = 200.3, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        IReadOnlyList<string>? written = null;
        lane.Written += p => written = p;
        Assert.Equal(OnlineTimingLane.Outcome.Written, await lane.ProcessAsync(song, CancellationToken.None));
        var ttml = File.ReadAllText(Path.ChangeExtension(song.FilePath, ".ttml"));
        Assert.Equal(WordTimingSource.LrcRed, OnlineTimingSidecar.SourceOf(ttml));
        Assert.Equal(10.48, OnlineTimingFit.WordsOf(ttml)[0].Start, 3);
        Assert.Equal("[00:10.00]w0 w1", File.ReadAllText(lrc));
        Assert.Equal([song.FilePath], written);
        lane.SetPlaying(song, []);
        Assert.Null(lane.Next(true));                                    // done for this run
        Assert.Equal(OnlineTimingLane.Outcome.AlreadyTimed, await Lane().ProcessAsync(song, CancellationToken.None)); // next start: no lookup
        Assert.Equal(1, _lrcRed.Calls);
    }

    [Fact]
    public async Task AnUnsureFitKeepsTheTimingAsItIs_NoSourceIsRememberedAcrossStarts()
    {
        // Owner 2026-10-29 (Circo Loco): unsure is not wrong; the online timing stays, unmoved.
        var unsure = Song("unsure");
        var nowhere = Song("nowhere");
        _unison.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        _fit = null;
        Assert.Equal(OnlineTimingLane.Outcome.Written, await Lane().ProcessAsync(unsure, CancellationToken.None));
        var ttml = File.ReadAllText(Path.ChangeExtension(unsure.FilePath, ".ttml"));
        Assert.Equal(10.0, OnlineTimingFit.WordsOf(ttml)[0].Start, 3);
        Assert.Contains("plus:shift=\"0.00\"", ttml);
        _unison.Answer = () => null;
        Assert.Equal(OnlineTimingLane.Outcome.Miss, await Lane().ProcessAsync(nowhere, CancellationToken.None));
        var calls = _lrcRed.Calls + _unison.Calls + _kugou.Calls;
        Assert.Equal(OnlineTimingLane.Outcome.Miss, await Lane().ProcessAsync(nowhere, CancellationToken.None));
        Assert.Equal(calls, _lrcRed.Calls + _unison.Calls + _kugou.Calls);
    }

    [Fact]
    public async Task AFailureIsNeitherRememberedNorDone()
    {
        using var lane = Lane();
        var song = Song("offline");
        lane.SetPlaying(song, []);
        _lrcRed.Answer = () => throw new LyricsProviderException("lrc.red", new HttpRequestException("offline"));
        Assert.Equal(OnlineTimingLane.Outcome.Transient, await lane.ProcessAsync(song, CancellationToken.None));
        Assert.Same(song, lane.Next(false));
        _lrcRed.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        Assert.Equal(OnlineTimingLane.Outcome.Written, await lane.ProcessAsync(song, CancellationToken.None));
    }

    [Fact]
    public async Task TheUsersOwnTimingAndMissingDecoderAreLeftAlone()
    {
        _lrcRed.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        var studio = Song("studio");
        _userTimed.Add(studio.FilePath);
        Assert.Equal(OnlineTimingLane.Outcome.Skipped, await Lane().ProcessAsync(studio, CancellationToken.None));
        var own = Song("own");
        File.WriteAllText(Path.ChangeExtension(own.FilePath, ".ttml"), WordTtml());
        Assert.Equal(OnlineTimingLane.Outcome.Skipped, await Lane().ProcessAsync(own, CancellationToken.None));
        var lyricsfile = Song("lyricsfile");
        File.WriteAllText(Path.ChangeExtension(lyricsfile.FilePath, ".lyricsfile"), "x");
        Assert.Equal(OnlineTimingLane.Outcome.Skipped, await Lane().ProcessAsync(lyricsfile, CancellationToken.None));
        Assert.Equal(0, _lrcRed.Calls);
        _ffmpeg = null;   // no decoder: the timing is written as found, unchecked
        var plain = Song("plain");
        Assert.Equal(OnlineTimingLane.Outcome.Written, await Lane().ProcessAsync(plain, CancellationToken.None));
        Assert.Equal(0, _checks);
    }

    [Fact]
    public async Task AStreamShowsItsTimingAtOnce_TheFinishedFileThenGetsItFitted()
    {
        using var lane = Lane();
        var song = Song("streamed");
        _lrcRed.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        (Track Track, string Ttml, string Source)? shown = null;
        lane.Provisional += (t, ttml, source) => shown = (t, ttml, source);
        Assert.True(await lane.ShowWhileStreamingAsync(song, CancellationToken.None));
        Assert.Same(song, shown!.Value.Track);
        Assert.Equal("lrc.red", shown.Value.Source);
        Assert.Equal(WordTimingSource.LrcRed, OnlineTimingSidecar.SourceOf(shown.Value.Ttml));
        Assert.False(await lane.ShowWhileStreamingAsync(song, CancellationToken.None));   // once per song
        Assert.Equal(0, _checks);                                                        // no file needed to show it
        // WordLyrics' file for the finished stream landed meanwhile: the online timing shown still gets its fit check.
        File.WriteAllText(Path.ChangeExtension(song.FilePath, ".elrc"), "[00:10.00]<00:10.00>w0 <00:10.30>");
        Assert.Equal(OnlineTimingLane.Outcome.Written, await lane.ProcessAsync(song, CancellationToken.None));
        Assert.Equal(1, _lrcRed.Calls);   // the answer found at Play is reused
        Assert.Equal(1, _checks);
    }

    [Fact]
    public async Task AStreamedTimingTheFitCheckIsUnsureOfIsNeverTakenBack()
    {
        // Owner 2026-10-29: Circo Loco's lrc.red timing showed at Play, then vanished for WordLyrics.
        using var lane = Lane();
        var song = Song("Circo Loco");
        _lrcRed.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        IReadOnlyList<string>? written = null;
        lane.Written += p => written = p;
        Assert.True(await lane.ShowWhileStreamingAsync(song, CancellationToken.None));
        _fit = null;
        Assert.Equal(OnlineTimingLane.Outcome.Written, await lane.ProcessAsync(song, CancellationToken.None));
        Assert.Equal([song.FilePath], written);
        Assert.Equal(WordTimingSource.LrcRed, OnlineTimingSidecar.SourceOf(File.ReadAllText(Path.ChangeExtension(song.FilePath, ".ttml"))));
    }

    [Fact]
    public async Task AStreamNoSourceHasIsNotShownAndIsRemembered()
    {
        using var lane = Lane();
        var song = Song("nothing online");
        var shown = false;
        lane.Provisional += (_, _, _) => shown = true;
        Assert.False(await lane.ShowWhileStreamingAsync(song, CancellationToken.None));
        Assert.False(shown);
        Assert.Equal(OnlineTimingLane.Outcome.Miss, await lane.ProcessAsync(song, CancellationToken.None));
        Assert.Equal(1, _lrcRed.Calls);
    }

    [Fact]
    public async Task TheLoopTimesThePlayingSongWithoutWaitingForTheLibrary()
    {
        using var lane = Lane();
        lane.Spacing = TimeSpan.FromMilliseconds(10);
        var song = Song("now");
        _lrcRed.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        var written = new TaskCompletionSource<IReadOnlyList<string>>();
        lane.Written += p => written.TrySetResult(p);
        lane.Start(TimeSpan.FromHours(1));
        lane.SetPlaying(song, []);
        var done = await Task.WhenAny(written.Task, Task.Delay(5000));
        Assert.Same(written.Task, done);
    }

    /// <summary>Opt-in, real sources + real ffmpeg on a scratch COPY of a song (FIT200_LANE = "copy.mp3|artist|title|seconds",
    /// FIT200_FFMPEG = ffmpeg.exe). Writes only beside that copy.</summary>
    [Fact]
    public async Task LiveOnAScratchCopy()
    {
        var spec = Environment.GetEnvironmentVariable("FIT200_LANE")?.Split('|');
        var ffmpeg = Environment.GetEnvironmentVariable("FIT200_FFMPEG");
        if (spec is not { Length: 4 } || string.IsNullOrEmpty(ffmpeg) || spec[0].StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase)) return;
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var lane = new OnlineTimingLane(new OnlineWordTiming(new LrcRedLyricsService(http), new UnisonLyricsService(http), new KugouLyricsService(http), () => true),
            new OnlineTimingMisses(Path.Combine(_dir, "misses.json")),
            new LyricsWriter(null!, null, new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache")),
            () => ffmpeg, () => false, _ => false);
        var track = new Track { FilePath = spec[0], Artist = spec[1], Title = spec[2], Duration = TimeSpan.FromSeconds(double.Parse(spec[3], System.Globalization.CultureInfo.InvariantCulture)) };
        var elrc = Path.ChangeExtension(spec[0], ".elrc");
        var before = File.Exists(elrc) ? File.ReadAllText(elrc) : null;
        var outcome = await lane.ProcessAsync(track, CancellationToken.None);
        var ttml = Path.ChangeExtension(spec[0], ".ttml");
        Console.WriteLine($"LANE-LIVE outcome {outcome}; ttml {(File.Exists(ttml) ? OnlineTimingSidecar.SourceOf(File.ReadAllText(ttml)) + " " + System.Text.RegularExpressions.Regex.Match(File.ReadAllText(ttml), "plus:shift=\"[^\"]*\"").Value : "none")}");
        Assert.Equal(before, File.Exists(elrc) ? File.ReadAllText(elrc) : null);
    }
}
