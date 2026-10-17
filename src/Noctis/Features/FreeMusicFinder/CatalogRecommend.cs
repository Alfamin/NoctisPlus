namespace FreeMusicFinder;

internal sealed record CatalogArtistSongs(CatalogArtist? Artist, List<CatalogSearchSong> Songs);

internal sealed partial class ArtistCatalog
{
    /// <summary>One request for a related artist's songs instead of an artist search followed by a lookup.
    /// Exact names only; use the ID represented by the most distinct recordings, and refuse a tie.
    /// Owned artists still use IdentifyAsync with local album evidence.</summary>
    public Task<CatalogResult<CatalogArtistSongs>> SongsByArtistNameAsync(string name, CancellationToken ct)
    {
        name = SearchText(name);
        return Cached("discover-artist-songs:" + BotText.Key(name), TimeSpan.FromDays(7), false, async () =>
        {
            List<CatalogSearchSong> songs;
            try
            {
                using var data = await Get("deezer", "/search/track?limit=50&q=" + Uri.EscapeDataString("artist:\"" + name.Replace("\"", "") + "\""), ct);
                songs = Data(data.RootElement).Select(t =>
                {
                    if (DeezerSong(t) is not { } song) return null;
                    var album = t.TryGetProperty("album", out var a) ? DeezerRelease(a, song.Artists[0]) : null;
                    return new CatalogSearchSong(song, album, album?.Artwork ?? "");
                }).Where(t => t is not null).Cast<CatalogSearchSong>().ToList();
                if (!songs.Any(s => BotText.Key(s.Song.Artists[0].Name) == BotText.Key(name))) throw Invalid("No exact Deezer artist songs; trying Apple.");
            }
            catch (Exception ex) when (Recoverable(ex))
            {
                using var data = await Get("apple", "/search?entity=song&attribute=artistTerm&media=music&country=US&explicit=Yes&limit=50&term=" + Uri.EscapeDataString(name), ct);
                songs = Results(data.RootElement).Select(t => AppleSong(t) is { } song ? new CatalogSearchSong(song, AppleRelease(t), Text(t, "artworkUrl100")) : null)
                    .Where(t => t is not null).Cast<CatalogSearchSong>().ToList();
            }
            var groups = songs.Where(s => BotText.Key(s.Song.Artists[0].Name) == BotText.Key(name)).GroupBy(s => s.Song.Artists[0].Id)
                .Select(g => (Artist: g.First().Song.Artists[0], Songs: SearchRanking.DeduplicateSongs(g).ToList())).OrderByDescending(g => g.Songs.Count).ToList();
            if (groups.Count == 0 || groups.Count > 1 && groups[0].Songs.Count == groups[1].Songs.Count)
                return new CatalogArtistSongs(null, []);
            return new CatalogArtistSongs(groups[0].Artist, groups[0].Songs);
        }, ct);
    }

    /// <summary>
    /// The artists Deezer's listeners pair with this one, in Deezer's order. Only Deezer artists have such a list; an
    /// Apple artist gives none. Public metadata, cached for a week like other slow-changing pages.
    /// </summary>
    public Task<CatalogResult<List<CatalogArtist>>> RelatedAsync(CatalogArtist artist, CancellationToken ct)
    {
        var (provider, id) = Split(artist.Id);
        if (provider != "deezer") return Task.FromResult(new CatalogResult<List<CatalogArtist>>([], _now(), false));
        return Cached("related:" + artist.Id, TimeSpan.FromDays(7), false, async () =>
        {
            using var data = await Get("deezer", "/artist/" + id + "/related?limit=25", ct);
            return Data(data.RootElement).Select(DeezerArtist).Where(a => a is not null).Cast<CatalogArtist>()
                .Where(a => a.Id != artist.Id).DistinctBy(a => a.Id).ToList();
        }, ct);
    }
}
