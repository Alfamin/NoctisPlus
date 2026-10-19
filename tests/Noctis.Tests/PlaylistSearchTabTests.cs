using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeMusicFinder;
using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #69: the Playlists tab of Search, and a found playlist's unsaved page.</summary>
public class PlaylistSearchTabTests
{
    private sealed class Source : IOnlineMusicSearch
    {
        internal IReadOnlyList<OnlinePlaylist> Found = [];
        public void WarmUp() { }
        public void Retry() { }
        public string Route => "synthetic";
        public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, [EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return new(query, [], [], [], true) { Playlists = Found }; }
        public string? CachedArtwork(string url) => null;
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>(null);
        public bool CanDownload => true;
        public void RequestSignIn() { }
        public OnlineDownloadStatus StatusOf(OnlineSong song) => OnlineDownloadStatus.None;
        public event EventHandler? DownloadsChanged { add { } remove { } }
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct) => Task.FromResult("Added to downloads.");
        public IDownloadQueue Downloads { get; } = new NoQueue();
        public void OpenArtist(OnlineArtist artist) { }
        public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoQueue : IDownloadQueue
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

    [AvaloniaFact]
    public async Task PlaylistsTab_ShowsFoundPlaylists_BestFirst_WithSaves_AndOpensThem()
    {
        var previous = OnlineMusicSearch.Current;
        var saved = Guid.NewGuid();
        var source = new Source { Found =
        [
            new("u2", "Hype Drake Only", "Nikita", "https://open.spotify.com/playlist/b", "", 87, 6_353, Score: 2),
            new("u1", "Drake Hype Songs", "Saarujan", "https://open.spotify.com/playlist/a", "", 106, 18_335, saved, Score: 3),
            new("u3", "hype drake", "Ollie", "https://open.spotify.com/playlist/c", "", 1, 0, Score: 1),
        ] };
        using var data = new TestPersistenceService();
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, data, new FakeAnimatedCoverService());
        OnlineMusicSearch.Current = source;
        using var search = new SearchViewModel(library, player, _ => { }, _ => { });
        try
        {
            search.SetQuery("hype drake");
            var until = DateTime.UtcNow.AddSeconds(3);
            while (search.IsOnlineLoading && DateTime.UtcNow < until) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }

            Assert.Equal(new[] { "Drake Hype Songs", "Hype Drake Only", "hype drake" }, search.Playlists.Select(p => p.Title));
            Assert.True(search.ShowPlaylists); // a row of them on All
            Assert.False(search.ShowNoResults); // playlists alone are results
            var top = search.Playlists[0];
            Assert.Equal("Saarujan · 106 songs", top.Subtitle);
            Assert.True(top.IsSaved);
            Assert.True(top.HasSaves);
            Assert.False(search.Playlists[2].HasSaves); // nobody saved it: no "0" on the cover
            Assert.Equal("hype drake", search.Playlists[2].Title);
            Assert.Equal("Ollie · 1 song", search.Playlists[2].Subtitle);

            search.SelectFilterCommand.Execute("Playlists");
            Assert.True(search.IsPlaylistsFilter);
            Assert.True(search.ShowPlaylists);
            Assert.False(search.ShowArtists);
            Assert.False(search.ShowNoPlaylists);

            OnlinePlaylist? opened = null;
            search.PlaylistOpened += (_, p) => opened = p;
            search.OpenPlaylistCommand.Execute(search.Playlists[1]);
            Assert.Equal("https://open.spotify.com/playlist/b", opened?.Url);

            source.Found = [];
            search.SetQuery("nothing like this");
            until = DateTime.UtcNow.AddSeconds(3);
            while (search.IsOnlineLoading && DateTime.UtcNow < until) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.Empty(search.Playlists);
            Assert.True(search.ShowNoResults);
        }
        finally { search.Dispose(); OnlineMusicSearch.Current = previous; }
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1_234, "1.2K")]
    [InlineData(18_335, "18K")]
    [InlineData(1_625_312, "1.6M")]
    [InlineData(12_400_000, "12M")]
    public void SaveCounts_AreShortOnTheCover(long saves, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { Assert.Equal(expected, SearchPlaylistItem.Compact(saves)); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [AvaloniaFact]
    public async Task FoundPlaylist_OpensUnsaved_ReadOnly_WithEverySongInOrder()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var owned = new Track
        {
            Id = Guid.NewGuid(), Title = "Owned Song", Artist = "Synthetic Singer", Album = "Night", Duration = TimeSpan.FromMinutes(3),
            FilePath = TestPaths.Primary("found-playlist", $"{Guid.NewGuid():N}.mp3"),
        };
        var lib = new FakeLibraryService(); lib.TrackList.Add(owned);
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var found = new OnlinePlaylist("u1", "Drake Hype Songs", "Saarujan", "https://open.spotify.com/playlist/a", "", 2, 18_335);
        var vm = PlaylistViewModel.ForPreview(found, player, lib, persistence, sidebar);
        var view = new PlaylistView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 900, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsPreview);
            Assert.False(vm.IsEditable);
            Assert.False(vm.CanReorder);
            Assert.False(vm.CanAddSongs);
            Assert.False(vm.ShowEmptyManual); // not "This playlist is empty, add songs" while it is read
            Assert.Equal("Drake Hype Songs", vm.Name);
            Assert.Contains("Saarujan · 18", vm.PreviewByline);
            Assert.EndsWith(" saves", vm.PreviewByline);
            Assert.True(vm.SavePreviewCommand.CanExecute(null));

            CatalogSong missing = new("spotify:m", "Missing One", [new("", "Synthetic Singer")], 201, true);
            vm.ApplyPreview([new(1, missing, null, ""), new(2, null, owned.Id, "")]);
            var until = Environment.TickCount64 + 5000;
            while (vm.Tracks.Count == 0 && Environment.TickCount64 < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
            for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

            Assert.Equal(2, vm.Rows.Count);
            Assert.Equal(1, Assert.IsType<ArtistCatalogSong>(vm.Rows.ElementAt(0)).Rank);
            Assert.Same(owned, vm.Rows.ElementAt(1));
            Assert.Equal("2 tracks", vm.TrackCountText);
            Assert.True(vm.ShowPlayActions); // the owned song plays
            Assert.True(vm.HasMissingDownloads);
            Assert.Equal("Download missing · 1", vm.MissingDownloadsLabel);
            var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Save", texts);
            Assert.DoesNotContain(texts, t => t?.StartsWith("Created ") == true); // its maker and saves stand in for dates

            // Nothing on the page edits a playlist that is not saved.
            vm.StartRenameCommand.Execute(null);
            Assert.False(vm.IsRenamingName);
            await vm.RemoveTrackCommand.ExecuteAsync(owned);
            Assert.Same(owned, vm.Rows.ElementAt(1));
            await vm.MoveTrack(0, 1);
            Assert.Empty(sidebar.Playlists); // and nothing was saved
        }
        finally { window.Close(); vm.Dispose(); }
    }
}
