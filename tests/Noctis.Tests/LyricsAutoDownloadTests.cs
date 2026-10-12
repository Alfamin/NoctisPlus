using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #135 on the lyrics page: "Automatically download lyrics" when a lyrics view opens on a
/// song with none, the "Preferred lyrics type" for what an automatic pick keeps, and online lyrics
/// saved per "Save lyrics to" — inside the audio file without touching lyrics the song already has.
/// </summary>
public class LyricsAutoDownloadTests : IDisposable
{
    private sealed class StubLrcLib : ILrcLibService
    {
        public Func<Task<LrcLibResult?>> GetImpl = () => Task.FromResult<LrcLibResult?>(null);
        public int Calls;

        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return GetImpl();
        }

        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class SettingsPersistence : TestPersistenceService
    {
        public AppSettings Settings { get; } = new() { NetEaseEnabled = false, KugouEnabled = false };
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
    }

    private const string Elrc = "[00:01.00]<00:01.00>word <00:01.50>timed<00:02.00>";
    private const string Lrc = "[00:01.00]line timed";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "auto135-" + Guid.NewGuid().ToString("N"));

    public LyricsAutoDownloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.Exists(_dir) ? Directory.GetFiles(_dir) : [])
            AppWrittenSidecarRegistry.Default.Remove(f);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static LrcLibResult Result(string? synced = null, string? plain = null) => new()
    {
        TrackName = "Test Song",
        ArtistName = "Test Artist",
        Duration = 200,
        SyncedLyrics = synced,
        PlainLyrics = plain ?? (synced == null ? null : LyricsTextHelper.StripTimestamps(synced)),
    };

    private sealed record Rig(LyricsViewModel Vm, PlayerViewModel Player, Track Track, StubLrcLib LrcLib);

    /// <summary>A lyrics page over a real audio file (a temp copy of a silent fixture) or a path with no file.</summary>
    private Rig Mount(StubLrcLib lrcLib, string? ext = null, Action<TagLib.File>? tag = null,
        bool autoDownload = true, string target = "Sidecar", string preferred = "WordSynced")
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            LyricsAutoDownload = autoDownload,
            LyricsSaveTarget = target,
            LyricsPreferredType = preferred,
        };
        var vm = new LyricsViewModel(player, lrcLib, new StubNetEase(), new MetadataService(), new SettingsPersistence(), new FakeLibraryService())
        {
            TrashSidecarFile = path => { File.Delete(path); return true; },
            ShowLyricsSearchDialog = _ => Task.CompletedTask,
        };
        var path = ext != null
            ? EmbeddedLyricsTagTests.Copy(_dir, ext, tag)
            : Path.Combine(_dir, $"nofile-{Guid.NewGuid():N}.mp3");
        var track = new Track
        {
            Title = "Test Song",
            Artist = "Test Artist",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = path,
            SourceType = SourceType.Local,
        };
        player.CurrentTrack = track;
        return new Rig(vm, player, track, lrcLib);
    }

    private static async Task PumpUntilAsync(Func<bool> done, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !done())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task DrainAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await LyricsViewModel.EnqueueLyricsFileWork(() => { });
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task SettleAsync(LyricsViewModel vm)
    {
        await PumpUntilAsync(() => false, 150);
        await PumpUntilAsync(() => !vm.IsSearching);
        await DrainAsync();
    }

    private string Side(Track t, string ext) => Path.ChangeExtension(t.FilePath, ext);

    // ── Automatic download ──

    [AvaloniaFact]
    public async Task ViewOpensOnASongWithNoLyrics_SearchesOnce_AndShowsWhatIsFound()
    {
        var rig = Mount(new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Lrc)) });
        rig.Vm.SetLyricsSurfaceVisible(true);

        rig.Vm.EnsureLyricsForCurrentTrack();
        await PumpUntilAsync(() => rig.Vm.LyricsSourceName == "LRCLIB");

        Assert.Equal(1, rig.LrcLib.Calls);
        Assert.Equal("LRCLIB", rig.Vm.LyricsSourceName);
        Assert.Contains(rig.Vm.LyricLines, l => l.Text == "line timed");
        await DrainAsync();
        Assert.True(File.Exists(Side(rig.Track, ".lrc"))); // saved per "Save lyrics to" (files, the default)
    }

    [AvaloniaFact]
    public async Task SongWithNothingOnline_IsNotSearchedAgain_WhenItsLyricsReopen()
    {
        var rig = Mount(new StubLrcLib());
        rig.Vm.SetLyricsSurfaceVisible(true);
        rig.Vm.EnsureLyricsForCurrentTrack();
        await SettleAsync(rig.Vm);
        Assert.Equal(1, rig.LrcLib.Calls);

        rig.Vm.SetLyricsSurfaceVisible(false);
        rig.Vm.SetLyricsSurfaceVisible(true);
        await SettleAsync(rig.Vm);

        Assert.Equal(1, rig.LrcLib.Calls);
        Assert.True(rig.Vm.ShowSearchButton);
    }

    [AvaloniaFact]
    public async Task SettingOff_NoSearch()
    {
        var rig = Mount(new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Lrc)) }, autoDownload: false);
        rig.Vm.SetLyricsSurfaceVisible(true);

        rig.Vm.EnsureLyricsForCurrentTrack();
        await SettleAsync(rig.Vm);

        Assert.Equal(0, rig.LrcLib.Calls);
        Assert.True(rig.Vm.ShowSearchButton);
    }

    [AvaloniaFact]
    public async Task NoLyricsViewOpen_WaitsUntilOneOpens()
    {
        var rig = Mount(new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Lrc)) });

        rig.Vm.EnsureLyricsForCurrentTrack();
        await SettleAsync(rig.Vm);
        Assert.Equal(0, rig.LrcLib.Calls);

        rig.Vm.SetLyricsSurfaceVisible(true);
        await PumpUntilAsync(() => rig.Vm.LyricsSourceName == "LRCLIB");

        Assert.Equal(1, rig.LrcLib.Calls);
    }

    [AvaloniaFact]
    public async Task SongWithLyricsInItsTags_IsNeverSearched()
    {
        var rig = Mount(new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Lrc)) },
            ext: "flac", tag: f => ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true)).SetField("UNSYNCEDLYRICS", "my own words"));
        rig.Vm.SetLyricsSurfaceVisible(true);

        rig.Vm.EnsureLyricsForCurrentTrack();
        await SettleAsync(rig.Vm);

        Assert.Equal(0, rig.LrcLib.Calls);
        Assert.Contains(rig.Vm.UnsyncedLines, l => l.Text == "my own words");
    }

    [AvaloniaFact]
    public async Task RemovedLyrics_AreNotDownloadedStraightBack()
    {
        var rig = Mount(new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Lrc)) }, autoDownload: false);
        rig.Vm.SetLyricsSurfaceVisible(true);
        rig.Vm.EnsureLyricsForCurrentTrack();
        await SettleAsync(rig.Vm);
        rig.Vm.SearchLyricsCommand.Execute(null);
        await PumpUntilAsync(() => rig.Vm.LyricsSourceName == "LRCLIB");
        await DrainAsync();

        await rig.Vm.RemoveLyricsCommand.ExecuteAsync(null);
        rig.Player.LyricsAutoDownload = true;
        rig.Vm.SetLyricsSurfaceVisible(true);
        await SettleAsync(rig.Vm);

        Assert.Equal(1, rig.LrcLib.Calls);
    }

    // ── Preferred lyrics type ──

    [Fact]
    public void ApplyPreferredType_ReducesWhatIsKept()
    {
        var word = Result(synced: Elrc);

        Assert.Same(word, LyricsViewModel.ApplyPreferredType(word, LyricsPreferredType.WordSynced));

        var line = LyricsViewModel.ApplyPreferredType(word, LyricsPreferredType.LineSynced);
        Assert.Equal("[00:01.00]word timed", line.SyncedLyrics);
        Assert.Null(line.Lyricsfile);
        Assert.Equal("word timed", line.PlainLyrics);

        var plain = LyricsViewModel.ApplyPreferredType(word, LyricsPreferredType.Plain);
        Assert.Null(plain.SyncedLyrics);
        Assert.Equal("word timed", plain.PlainLyrics);
        Assert.True(plain.HasLyrics);

        // Nothing timed to begin with: line-synced keeps the plain text, timings are not invented.
        var onlyPlain = LyricsViewModel.ApplyPreferredType(Result(plain: "just words"), LyricsPreferredType.LineSynced);
        Assert.Null(onlyPlain.SyncedLyrics);
        Assert.Equal("just words", onlyPlain.PlainLyrics);
    }

    [AvaloniaFact]
    public async Task PreferredPlain_KeepsPlainText_AndWritesNoLrc()
    {
        var rig = Mount(new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Elrc)) }, preferred: "Plain");
        rig.Vm.SetLyricsSurfaceVisible(true);

        rig.Vm.EnsureLyricsForCurrentTrack();
        await PumpUntilAsync(() => rig.Vm.LyricsSourceName == "LRCLIB");
        await DrainAsync();

        Assert.False(rig.Vm.HasSyncedLyricsAvailable);
        Assert.False(File.Exists(Side(rig.Track, ".lrc")));
        Assert.Equal("word timed", rig.Track.Lyrics);
        Assert.Equal(string.Empty, rig.Track.SyncedLyrics);
    }

    [AvaloniaFact]
    public async Task PreferredLineSynced_SavesLineTimingsOnly()
    {
        var rig = Mount(new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Elrc)) }, preferred: "LineSynced");
        rig.Vm.SetLyricsSurfaceVisible(true);

        rig.Vm.EnsureLyricsForCurrentTrack();
        await PumpUntilAsync(() => rig.Vm.LyricsSourceName == "LRCLIB");
        await DrainAsync();

        Assert.True(rig.Vm.HasSyncedLyricsAvailable);
        Assert.All(rig.Vm.LyricLines, l => Assert.Null(l.Words));
        Assert.False(File.Exists(Side(rig.Track, ".elrc")));
        Assert.Equal("[00:01.00]word timed", File.ReadAllText(Side(rig.Track, ".lrc")).Trim());
    }

    // ── Online lyrics saved inside the audio file ──

    [AvaloniaFact]
    public async Task InsideTheFile_FoundLyricsGoIntoTheTags_WithWordTimings_AndNoLyricsFile()
    {
        var rig = Mount(new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Elrc)) }, ext: "flac", target: "Embedded");
        rig.Vm.SetLyricsSurfaceVisible(true);

        rig.Vm.EnsureLyricsForCurrentTrack();
        await PumpUntilAsync(() => rig.Vm.LyricsSourceName == "LRCLIB");
        await DrainAsync();

        Assert.False(File.Exists(Side(rig.Track, ".lrc")));
        Assert.False(File.Exists(Side(rig.Track, ".elrc")));
        Assert.Equal(Elrc, EmbeddedLyrics.ReadFile(rig.Track.FilePath).Text.Replace("\r\n", "\n"));
        Assert.Equal(Elrc, rig.Track.SyncedLyrics);
    }

    [AvaloniaFact]
    public async Task InsideTheFile_RemoveLyrics_TakesThemOutOfTheTagsAgain()
    {
        var rig = Mount(new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Lrc)) }, ext: "mp3", target: "Embedded");
        rig.Vm.SetLyricsSurfaceVisible(true);
        rig.Vm.EnsureLyricsForCurrentTrack();
        await PumpUntilAsync(() => rig.Vm.LyricsSourceName == "LRCLIB");
        await DrainAsync();
        Assert.False(EmbeddedLyrics.ReadFile(rig.Track.FilePath).IsEmpty);

        await rig.Vm.RemoveLyricsCommand.ExecuteAsync(null);
        await DrainAsync();

        Assert.True(EmbeddedLyrics.ReadFile(rig.Track.FilePath).IsEmpty);
    }

    [AvaloniaFact]
    public async Task InsideTheFile_ExplicitPick_NeverReplacesTheSongsOwnTagLyrics()
    {
        // The row was scanned before the user's tagger added lyrics, so the page searches; the
        // file meanwhile holds the user's own text — the write re-reads and leaves it.
        var rig = Mount(new StubLrcLib(), ext: "opus", target: "Embedded");
        rig.Vm.SetLyricsSurfaceVisible(true);
        rig.Vm.EnsureLyricsForCurrentTrack();
        await SettleAsync(rig.Vm);
        using (var f = TagLib.File.Create(rig.Track.FilePath)) { f.Tag.Lyrics = "the user's own"; f.Save(); }

        rig.Vm.ApplySearchedLyrics(rig.Track, Result(synced: Lrc), "LRCLIB");
        await DrainAsync();

        Assert.Equal("the user's own", EmbeddedLyrics.ReadFile(rig.Track.FilePath).Text);
    }

    [AvaloniaFact]
    public async Task InsideTheFile_SaveLyricsToFile_NamesTheTagLyricsItReplaces_ThenReplacesThem()
    {
        var rig = Mount(new StubLrcLib(), ext: "flac", target: "Embedded",
            tag: f => f.Tag.Lyrics = "the user's own");
        var prompts = new List<string>();
        rig.Vm.ConfirmReplaceLyrics = m => { prompts.Add(m); return Task.FromResult(true); };
        rig.Vm.SetLyricsSurfaceVisible(true);
        rig.Vm.EnsureLyricsForCurrentTrack();
        await SettleAsync(rig.Vm);

        rig.Vm.ApplySearchedLyrics(rig.Track, Result(synced: Elrc), "LRCLIB");
        await DrainAsync();
        Assert.Equal("the user's own", EmbeddedLyrics.ReadFile(rig.Track.FilePath).Text); // the pick alone never replaces them

        await rig.Vm.SaveLyricsToFileCommand.ExecuteAsync(null);
        await DrainAsync();

        var prompt = Assert.Single(prompts);
        Assert.Contains(Noctis.Localization.Loc.T("Lyrics.SaveReplaceTags"), prompt);
        // With no deferred writer (tests) the writer puts the tags on a pool thread.
        await PumpUntilAsync(() => EmbeddedLyrics.ReadFile(rig.Track.FilePath).Text.Contains("<00:01.50>"));
        Assert.Equal(Elrc, EmbeddedLyrics.ReadFile(rig.Track.FilePath).Text.Replace("\r\n", "\n"));
        Assert.False(File.Exists(Side(rig.Track, ".lrc")));
        Assert.False(File.Exists(Side(rig.Track, ".elrc")));
    }
}
