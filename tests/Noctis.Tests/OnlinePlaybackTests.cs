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
        internal readonly Dictionary<string, TaskCompletionSource<Track>> Pending = [];
        internal readonly Dictionary<string, CancellationToken> Tokens = [];
        public Task<Track> PreparePlaybackAsync(OnlineSong song, CancellationToken ct)
        { Tokens[song.Id] = ct; return (Pending[song.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task; }
        public void WarmUp() { }
        public void Retry() { }
        public string Route => "synthetic";
        public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new(query, [new("online", query, "Synthetic", Score: 97)], [], [], true); }
        public string? CachedArtwork(string url) => null;
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>(null);
        public bool CanDownload => true;
        public void RequestSignIn() { }
        public OnlineDownloadStatus StatusOf(OnlineSong song) => OnlineDownloadStatus.None;
        public event EventHandler? DownloadsChanged { add { } remove { } }
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
