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
        internal Exception? Fail;
        public bool IsCachedFile(string? path) => path?.StartsWith(Cache, StringComparison.OrdinalIgnoreCase) == true;
        public bool IsStreamedFile(string? path) => IsCachedFile(path);
        public Task<StreamedAdd> AddStreamedAsync(string path, CancellationToken ct)
        {
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

    [Fact]
    public async Task ASongThatCannotBeCopied_KeepsItsOwnEntry_AndNothingThrows()
    {
        var source = new Source { Fail = new IOException("disk full") };
        var played = Streamed("Song.flac");
        var result = await SidebarViewModel.InLibraryAsync([played], source);
        Assert.Same(played, result[0]);
    }
}
