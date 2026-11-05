using Noctis.Services.Search;

namespace FreeMusicFinder;

internal sealed partial class FinderOnlineSearch
{
    public IOnlineCatalog Browser => this;

    public async Task<OnlineAlbumPage> ReadAlbumAsync(OnlineAlbum release, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_pages.Enabled) throw new InvalidOperationException("Online catalog is disabled");
        // Only identities registered by Search or the existing library/artist resolver.
        // Caller-supplied artwork URLs and titles never become catalog request destinations.
        if (!_albums.TryGetValue(release.Id, out var known)) throw new InvalidOperationException("Search for this album again");
        var open = await _pages.ReadAlbumAsync(known, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return RememberAlbum(open);
    }

    public async Task<OnlineAlbumPage?> FindLibraryAlbumAsync(string artist, string title, IReadOnlyList<string> songTitles, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;
        var (identity, _) = await _pages.IdentifyAsync(artist, [title], songTitles, ct).ConfigureAwait(false);
        if (identity is null) return null;
        var release = await _pages.FindLibraryReleaseAsync(identity, title, ct).ConfigureAwait(false);
        if (release is null) return null;
        _albums[release.Id] = release;
        return await ReadAlbumAsync(AlbumRow(release), ct).ConfigureAwait(false);
    }

    internal static OnlineAlbum AlbumRow(CatalogRelease a) => new(a.Id, a.Title, a.Artist.Name,
        a.Date.Length >= 4 ? a.Date[..4] : "", a.Kind is "single" or "ep" ? a.Kind : "album",
        a.Count, a.Explicit, CatalogArtwork.SizedUrl(a.Artwork, CardArtwork) ?? "");

    internal OnlineAlbumPage RememberAlbum(CatalogAlbumOpen open)
    {
        var release = open.Album.Release;
        _albums[release.Id] = release;
        var rows = open.Album.Songs.Select(s =>
        {
            var song = Remember(new(s, release, release.Artwork));
            var local = open.Matches.TryGetValue(s.Id, out var match) ? match.Local?.Id : null;
            return new OnlineAlbumSong(song with { LocalTrackId = local }, s.Disc, s.Number);
        }).ToArray();
        return new(AlbumRow(release), rows, release.Date, open.Album.Notice);
    }
}
