namespace FreeMusicFinder;

internal sealed partial class ArtistCatalog
{
    /// <summary>A provider can put several musicians under ONE id. Match the library's
    /// recordings against an independent catalog before accepting that association.
    /// Never use popularity, genre or a blacklist as identity proof.</summary>
    internal async Task<CatalogArtist> SeparateNamesakesAsync(CatalogArtist artist,
        IEnumerable<string> localAlbums, IEnumerable<string> localTitles, CancellationToken ct)
    {
        if (!artist.Id.StartsWith("deezer:", StringComparison.Ordinal)) return artist;
        var albums = ArtistEvidence.Albums(localAlbums);
        var titleText = localTitles.DistinctBy(BotText.Key).Take(40).ToList();
        var titles = titleText.Select(BotText.Key).Where(t => t.Length >= 4).ToHashSet();
        var ownedTitles = titles.ToHashSet();
        var hasLocalEvidence = albums.Count > 0 || titles.Count >= 2;
        try
        {
            // Browsing a related artist may have no local recordings at all. In that
            // case use the provider's first five ranked credits as anchors, not its
            // entire potentially merged discography. Competing evidence still ties.
            if (albums.Count == 0 && titles.Count < 2)
            {
                var ranked = await ArtistSongsAsync(artist, false, ct).ConfigureAwait(false);
                titleText = ranked.Value.Take(5).Select(t => t.Song.Title).ToList();
                titles = titleText.Select(BotText.Key).Where(t => t.Length >= 4).ToHashSet();
                if (titles.Count < 2) return artist;
            }
            var result = await AppleIdentitySongsAsync(artist.Name, ct).ConfigureAwait(false);
            var recordings = result.Value.ToList();
            CatalogArtist? Winner()
            {
            var candidates = recordings.Where(t => BotText.Key(t.Song.Artists[0].Name) == BotText.Key(artist.Name))
                .GroupBy(t => t.Song.Artists[0].Id)
                .Select(g => new
                {
                    Artist = g.First().Song.Artists[0],
                    Albums = ArtistEvidence.Matches(albums, g.Select(t => t.Album).OfType<CatalogRelease>()),
                    OwnSongs = g.Any(t => ownedTitles.Contains(BotText.Key(t.Song.Title))),
                    Songs = g.Where(t => titles.Contains(BotText.Key(t.Song.Title))).Select(t => BotText.Key(t.Song.Title)).Distinct().Count()
                }).Where(g => (ArtistEvidence.Strong(g.Albums) || g.Songs >= 2) && (!hasLocalEvidence || g.OwnSongs || ArtistEvidence.Strong(g.Albums)))
                .OrderByDescending(g => g.Songs).ThenByDescending(g => g.Albums.Count).ToArray();
            // Two independently matching namesakes remain ambiguous. Keep the explicit
            // provider selection instead of silently choosing the most famous one.
            if (candidates.Length == 1 || candidates.Length > 1 &&
                (candidates[0].Songs > candidates[1].Songs || candidates[0].Songs == candidates[1].Songs && candidates[0].Albums.Count > candidates[1].Albums.Count))
                return candidates[0].Artist;
            return null;
            }
            if (Winner() is { } known) return known;
            // A recording may have been withdrawn in one region. When one local title
            // already links the catalogs, other ranked credits can corroborate it.
            // Without that local link we cannot replace the user's chosen musician
            // merely because another namesake dominates the provider's top list.
            if (hasLocalEvidence && recordings.Any(t => ownedTitles.Contains(BotText.Key(t.Song.Title)) && t.Song.Artists.Any(a => BotText.Key(a.Name) == BotText.Key(artist.Name))))
            {
                var ranked = await ArtistSongsAsync(artist, false, ct).ConfigureAwait(false);
                foreach (var track in ranked.Value.Take(5)) titles.Add(BotText.Key(track.Song.Title));
                if (Winner() is { } corroborated) return corroborated;
            }
            // Broad store searches are capped and can omit even an artist's best-known
            // recording. Fill at most two missing anchors, never crawl every namesake.
            var attempts = 0;
            foreach (var title in titleText.Where(t => BotText.Key(t).Length >= 4).Take(5))
            {
                if (recordings.Any(t => BotText.Key(t.Song.Title) == BotText.Key(title) && t.Song.Artists.Any(a => BotText.Key(a.Name) == BotText.Key(artist.Name)))) continue;
                var targeted = await AppleIdentitySongsAsync(artist.Name + " " + title, ct).ConfigureAwait(false);
                recordings.AddRange(targeted.Value);
                if (Winner() is { } found) return found;
                if (++attempts >= 2) break;
            }
        }
        catch (Exception ex) when (Recoverable(ex)) { /* Offline keeps the existing catalog, never erases local music. */ }
        return artist;
    }

    private Task<CatalogResult<List<CatalogSearchSong>>> AppleIdentitySongsAsync(string name, CancellationToken ct) =>
        Cached("identity-recordings:apple:" + name.ToUpperInvariant(), TimeSpan.FromDays(7), false, async () =>
        {
            using var data = await Get("apple", "/search?entity=song&media=music&country=US&limit=200&term=" + Uri.EscapeDataString(name), ct);
            return Results(data.RootElement).Select(t => AppleSong(t) is { } song
                ? new CatalogSearchSong(song, AppleRelease(t), Text(t, "artworkUrl100")) : null)
                .OfType<CatalogSearchSong>().ToList();
        }, ct);

    /// <summary>Provider-ranked songs, fetched on the artist page, never during Search.
    /// Same-name foreign credits are excluded by id. Apple lookup is a catalog list,
    /// not a promise of a complete discography or a global popularity chart.</summary>
    internal Task<CatalogResult<List<CatalogSearchSong>>> ArtistSongsAsync(CatalogArtist artist, bool refresh, CancellationToken ct)
    {
        var (provider, id) = Split(artist.Id);
        return Cached("artist-songs:" + artist.Id, TimeSpan.FromDays(1), refresh, async () =>
        {
            if (provider == "deezer")
            {
                var rows = await Pages("/artist/" + id + "/top?limit=100", ct);
                return rows.Select(t =>
                {
                    var song = DeezerSong(t);
                    var album = t.TryGetProperty("album", out var a) ? DeezerRelease(a, song?.Artists.FirstOrDefault() ?? artist) : null;
                    return song is not null ? new CatalogSearchSong(song, album, album?.Artwork ?? "") : null;
                }).OfType<CatalogSearchSong>().Where(t => t.Song.Artists.Any(a => a.Id == artist.Id)).DistinctBy(t => t.Song.Id).ToList();
            }
            using var data = await Get("apple", "/lookup?id=" + id + "&entity=song&limit=200&country=US", ct);
            return Results(data.RootElement).Select(t => AppleSong(t) is { } song
                ? new CatalogSearchSong(song, AppleRelease(t), Text(t, "artworkUrl100")) : null)
                .OfType<CatalogSearchSong>().Where(t => t.Song.Artists.Any(a => a.Id == artist.Id)).DistinctBy(t => t.Song.Id).ToList();
        }, ct);
    }
}
