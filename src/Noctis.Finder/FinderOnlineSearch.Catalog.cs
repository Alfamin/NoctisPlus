using Noctis.Services.Search;

namespace FreeMusicFinder;

internal sealed partial class FinderOnlineSearch
{
    public IOnlineCatalog Browser => this;

    public OnlineAlbum? KnownAlbum(string id) => _albums.TryGetValue(id, out var release) ? AlbumRow(release) : null;

    public async Task<OnlineArtistPage?> ReadArtistAsync(OnlineArtist artist, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_pages.Enabled) throw new InvalidOperationException("Online catalog is disabled");
        if (!_artists.TryGetValue(artist.Id, out var known)) throw new InvalidOperationException("Search for this artist again");
        var catalog = Catalog;
        var profileTask = catalog.ArtistAsync(known, false, ct);
        async Task<(List<CatalogSearchSong> Rows, string Warning)> ReadSongs()
        {
            try { return ((await catalog.ArtistSongsAsync(known, false, ct).ConfigureAwait(false)).Value, ""); }
            catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
            { return ([], "Online songs unavailable"); }
        }
        var songsTask = ReadSongs();
        await Task.WhenAll(profileTask, songsTask).ConfigureAwait(false);
        var profile = await profileTask.ConfigureAwait(false);
        var songs = await songsTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var verified = songs.Rows.Where(s => s.Song.Artists.Any(a => a.Id == known.Id)).ToArray();
        IReadOnlyDictionary<string, DiscoveryMatch> matches;
        try { matches = await _actions.MatchAsync(verified.Select(s => s.Song).ToArray(), ct, reuse: true).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
        { matches = new Dictionary<string, DiscoveryMatch>(); }
        foreach (var release in profile.Value.Releases) _albums[release.Id] = release;
        var rows = verified.Select(s => Remember(s) with
        { LocalTrackId = matches.TryGetValue(s.Song.Id, out var match) ? match.Local?.Id : null }).ToArray();
        var actual = profile.Value.Artist; _artists[actual.Id] = actual;
        return new(new(actual.Id, actual.Name, CatalogArtwork.LargeUrl(actual.Picture) ?? actual.Picture, actual.Genre), profile.Value.Releases.Select(AlbumRow).ToArray(), rows,
            songs.Warning.Length > 0 ? songs.Warning : profile.Value.Notice);
    }

    public async Task<OnlineArtistPage?> FindLibraryArtistAsync(string name, IReadOnlyList<string> albums, IReadOnlyList<string> titles, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_pages.Enabled || string.IsNullOrWhiteSpace(name)) return null;
        var (identity, _) = await _pages.IdentifyAsync(name, albums, titles, ct).ConfigureAwait(false);
        if (identity is null) return null;
        _artists[identity.Id] = identity;
        return await ReadArtistAsync(new(identity.Id, identity.Name), ct).ConfigureAwait(false);
    }

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
