using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using System.Runtime.CompilerServices;
using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class OnlinePlaybackTests
{
    private sealed class Source : IOnlineMusicSearch
    {
        internal IReadOnlyList<OnlineArtist> ArtistRows = [];
        internal readonly Dictionary<string, TaskCompletionSource<Track>> Pending = [];
        internal readonly Dictionary<string, CancellationToken> Tokens = [];
        internal int Asked;
        public Task<Track> PreparePlaybackAsync(OnlineSong song, CancellationToken ct)
        { Asked++; Tokens[song.Id] = ct; return (Pending[song.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task; }
        public void WarmUp() { }
        public void Retry() { }
        public string Route => "synthetic";
        public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new(query, [new("online", query, "Synthetic", Score: 97)], ArtistRows, [], true); }
        public string? CachedArtwork(string url) => null;
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>(null);
        public bool CanDownload => true;
        public void RequestSignIn() { }
        internal OnlineDownloadStatus Status = OnlineDownloadStatus.None;
        internal EventHandler? Changed;
        public OnlineDownloadStatus StatusOf(OnlineSong song) => Status;
        public event EventHandler? DownloadsChanged { add => Changed += value; remove => Changed -= value; }
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct) => throw new NotSupportedException();
        public IDownloadQueue Downloads { get; } = new EmptyQueue();
        internal OnlineArtist? Opened;
        public void OpenArtist(OnlineArtist artist) => Opened = artist;
        public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class EmptyQueue : IDownloadQueue
    {
        public IReadOnlyList<DownloadEntry> Snapshot() => [];
        public bool IsPaused => false;
        public event EventHandler? Changed { add { } remove { } }
        public void SetPaused(bool paused) { }
        public void RetryFailed() { }
        public void StartHeld() { }
        public void Clear() { }
        public void DownloadNext(DownloadEntry entry) { }
        public void Remove(DownloadEntry entry) { }
    }
    private static Track Track(string title) => new() { Title = title, Artist = "Synthetic", Duration = TimeSpan.FromMinutes(3), FilePath = title + ".flac", IsExternal = true };

    [AvaloniaFact]
    public async Task OnlineNamesakes_DoNotReceiveALibraryBonus_OrHideEachOthersIdentity()
    {
        var previous = OnlineMusicSearch.Current;
        var source = new Source { ArtistRows = [new("kanye", "Kanye West", Score: 96),
            new("namesake-one", "Runaway", Score: 91), new("namesake-two", "Runaway", Score: 90)] };
        using var data = new TestPersistenceService();
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, data, new FakeAnimatedCoverService());
        OnlineMusicSearch.Current = source;
        using var search = new SearchViewModel(library, player, _ => { }, _ => { });
        try
        {
            search.SetQuery("runaway");
            var until = DateTime.UtcNow.AddSeconds(3);
            while (search.IsOnlineLoading && DateTime.UtcNow < until) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.False(search.IsOnlineLoading);
            Assert.Equal("kanye", search.Artists.First().Online?.Id);
            Assert.Equal(new[] { "kanye", "namesake-one", "namesake-two" }, search.Artists.Select(a => a.Online?.Id));
        }
        finally { search.Dispose(); OnlineMusicSearch.Current = previous; }
    }

    [AvaloniaFact]
    public void LibraryArtistAlsoFoundOnline_OpensWithItsExactOnlineIdentity()
    {
        // Owner, 2026-10-18: with one J. Cole song owned, his page showed only that song (opened by name, it had to
        // guess between two Deezer "J. Cole" accounts).
        var previous = OnlineMusicSearch.Current; var source = new Source();
        using var data = new TestPersistenceService(); var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, data, new FakeAnimatedCoverService());
        OnlineMusicSearch.Current = source; string? byName = null;
        using var search = new SearchViewModel(library, player, name => byName = name, _ => { });
        try
        {
            Dispatcher.UIThread.RunJobs();
            var merged = new SearchArtistItem(new Artist { Name = "J. Cole" });
            merged.AttachOnline(new OnlineArtist("deezer:339209", "J. Cole"));
            search.OpenArtistCommand.Execute(merged);
            Assert.Equal("deezer:339209", source.Opened?.Id); Assert.Null(byName);
            search.OpenArtistCommand.Execute(new SearchArtistItem(new Artist { Name = "Only Local" }));
            Assert.Equal("Only Local", byName);
        }
        finally { search.Dispose(); OnlineMusicSearch.Current = previous; }
    }

    [AvaloniaFact]
    public async Task SearchTypedBeforeSourceStartupGetsOnlineResults_AndDisablingSourceRemovesThem()
    {
        var previous = OnlineMusicSearch.Current;
        using var data = new TestPersistenceService(); var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, data, new FakeAnimatedCoverService());
        OnlineMusicSearch.Current = null;
        using var search = new SearchViewModel(library, player, _ => { }, _ => { });
        try
        {
            search.SetQuery("Night Drive"); Assert.Empty(search.Songs);
            OnlineMusicSearch.Current = new Source();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (search.Songs.Count == 0 && DateTime.UtcNow < until) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal("Night Drive", Assert.Single(search.Songs).Title);
            OnlineMusicSearch.Current = null; Dispatcher.UIThread.RunJobs();
            Assert.Empty(search.Songs); Assert.False(search.HasOnline);
        }
        finally { search.Dispose(); OnlineMusicSearch.Current = previous; }
    }

    [AvaloniaFact]
    public async Task LatestOnlinePlayWins_EvenWhenTheOlderSourceIgnoresCancellation_AndQueueIsPreserved()
    {
        using var data = new TestPersistenceService(); var audio = new FakeAudioPlayer(); var source = new Source();
        var player = new PlayerViewModel(audio, new FakeLibraryService(), data, new FakeAnimatedCoverService());
        var next = Track("next"); player.UpNext.Add(next);
        var first = player.PlayOnlineAsync(source, new("first", "First", "Synthetic"));
        var second = player.PlayOnlineAsync(source, new("second", "Second", "Synthetic"));
        Assert.True(source.Tokens["first"].IsCancellationRequested); Assert.True(player.IsWaitingForAudio);
        source.Pending["second"].SetResult(Track("Second")); await second;
        source.Pending["first"].SetResult(Track("First")); await first;
        Assert.Equal("Second", player.CurrentTrack?.Title); Assert.Single(audio.PlayedPaths);
        Assert.Same(next, Assert.Single(player.UpNext)); Assert.False(player.IsWaitingForAudio);
        player.StopAndClear();
    }

    [AvaloniaFact]
    public async Task PreviousToAStreamWhoseDownloadWasCutOff_FetchesItAgain_InsteadOfSkippingForward()
    {
        // Owner #215: Previous "goes forward": the song before had no file (its download stopped when it was skipped), so
        // it failed and the player moved on to the next song.
        using var data = new TestPersistenceService(); var audio = new FakeAudioPlayer(); var source = new Source();
        var player = new PlayerViewModel(audio, new FakeLibraryService(), data, new FakeAnimatedCoverService());
        var first = player.PlayOnlineAsync(source, new("first", "First", "Synthetic"));
        source.Pending["first"].SetResult(Track("First")); await first;
        var second = player.PlayOnlineAsync(source, new("second", "Second", "Synthetic"));
        source.Pending["second"].SetResult(Track("Second")); await second;
        Assert.Equal("Second", player.CurrentTrack?.Title);

        player.PreviousCommand.Execute(null);
        Assert.Equal(3, source.Asked);                      // "First" is asked for again
        source.Pending["first"].SetResult(Track("First"));
        await Task.Delay(50);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("First", player.CurrentTrack?.Title);
        player.StopAndClear();
    }

    [AvaloniaFact]
    public async Task RepeatClicksOnTheSongBeingPrepared_ShareOneRequest()
    {
        // Owner #29: every click on a song sent the bots the same request again.
        using var data = new TestPersistenceService(); var audio = new FakeAudioPlayer(); var source = new Source();
        var player = new PlayerViewModel(audio, new FakeLibraryService(), data, new FakeAnimatedCoverService());
        var song = new OnlineSong("online", "Online", "Synthetic");
        var first = player.PlayOnlineAsync(source, song);
        var again = player.PlayOnlineAsync(source, song); var third = player.PlayOnlineAsync(source, song);
        Assert.Same(first, again); Assert.Same(first, third); Assert.Equal(1, source.Asked);
        Assert.False(source.Tokens["online"].IsCancellationRequested);
        source.Pending["online"].SetResult(Track("Online")); await first;
        Assert.Single(audio.PlayedPaths); Assert.Equal("Online", player.CurrentTrack?.Title);
        // Another song still takes over at once.
        var other = player.PlayOnlineAsync(source, new("other", "Other", "Synthetic")); Assert.Equal(2, source.Asked);
        source.Pending["other"].SetResult(Track("Other")); await other; player.StopAndClear();
    }

    [AvaloniaFact]
    public async Task PreparingASong_FollowsItsDownloadStep_ForThePlayButton()
    {
        // Owner #54: the Play button shows what the download is doing, not just a spinning ring.
        using var data = new TestPersistenceService(); var source = new Source();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), data, new FakeAnimatedCoverService());
        var song = new OnlineSong("online", "Online", "Synthetic");
        source.Status = new(OnlineDownloadState.Downloading, 0, "", OnlineDownloadStep.Asking);
        var play = player.PlayOnlineAsync(source, song);
        Assert.Equal(OnlineDownloadStep.Asking, player.OnlinePreparingStep);

        source.Status = new(OnlineDownloadState.Downloading, 0.4, "", OnlineDownloadStep.Receiving);
        source.Changed?.Invoke(source, EventArgs.Empty);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(OnlineDownloadStep.Receiving, player.OnlinePreparingStep);
        Assert.Equal(0.4, player.OnlinePreparingProgress, 3);

        source.Pending["online"].SetResult(Track("Online")); await play;
        Assert.False(player.IsPreparingOnlinePlayback);
        // Preparation's handler is gone; one playback-window listener remains so
        // reconnect/download completion can update the active window without a new click.
        Assert.Single(source.Changed!.GetInvocationList());
        player.StopAndClear();
    }

    [AvaloniaFact]
    public void StepBadge_ShowsOneIconPerStep_AndNamesIt()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var badge = new Noctis.Controls.DownloadStepBadge();
        var window = new Avalonia.Controls.Window { Content = badge, Width = 100, Height = 100 };
        try
        {
            window.Show();
            foreach (var (step, spinning, arrow) in new[]
            {
                (OnlineDownloadStep.Asking, true, false), (OnlineDownloadStep.Switching, true, false),
                (OnlineDownloadStep.Receiving, false, true), (OnlineDownloadStep.Saving, false, false),
            })
            {
                badge.Step = step; badge.Progress = 0.5;
                Assert.Equal(spinning, badge.Ring.IsIndeterminate);
                Assert.Equal(arrow, badge.Arrow.IsVisible);
                Assert.Equal(!arrow, badge.Glyph.IsVisible);
                if (!arrow) Assert.NotNull(badge.Glyph.Data);
                Assert.Equal(Noctis.Controls.DownloadStepBadge.Describe(step), Avalonia.Automation.AutomationProperties.GetName(badge));
            }
            Assert.Equal(1, badge.Ring.Value); // saving: the ring is full
            Assert.Contains("spin", new[] { OnlineDownloadStep.Switching }.Select(s => { badge.Step = s; return string.Join(",", badge.Glyph.Classes); }).Single());
            // Every song row carries a hidden badge: hidden, its icon must not animate.
            badge.Step = OnlineDownloadStep.Asking;
            Assert.Contains("pulse", badge.Glyph.Classes);
            badge.IsVisible = false;
            Assert.DoesNotContain("pulse", badge.Glyph.Classes);
            Assert.DoesNotContain("spin", badge.Glyph.Classes);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("pause")]
    [InlineData("stop")]
    [InlineData("local")]
    [InlineData("shutdown")]
    public async Task PlaybackIntentCancelsPendingAutoplay_WithoutRemovingTheDownload(string intent)
    {
        using var data = new TestPersistenceService(); var audio = new FakeAudioPlayer(); var source = new Source();
        var player = new PlayerViewModel(audio, new FakeLibraryService(), data, new FakeAnimatedCoverService());
        player.ReplaceQueueAndPlay([Track("Current")], 0);
        var pending = player.PlayOnlineAsync(source, new("online", "Online", "Synthetic"));
        switch (intent)
        {
            case "pause": player.PlayPauseCommand.Execute(null); break;
            case "stop": player.StopAndClear(); break;
            case "local": player.ReplaceQueueAndPlay([Track("Local")], 0); break;
            case "shutdown": player.PauseForShutdown(); break;
        }
        Assert.True(source.Tokens["online"].IsCancellationRequested);
        var plays = audio.PlayedPaths.Count;
        source.Pending["online"].SetResult(Track("Online")); await pending;
        Assert.Equal(plays, audio.PlayedPaths.Count); Assert.NotEqual("Online", player.CurrentTrack?.Title);
        Assert.False(player.IsWaitingForAudio); player.StopAndClear();
    }

    [AvaloniaFact]
    public async Task ReplayingTheSameOnlineRowPauses_AndImportAdoptsTheLibraryObjectWithoutRestartOrDoubleCount()
    {
        using var data = new TestPersistenceService(); var audio = new FakeAudioPlayer(); var source = new Source(); var library = new FakeLibraryService();
        var player = new PlayerViewModel(audio, library, data, new FakeAnimatedCoverService());
        var song = new OnlineSong("online", "Online", "Synthetic");
        var preparing = player.PlayOnlineAsync(source, song); var streamed = Track("Online");
        source.Pending["online"].SetResult(streamed); await preparing;
        player.Position = TimeSpan.FromSeconds(10);
        await player.PlayOnlineAsync(source, song);
        Assert.Equal(PlaybackState.Paused, player.State); Assert.Single(audio.PlayedPaths); Assert.Single(source.Pending);
        var row = new SearchSongItem(song); row.ApplyPlayback(player); Assert.True(row.IsNowPlaying); Assert.False(row.IsPlaying);
        var imported = Track("Online"); imported.IsExternal = false; library.TrackList.Add(imported);
        library.RaiseLibraryUpdated(); Dispatcher.UIThread.RunJobs();
        Assert.Same(imported, player.CurrentTrack); Assert.Equal(TimeSpan.FromSeconds(10), player.Position);
        Assert.Single(audio.PlayedPaths); Assert.Equal(1, imported.PlayCount);
        library.RaiseLibraryUpdated(); Dispatcher.UIThread.RunJobs(); Assert.Equal(1, imported.PlayCount);
        player.StopAndClear();
    }
}
