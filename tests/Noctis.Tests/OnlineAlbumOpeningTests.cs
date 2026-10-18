using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FreeMusicFinder;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner #34: an online album not read yet stalled on the For you shelf until its songs arrived.
/// Its page now opens at once with skeleton rows and fills in place.
/// </summary>
public class OnlineAlbumOpeningTests
{
    private sealed class FakeLastFm : ILastFmService
    {
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string a, string b, string? d, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static AlbumDetailViewModel Page(CatalogRelease release)
    {
        var lib = new FakeLibraryService(); var persistence = new TestPersistenceService();
        var album = new Album { Id = Guid.NewGuid(), Name = release.Title, Artist = release.Artist.Name, CatalogReleaseId = release.Id, Tracks = [] };
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    [AvaloniaFact]
    public void OnlineAlbum_OpensAtOnceWithSkeletons_ThenFillsInPlace()
    {
        var artist = new CatalogArtist("deezer:7", "Synthetic Singer");
        var release = new CatalogRelease("deezer:70", "Night Album", artist, "2013-09-24", "album", 0);
        var vm = Page(release);
        try
        {
            vm.ShowCatalogLoading(release);
            Assert.True(vm.IsCatalogLoading);
            Assert.False(vm.ShowPlayActions);
            Assert.Equal("", vm.Album.TrackCountText);       // never "0 tracks"
            Assert.Equal("", vm.Album.HeaderDurationFormatted);
            Assert.NotEmpty(vm.CatalogSkeletonRows);

            List<CatalogSong> songs = [new("deezer:71", "One", [artist], 200, false, Number: 1), new("deezer:72", "Two", [artist], 180, false, Number: 2)];
            vm.ShowCatalog(new(new CatalogAlbum(release, songs), new Dictionary<string, DiscoveryMatch>()));
            Assert.False(vm.IsCatalogLoading);
            Assert.Equal("2 tracks", vm.Album.TrackCountText);
            Assert.Equal(2, vm.CatalogMissing.Count);
            Assert.True(vm.IsOnlineAlbum);
        }
        finally { vm.Dispose(); }
    }

    [AvaloniaFact]
    public void OnlineAlbum_FailedOrClosedWhileLoading_StopsTheSkeletons()
    {
        var artist = new CatalogArtist("deezer:8", "Synthetic Band");
        var release = new CatalogRelease("deezer:80", "Day Album", artist, "2020", "album", 9);
        var failed = Page(release);
        failed.ShowCatalogLoading(release);
        failed.ShowCatalogFailed("The catalog did not answer.");
        Assert.False(failed.IsCatalogLoading);
        Assert.Equal("The catalog did not answer.", failed.CatalogNotice);
        failed.Dispose();

        var closed = Page(release);
        closed.ShowCatalogLoading(release);
        closed.Dispose();
        Assert.False(closed.IsCatalogLoadingFor(release.Id)); // a late answer never fills a closed page
    }
}
