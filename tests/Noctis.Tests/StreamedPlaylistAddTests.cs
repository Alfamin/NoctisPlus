using System.Runtime.CompilerServices;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #88: Songs are all the songs you have. A played-only song added to a playlist joins Songs, and the
/// playlist holds its library copy.</summary>
public class StreamedPlaylistAddTests
{
    private const string Cache = @"C:\Cache\stream-cache\", Library = @"C:\Music\Noctis Free Music\";

    private sealed class Source : IOnlineMusicSearch
    {
        internal readonly List<string> Added = [];
        internal readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase);
        public bool CanBlockArtists => true;
        public bool IsArtistBlocked(string artist) => Blocked.Contains(artist);
        public bool SetArtistBlocked(string artist, bool blocked) => blocked ? Blocked.Add(artist) : Blocked.Remove(artist);
        internal Exception? Fail;
        internal Func<string, Task<StreamedAdd>>? Add;
        public bool IsCachedFile(string? path) => path?.StartsWith(Cache, StringComparison.OrdinalIgnoreCase) == true;
        public bool IsStreamedFile(string? path) => IsCachedFile(path);
        public Task<StreamedAdd> AddStreamedAsync(string path, CancellationToken ct)
        {
            if (Add is not null) return Add(path);
            if (Fail is not null) return Task.FromException<StreamedAdd>(Fail);
            Added.Add(path);
            return Task.FromResult(new StreamedAdd("Added to your songs.", Library + Path.GetFileName(path)));
        }
        public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); yield break; }
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>(null);
        public void WarmUp() { }
        public void Retry() { }
        public string Route => "synthetic";
        public string? CachedArtwork(string url) => null;
        public bool CanDownload => true;
        public void RequestSignIn() { }
        public OnlineDownloadStatus StatusOf(OnlineSong song) => OnlineDownloadStatus.None;
        public event EventHandler? DownloadsChanged { add { } remove { } }
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct) => throw new NotSupportedException();
        public IDownloadQueue Downloads => throw new NotSupportedException();
        public void OpenArtist(OnlineArtist artist) { }
        public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => Task.CompletedTask;
    }

    private static Track Streamed(string name) => new()
    { Id = Guid.NewGuid(), FilePath = Cache + name, Title = "Levitating", Artist = "Dua Lipa", IsExternal = true };

    [Fact]
    public async Task APlayedSong_AddedToAPlaylist_JoinsSongs_AndThePlaylistHoldsTheLibraryCopy()
    {
        var source = new Source();
        var played = Streamed("Dua Lipa - Levitating.flac");
        var owned = new Track { Id = Guid.NewGuid(), FilePath = Library + "Owned.flac", Title = "Owned" };
        var dropped = new Track { Id = Guid.NewGuid(), FilePath = @"D:\Elsewhere\Dropped.flac", Title = "Dropped", IsExternal = true };

        var result = await SidebarViewModel.InLibraryAsync([owned, played, dropped], source);

        Assert.Equal([Cache + "Dua Lipa - Levitating.flac"], source.Added);
        Assert.Same(owned, result[0]);
        Assert.Same(dropped, result[2]); // a dropped file outside the cache is not the stream cache's business
        var copy = result[1];
        Assert.Equal(Library + "Dua Lipa - Levitating.flac", copy.FilePath);
        Assert.Equal(LibraryService.TrackIdForPath(copy.FilePath), copy.Id); // the id the library gives the copy when imported
        Assert.False(copy.IsExternal);
        Assert.Equal("Levitating", copy.Title);
    }

    /// <summary>Owner #178: Add on one played song, then the next streamed song within the check's 2.5 s: the check stayed
    /// on the new song, which was never added.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheAddedCheck_StaysWithItsSong_WhenTheNextStreamedSongStarts()
    {
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        var before = OnlineMusicSearch.Current;
        OnlineMusicSearch.Current = new Source();
        try
        {
            var first = Streamed("A.flac"); var second = Streamed("B.flac");
            player.ReplaceQueueAndPlay([first, second], 0); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(player.IsStreamedTrack);
            var add = player.AddToSongsCommand.ExecuteAsync(null);
            await Task.Delay(50); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(player.JustAddedToSongs);
            player.NextCommand.Execute(null); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Same(second, player.CurrentTrack);
            Assert.True(player.IsStreamedTrack); // its own Add is offered
            Assert.False(player.JustAddedToSongs);
            await add;
            Assert.False(player.JustAddedToSongs);
        }
        finally { OnlineMusicSearch.Current = before; }
    }

    /// <summary>Owner #177: the lyrics page offers Not for me only while a For you song plays, and asks the shelf.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void NotForMe_IsOfferedOnlyForAForYouSong_AndGoesToTheShelf()
    {
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        var asked = new List<string>();
        player.IsForYouSong = id => id == "deezer:1";
        player.DismissForYouSong = asked.Add;
        player.IsStreamedTrack = true;
        player.OnlinePlayingSongId = "deezer:2";
        Assert.False(player.CanNotForMe);
        player.OnlinePlayingSongId = "deezer:1";
        Assert.True(player.CanNotForMe);
        player.NotForMeCommand.Execute(null);
        Assert.Equal(["deezer:1"], asked);
        player.OnlinePlayingSongId = null;
        Assert.False(player.CanNotForMe);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task AddingTheForYouSong_HidesNotForMeImmediately_AndItReturnsForTheNextUnsavedSong()
    {
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        var before = OnlineMusicSearch.Current;
        var saved = new TaskCompletionSource<StreamedAdd>();
        OnlineMusicSearch.Current = new Source { Add = _ => saved.Task };
        try
        {
            var first = Streamed("A.flac"); var second = Streamed("B.flac");
            player.IsForYouSong = _ => true;
            player.ReplaceQueueAndPlay([first, second], 0);
            player.OnlinePlayingSongId = "deezer:1";
            Assert.True(player.CanNotForMe);
            var add = player.AddToSongsCommand.ExecuteAsync(null);
            Assert.True(player.CanNotForMe); // A pending save is not yet a kept song.
            saved.SetResult(new("Saved to library", Library + "A.flac"));
            await Task.Delay(25); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(player.JustAddedToSongs);
            Assert.False(player.CanNotForMe); // No need to wait for the Added check's timer or another page change.
            player.RefreshNotForMe(); Assert.False(player.CanNotForMe);
            player.NextCommand.Execute(null); player.OnlinePlayingSongId = "deezer:2";
            Assert.True(player.CanNotForMe);
            await add;
            Assert.True(player.CanNotForMe);
        }
        finally { OnlineMusicSearch.Current = before; }
    }

    [Avalonia.Headless.XUnit.AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOrDeclinedAdd_KeepsNotForMeAndDoesNotShowAdded(bool throws)
    {
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        var before = OnlineMusicSearch.Current;
        OnlineMusicSearch.Current = new Source { Add = _ => throws ? Task.FromException<StreamedAdd>(new IOException("Disk full")) : Task.FromResult(new StreamedAdd("Not saved", null)) };
        try
        {
            player.IsForYouSong = _ => true;
            player.ReplaceQueueAndPlay([Streamed("A.flac")], 0); player.OnlinePlayingSongId = "deezer:1";
            await player.AddToSongsCommand.ExecuteAsync(null);
            Assert.True(player.IsStreamedTrack && player.CanNotForMe);
            Assert.False(player.JustAddedToSongs);
        }
        finally { OnlineMusicSearch.Current = before; }
    }

    [Avalonia.Headless.XUnit.AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LateAddForAnOldSong_CannotChangeTheNextSongsActionsOrTooltip(bool succeeds)
    {
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        var before = OnlineMusicSearch.Current;
        var saved = new TaskCompletionSource<StreamedAdd>();
        OnlineMusicSearch.Current = new Source { Add = _ => saved.Task };
        try
        {
            player.IsForYouSong = _ => true;
            player.ReplaceQueueAndPlay([Streamed("A.flac"), Streamed("B.flac")], 0); player.OnlinePlayingSongId = "deezer:1";
            var add = player.AddToSongsCommand.ExecuteAsync(null);
            player.NextCommand.Execute(null); player.OnlinePlayingSongId = "deezer:2";
            if (succeeds) saved.SetResult(new("Saved to library", Library + "A.flac")); else saved.SetException(new IOException("Disk full"));
            await add;
            Assert.True(player.CanNotForMe && player.IsStreamedTrack);
            Assert.False(player.JustAddedToSongs);
            Assert.Empty(player.AddToSongsMessage);
        }
        finally { OnlineMusicSearch.Current = before; }
    }

    /// <summary>Owner #166: Don't recommend artist in song and artist menus. One artist: the entry flips to Recommend again;
    /// several: one ticked or unticked entry each, never all at once. Hidden without For you.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void DontRecommendArtist_OneArtistFlips_SeveralAreChosenOneByOne_HiddenWithoutForYou()
    {
        var before = OnlineMusicSearch.Current;
        var source = new Source();
        OnlineMusicSearch.Current = source;
        try
        {
            var item = new Avalonia.Controls.MenuItem();
            Noctis.Helpers.RecommendMenu.Bind(item, ["Drake"]);
            Assert.True(item.IsVisible);
            Assert.Equal("Don't recommend artist", item.Header);
            item.Command!.Execute(item.CommandParameter);
            Assert.Contains("Drake", source.Blocked);
            Noctis.Helpers.RecommendMenu.Bind(item, ["Drake"]);
            Assert.Equal("Recommend again", item.Header);

            Noctis.Helpers.RecommendMenu.Bind(item, ["Drake", "Future"]);
            Assert.Null(item.Command);
            var entries = item.Items.OfType<Avalonia.Controls.MenuItem>().ToList();
            Assert.Equal(["Drake", "Future"], entries.Select(e => (string)e.Header!));
            Assert.Equal([true, false], entries.Select(e => e.IsChecked));
            entries[1].Command!.Execute(entries[1].CommandParameter);
            Assert.Equal(2, source.Blocked.Count);

            OnlineMusicSearch.Current = null;
            Noctis.Helpers.RecommendMenu.Bind(item, ["Drake"]);
            Assert.False(item.IsVisible);
        }
        finally { OnlineMusicSearch.Current = before; }
    }

    [Fact]
    public async Task ASongThatCannotBeCopied_KeepsItsOwnEntry_AndNothingThrows()
    {
        var source = new Source { Fail = new IOException("disk full") };
        var played = Streamed("Song.flac");
        var result = await SidebarViewModel.InLibraryAsync([played], source);
        Assert.Same(played, result[0]);
    }
}
