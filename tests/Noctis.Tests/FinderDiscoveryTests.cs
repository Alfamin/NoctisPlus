using FreeMusicFinder;
using Noctis.Services.Search;
using Xunit;

namespace Noctis.Tests;

/// <summary>Real Plus cache/identity/exclusions/feedback behind the typed phone adapter.
/// Metadata-only fixture: any unexpected catalog access throws, no Telegram or audio fetch.</summary>
public class FinderDiscoveryTests
{
    [Fact]
    public void RestoringQueueMetadataAcceptsCatalogIdentitiesWithoutNetworkOrAudio()
    {
        using var rig = new Rig();
        Assert.True(rig.Online.RestoreQueueSong(new("deezer:991", "Song", "Artist", Seconds: 180)));
        Assert.Equal(OnlineDownloadStatus.None, rig.Online.StatusOf(new("deezer:991", "Song", "Artist")));
        Assert.False(rig.Online.RestoreQueueSong(new("https://example.test/audio", "Song", "Artist")));
        Assert.False(rig.Online.RestoreQueueSong(new("apple:22", "", "Artist")));
        Assert.Empty(rig.Downloads.History);
    }
    private sealed class Library : IPlaylistLibrary
    {
        public List<LocalSong> Songs = [];
        public Task<IReadOnlyList<LocalSong>> SnapshotAsync() => Task.FromResult<IReadOnlyList<LocalSong>>(Songs.ToArray());
        public bool Exists(string path) => Songs.Any(s => s.Path == path);
        public Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null) => Task.CompletedTask;
    }
    private sealed class Rig : IDisposable
    {
        public readonly TestPersistenceService Data = new();
        public readonly Library Library = new();
        public readonly Downloads Downloads;
        public readonly CatalogPages Catalog;
        public readonly FinderOnlineSearch Online;
        public readonly ForYouSource Source;
        public readonly FinderDiscovery Discovery;
        public readonly CatalogArtist A = new("deezer:100", "First Artist"), B = new("deezer:200", "Second Artist");
        public CatalogSong Song(string id, string title, CatalogArtist? artist = null) => new(id, title, [artist ?? A], 180, true);
        public Rig()
        {
            Downloads = new Downloads(_ => { });
            var actions = new DiscoveryActions(Library, Downloads, _ => throw new InvalidOperationException("No audio in metadata tests"), () => Data.DataDirectory, () => false);
            Catalog = new CatalogPages(() => throw new InvalidOperationException("Cache should serve this"), actions, _ => { }, () => true);
            Online = new FinderOnlineSearch(() => null, actions, Catalog, () => false, () => { });
            Source = NewSource(); Discovery = new(Source, Online, Library);
            var page = new DiscoverResult([
                new(new(Song("deezer:1", "One"), null), 2, "test"),
                new(new(Song("deezer:2", "Two"), null), 1, "test"),
                new(new(Song("deezer:3", "Three", B), null), 1, "test")], [], "", Batch: "batch1");
            var now = DateTimeOffset.UtcNow;
            new ForYouCache(Path.Combine(Data.DataDirectory, "for-you-last.json")).Write(new(1, now, now.AddDays(1), page));
        }
        public ForYouSource NewSource() => new(() => throw new InvalidOperationException("No network"), null, _ => [], Library,
            Data.DataDirectory, Path.Combine(Data.DataDirectory, "for-you-hidden.json"));
        public void Dispose() { Source.Dispose(); Online.Detach(); Catalog.Dispose(); Downloads.Dispose(); Data.Dispose(); }
    }

    [Fact]
    public async Task CachedPicksUseTheLiveSafLibraryMatch_AndNeverStartAnAudioDownload()
    {
        using var rig = new Rig();
        var page = Assert.Single(await rig.Discovery.LoadAsync(false, default));
        Assert.Equal(3, page.Songs.Count);
        rig.Library.Songs.Add(new(Guid.NewGuid(), rig.A.Name, "One", "content://tree/owned", TimeSpan.FromSeconds(180)));
        page = Assert.Single(await rig.Discovery.LoadAsync(false, default));
        Assert.DoesNotContain(page.Songs, s => s.Id == "deezer:1");
        Assert.Empty(rig.Downloads.History);
        Assert.False(rig.Online.CanDownload);
    }

    [Fact]
    public async Task SongDismissalIsSpecific_PersistsAcrossSourceRestart_AndUndoDoesNotUnblockUnrelatedChoices()
    {
        using var rig = new Rig();
        var page = Assert.Single(await rig.Discovery.LoadAsync(false, default));
        var one = page.Songs[0]; var two = page.Songs[1];
        var receipt = await rig.Discovery.HideAsync(page.Id, one);
        Assert.NotNull(receipt);
        page = Assert.Single(await rig.Discovery.LoadAsync(false, default));
        Assert.DoesNotContain(page.Songs, s => s.Id == one.Id);
        Assert.Contains(page.Songs, s => s.Id == two.Id); // same artist, different song stays
        using (var reloaded = rig.NewSource())
        {
            var restarted = new FinderDiscovery(reloaded, rig.Online, rig.Library);
            Assert.DoesNotContain(Assert.Single(await restarted.LoadAsync(false, default)).Songs, s => s.Id == one.Id);
        }
        Assert.True(await rig.Discovery.UndoAsync(receipt!));
        Assert.False(await rig.Discovery.UndoAsync(receipt!)); // one-shot receipt
        Assert.Contains(Assert.Single(await rig.Discovery.LoadAsync(false, default)).Songs, s => s.Id == one.Id);
    }

    [Fact]
    public async Task ExplicitArtistBlockRemovesItsSongs_ButUndoRestoresThem_AndUnknownIdsAreIgnored()
    {
        using var rig = new Rig();
        var page = Assert.Single(await rig.Discovery.LoadAsync(false, default));
        Assert.Null(await rig.Discovery.HideAsync("wrong-page", page.Songs[0]));
        var receipt = await rig.Discovery.HideAsync(page.Id, page.Songs[0], artist: true);
        Assert.Equal("deezer:3", Assert.Single(Assert.Single(await rig.Discovery.LoadAsync(false, default)).Songs).Id);
        Assert.True(await rig.Discovery.UndoAsync(receipt!));
        Assert.Equal(3, Assert.Single(await rig.Discovery.LoadAsync(false, default)).Songs.Count);
    }
}
