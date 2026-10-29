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
    bool _offline;
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

    OnlineTimingLane Lane() => new(new OnlineWordTiming(_lrcRed, _unison, () => true, _kugou), new OnlineTimingMisses(Path.Combine(_dir, "misses.json")),
        new LyricsWriter(null!, null, new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache")) { TrashFile = p => { File.Delete(p); return true; } },
        () => _ffmpeg, () => _offline, _userTimed.Contains)
    {
        Spacing = TimeSpan.Zero,
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
        Assert.Equal(0, _lrcRed.Calls + _unison.Calls);
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
        var calls = _lrcRed.Calls + _unison.Calls;
        Assert.Equal(OnlineTimingLane.Outcome.Miss, await Lane().ProcessAsync(nowhere, CancellationToken.None));
        Assert.Equal(calls, _lrcRed.Calls + _unison.Calls);
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
    public async Task WordLyricsWaitsForTheOnlineSourcesFirst_ForASongFromAnywhere()
    {
        // Owner #207 (hard rule): a song fetched ahead kept getting WordLyrics' timing, never the online one. WordLyrics
        // asks first; the lane looks at that song before the library (even before the library pass opens).
        using var lane = Lane();
        var library = Song("downloaded");
        lane.SetLibrary([Song("old library song"), library]);
        _lrcRed.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        Assert.True(lane.WantsFirst(library.FilePath));
        Assert.Same(library, lane.Next(libraryOpen: false));
        Assert.Equal(OnlineTimingLane.Outcome.Written, await lane.ProcessAsync(library, CancellationToken.None));
        Assert.False(lane.WantsFirst(library.FilePath));   // answered: WordLyrics finds the .ttml and lets it be

        var nothing = Song("nothing online");
        lane.SetLibrary([nothing]);
        _lrcRed.Answer = () => null;
        Assert.True(lane.WantsFirst(nothing.FilePath));
        Assert.Equal(OnlineTimingLane.Outcome.Miss, await lane.ProcessAsync(nothing, CancellationToken.None));
        Assert.False(lane.WantsFirst(nothing.FilePath));   // nothing online: WordLyrics goes on
    }

    [Fact]
    public async Task KugouIsThird_AfterLrcRedAndUnison_BeforeWordLyrics()
    {
        // Owner #209: lrc.red, then the better of Unison and Kugou (measured: Unison), then the other, WordLyrics last.
        _kugou.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        var onlyKugou = Song("only on Kugou");
        Assert.Equal(OnlineTimingLane.Outcome.Written, await Lane().ProcessAsync(onlyKugou, CancellationToken.None));
        Assert.Equal(WordTimingSource.Kugou, OnlineTimingSidecar.SourceOf(File.ReadAllText(Path.ChangeExtension(onlyKugou.FilePath, ".ttml"))));

        _unison.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        var both = Song("on Unison and Kugou");
        Assert.Equal(OnlineTimingLane.Outcome.Written, await Lane().ProcessAsync(both, CancellationToken.None));
        Assert.Equal(WordTimingSource.Unison, OnlineTimingSidecar.SourceOf(File.ReadAllText(Path.ChangeExtension(both.FilePath, ".ttml"))));

        _lrcRed.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        var all = Song("everywhere");
        Assert.Equal(OnlineTimingLane.Outcome.Written, await Lane().ProcessAsync(all, CancellationToken.None));
        Assert.Equal(WordTimingSource.LrcRed, OnlineTimingSidecar.SourceOf(File.ReadAllText(Path.ChangeExtension(all.FilePath, ".ttml"))));
    }

    [Theory]
    [InlineData(true)]    // the fit check is unsure
    [InlineData(false)]   // no ffmpeg: nothing to check it with
    public async Task KugouIsUsedOnlyWithAFitTheCheckIsSureOf_ElseWordLyricsTimesIt(bool unsure)
    {
        // Owner #214: White Iverson from Kugou was far out of sync (its fit unsure: +0.47 s against the energy's -0.47 s).
        _kugou.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        if (unsure) _fit = null; else _ffmpeg = null;
        using var lane = Lane();
        var song = Song("White Iverson");
        Assert.True(lane.WantsFirst(song.FilePath));
        Assert.Equal(OnlineTimingLane.Outcome.Miss, await lane.ProcessAsync(song, CancellationToken.None));
        Assert.False(File.Exists(Path.ChangeExtension(song.FilePath, ".ttml")));
        Assert.False(lane.WantsFirst(song.FilePath));   // WordLyrics goes on
    }

    [Fact]
    public async Task AStreamFromKugouIsNotShownAtPlay_ItWaitsForTheFitCheckOnTheWholeFile()
    {
        _kugou.Answer = () => new LrcLibResult { Duration = 200, SyncedLyrics = "x", OriginalTtml = WordTtml() };
        using var lane = Lane();
        var song = Song("streamed from Kugou");
        var shown = false;
        lane.Provisional += (_, _, _) => shown = true;
        Assert.False(await lane.ShowWhileStreamingAsync(song, CancellationToken.None));
        Assert.False(shown);
        Assert.Equal(OnlineTimingLane.Outcome.Written, await lane.ProcessAsync(song, CancellationToken.None));   // sure fit
        Assert.Equal(1, _kugou.Calls);   // the answer found at Play is reused
        Assert.Equal(WordTimingSource.Kugou, OnlineTimingSidecar.SourceOf(File.ReadAllText(Path.ChangeExtension(song.FilePath, ".ttml"))));
    }

    [Fact]
    public void ABacklogWaitsItsTurn_OnlyTheSongBeingLookedAtCanRunOutOfTime()
    {
        // Owner #210: 300 songs waiting for WordLyrics after an update are all looked up online first, however long the
        // line; only a look that hangs lets WordLyrics go on for that song.
        using var lane = Lane();
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        lane.Now = () => now;
        var backlog = Enumerable.Range(0, 300).Select(i => Song("waiting " + i)).ToList();
        lane.SetLibrary(backlog);
        Assert.All(backlog, s => Assert.True(lane.WantsFirst(s.FilePath)));
        now += TimeSpan.FromMinutes(10);
        Assert.True(lane.WantsFirst(backlog[299].FilePath));   // still in line: still waits
        lane.StartedOn(backlog[0].FilePath);
        now += OnlineTimingLane.WaitLimit;
        Assert.False(lane.WantsFirst(backlog[0].FilePath));    // its look hung: WordLyrics goes on with it
        Assert.True(lane.WantsFirst(backlog[1].FilePath));
    }

    [Fact]
    public void OfflineOrAPathNothingKnows_WordLyricsGoesOn()
    {
        using var lane = Lane();
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        lane.Now = () => now;
        _offline = true;
        Assert.False(lane.WantsFirst(Song("offline").FilePath));
        _offline = false;
        var unknown = Path.Combine(_dir, "not there.mp3");   // no file, no list: nothing to look it up by
        Assert.True(lane.WantsFirst(unknown));
        Assert.Null(lane.Next(false));                      // waits a moment (a new download may be listed soon)
        now += OnlineTimingLane.FindGrace;
        Assert.Null(lane.Next(false));
        Assert.False(lane.WantsFirst(unknown));
    }

    [Fact]
    public async Task ASongNoListKnowsIsLookedUpByItsOwnTags()
    {
        // Owner #210: a stream-cache copy or an old queue entry WordLyrics has waiting is in no list: its tags are used.
        using var lane = Lane();
        var audio = await ProgressiveDecoderTests.AudioAsync("mp3", _dir);
        var path = Path.Combine(_dir, "stream cache copy.mp3");
        File.WriteAllBytes(path, audio);
        using (var file = TagLib.File.Create(path)) { file.Tag.Title = "Movin' To The Sun"; file.Tag.Performers = ["HUGEL"]; file.Save(); }
        Assert.True(lane.WantsFirst(path));
        var found = lane.Next(false);
        Assert.NotNull(found);
        Assert.Equal(path, found!.FilePath);
        Assert.Equal("Movin' To The Sun", found.Title);
        Assert.Equal("HUGEL", found.Artist);
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
        var lane = new OnlineTimingLane(new OnlineWordTiming(new LrcRedLyricsService(http), new UnisonLyricsService(http), () => true),
            new OnlineTimingMisses(Path.Combine(_dir, "misses.json")),
            new LyricsWriter(null!, null, new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache")) { TrashFile = p => { File.Delete(p); return true; } },
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
