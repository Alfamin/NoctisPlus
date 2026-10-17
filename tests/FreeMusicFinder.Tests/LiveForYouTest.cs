using System.Text.Json;

namespace FreeMusicFinder.Tests;

/// <summary>
/// Explicit opt-in: For you on the real listening history and library (read only) against the public catalog. Prints
/// the picks; never opens Telegram, downloads, or writes to Noctis. FMF_FORYOU_LIVE = Noctis's data folder,
/// FMF_DISCOVERY_CACHE = an isolated catalog cache folder.
/// </summary>
internal static class LiveForYouTest
{
    public static async Task RunAsync(string noctisData)
    {
        var folder = Environment.GetEnvironmentVariable("FMF_DISCOVERY_CACHE") ?? throw new ArgumentException("Set an isolated FMF_DISCOVERY_CACHE directory.");
        var library = new List<LocalSong>(); var albums = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(noctisData, "library.json"))))
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                string S(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var duration = TimeSpan.TryParse(S("duration"), out var d) ? d : TimeSpan.Zero;
                var song = new LocalSong(Guid.TryParse(S("id"), out var id) ? id : Guid.NewGuid(), S("artist"), S("title"), S("filePath"), duration, AlbumArtist: S("albumArtist"));
                library.Add(song);
                var key = BotText.Key(Taste.MainArtist(song.Artist.Length > 0 ? song.Artist : song.AlbumArtist));
                if (!albums.TryGetValue(key, out var list)) albums[key] = list = [];
                if (S("album") is { Length: > 0 } album && !list.Contains(album)) list.Add(album);
            }
        var plays = TasteSources.Load(noctisData);
        var seeds = Taste.Seeds(plays, library, DateTime.UtcNow);
        Console.WriteLine($"  library {library.Count} songs, {plays.Count} plays; seeds: " + string.Join(", ", seeds.Select(s => $"{s.Name} ({s.Weight:0.0})")));
        var route = PlaylistCatalog.MetadataHandler("", way => Console.WriteLine("  " + way));
        using var catalog = new ArtistCatalog(folder, route);
        using var similar = new SimilarArtists(folder, PlaylistCatalog.MetadataHandler("", _ => { }));
        string lastKey = "";
        if (Environment.GetEnvironmentVariable("FMF_LASTFM_KEY_FILE") is { Length: > 0 } keyFile)
        {
            using var privateKey = JsonDocument.Parse(File.ReadAllBytes(keyFile));
            if (!privateKey.RootElement.TryGetProperty("api_key", out var value) || value.ValueKind != JsonValueKind.String || !LastFmSimilar.ValidKey(value.GetString()))
                throw new ArgumentException("The private Last.fm key file has no valid api_key.");
            lastKey = value.GetString()!.Trim();
        }
        using var lastFm = new LastFmSimilar(folder, PlaylistCatalog.MetadataHandler("", _ => { }), () => lastKey);
        var matcher = new PlaylistMatcher(library);
        if (!lastFm.Enabled) foreach (var seed in seeds)
        {
            var mbid = await similar.ArtistIdAsync(seed.Name, seed.SongTitles, CancellationToken.None);
            var names = mbid is null ? [] : (await similar.SimilarAsync(mbid, CancellationToken.None)).Take(6).Select(a => a.Name).ToList();
            Console.WriteLine($"  seed {seed.Name}: {(mbid is null ? "not found in MusicBrainz" : "found")} via [{string.Join(" | ", seed.SongTitles)}] → {string.Join(", ", names)}");
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var provider = new CatalogForDiscover(() => catalog, name => albums.GetValueOrDefault(BotText.Key(name)) ?? [], () => similar, () => lastFm);
        Task<DiscoverResult> Build() => Discover.BuildAsync(seeds, provider,
            (songs, _) => Task.FromResult(songs.Where(s => matcher.Find(s.Request) is not null).Select(s => s.Id).ToHashSet(StringComparer.Ordinal)),
            new DiscoverHidden(Path.Combine(folder, "hidden.json")), new Progress<string>(text => Console.WriteLine("    … " + text)), stop.Token);
        var result = await Build();
        Console.WriteLine($"  {result.Songs.Count} songs in {clock.Elapsed.TotalSeconds:0.0} s. {result.Notice}");
        foreach (var s in result.Songs.Take(30))
            Console.WriteLine($"    {string.Join(", ", s.Track.Song.Artists.Select(a => a.Name))} - {s.Track.Song.Title}  [{s.Because}]");
        Console.WriteLine("  artists for you: " + string.Join(", ", result.Artists.Select(a => a.Artist.Name)));
        Check.True(result.Songs.Count > 0, "live For you: suggestions from the real listening history");
        Check.False(result.Songs.Any(s => matcher.Find(s.Track.Song.Request) is not null), "live For you: nothing suggested is already in the library");
        if (lastFm.Enabled)
        {
            clock.Restart(); var warm = await Build();
            Console.WriteLine($"  warm: {warm.Songs.Count} songs in {clock.Elapsed.TotalSeconds:0.000} s. {warm.Notice}");
            Check.True(warm.Songs.Select(s => s.Track.Song.Id).SequenceEqual(result.Songs.Select(s => s.Track.Song.Id)), "cached live build preserves ranking");
            var page = new ForYouCache(Path.Combine(folder, "for-you-last.json"));
            var now = DateTimeOffset.UtcNow; var until = lastFm.DataExpires < now.AddDays(7) ? lastFm.DataExpires : now.AddDays(7);
            page.Write(new(1, now, until, result)); clock.Restart(); var saved = page.Read(now);
            Console.WriteLine($"  saved page read: {clock.Elapsed.TotalMilliseconds:0.0} ms, available: {saved is not null}");
        }
    }
}
