using System.Text.Json;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>Opt-in public metadata only; no Telegram, audio or real library access.</summary>
internal static class LiveUnifiedSearchTest
{
    public static async Task RunAsync(string text)
    {
        var folder = Environment.GetEnvironmentVariable("FMF_DISCOVERY_CACHE") ?? throw new ArgumentException("Use an isolated catalog cache.");
        var proxy = "";
        if (Environment.GetEnvironmentVariable("FMF_DISCOVERY_SETTINGS") is { Length: > 0 } path)
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(path));
            if (Find(settings.RootElement, "pluginSettingValues") is {} plugins && Find(plugins, "dev.moshi.freemusicfinder") is {} values && Find(values, "proxy") is { ValueKind: JsonValueKind.String } value) proxy = value.GetString() ?? "";
        }
        using var catalog = new ArtistCatalog(folder, PlaylistCatalog.MetadataHandler(proxy, route => Console.WriteLine("  " + route)));
        // Owner #188: "query=>Who" checks that the top result is Who (the artist, or the artist of the song/album); a pause
        // between queries keeps a long sweep under Deezer's rate limit, which would otherwise switch it to Apple.
        var parts = text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var brief = Environment.GetEnvironmentVariable("FMF_UNIFIED_BRIEF") is { Length: > 0 };
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(4 + parts.Length / 3));
        var misses = new List<string>();
        foreach (var entry in parts)
        {
            var wanted = entry.Split("=>", 2, StringSplitOptions.TrimEntries) is { Length: 2 } pair ? pair[1] : null;
            var part = wanted is null ? entry : entry[..entry.IndexOf("=>", StringComparison.Ordinal)].Trim();
            if (brief) await Task.Delay(1500, stop.Token);
            var query = FinderQuery.Parse(part); var timer = System.Diagnostics.Stopwatch.StartNew();
            var artists = await catalog.SearchAsync(query.Artist.Length > 0 ? query.Artist : query.Text, false, stop.Token);
            var songs = await catalog.SearchSongsAsync(query.Term, stop.Token);
            var albums = await catalog.SearchAlbumsAsync(query.Term, stop.Token);
            songs = songs with { Value = await catalog.RefineTitleAsync(query, songs.Value, stop.Token) };
            await catalog.SongArtistFansAsync(query, songs.Value, stop.Token);
            var hits = SearchRanking.Build(query, artists.Value, songs.Value, albums.Value, fansOf: catalog.FansOf);
            if (hits.FirstOrDefault()?.Artist is {} leading && SearchRanking.NamesArtist(query.Text, leading))
            {
                var byArtist = await catalog.ArtistSongsAsync(leading, stop.Token);
                songs = songs with { Value = byArtist.Value.Concat(songs.Value).DistinctBy(s => s.Song.Id).ToList() };
                hits = SearchRanking.Build(query, artists.Value, songs.Value, albums.Value, fansOf: catalog.FansOf);
                Check.True(byArtist.Value.Count > 0 && byArtist.Value.All(s => s.Song.Artists.Any(a => a.Id == leading.Id)), "artist query retrieves songs using its exact source identity");
            }
            Check.True(hits.Count > 0, "live unified query has public results: " + part);
            var top = hits.First(); Console.WriteLine($"  TOP {top.Kind}: {top.Title} / {top.Subtitle}; {artists.Value.Count} artists, {songs.Value.Count} songs, {albums.Value.Count} albums; {timer.Elapsed.TotalSeconds:F1}s"
                + (songs.Value.Count > 0 && songs.Value.All(s => s.Song.Id.StartsWith("apple:", StringComparison.Ordinal)) ? "; SOURCE apple" : ""));
            if (wanted is not null)
            {
                var who = top.Artist?.Name ?? top.Track?.Song.Artists.FirstOrDefault()?.Name ?? top.Album?.Artist.Name ?? "";
                var ok = BotText.Key(who) == BotText.Key(wanted) || top.Track?.Song.Artists.Any(a => BotText.Key(a.Name) == BotText.Key(wanted)) == true;
                Console.WriteLine($"  {(ok ? "HIT " : "MISS")} \"{part}\" => {wanted}: {top.Kind} {top.Title} / {who}");
                if (!ok) misses.Add(part);
                if (brief) continue;
            }
            foreach (var hit in hits.Take(12)) Console.WriteLine($"  SCORE {hit.Score:F3} {hit.Kind}: {hit.Title} / {hit.Subtitle}; popularity={hit.Track?.Song.Popularity}; fans={(hit.Artist is { } ha ? catalog.FansOf(ha) : hit.Track?.Song.Artists.Select(catalog.FansOf).Max())}; reach={hit.Track?.Song.ArtistReach}");
            foreach (var hit in SearchRanking.Artists(hits).Take(6)) Console.WriteLine($"  ARTIST {hit.ArtistRank ?? hit.Score:F3}: {hit.Id} / {hit.Title}");
            if (query.Artist.Length > 0) Check.True(top.Track is not null && top.Track.Song.Artists.Any(a => BotText.Key(a.Name) == BotText.Key(query.Artist)), "song-by-artist query leads with the named artist's recording");
            else if (part.Equals("runaway", StringComparison.OrdinalIgnoreCase))
                Check.True(top.Track?.Song.Artists.Any(a => a.Name == "Kanye West") == true, "Runaway leads with Kanye West's recording");
            else if (part is "Flaws and Sins" or "God's Plan")
            {
                var intended = part == "Flaws and Sins" ? "Juice WRLD" : "Drake";
                var contextual = SearchRanking.Artists(hits).First();
                Console.WriteLine("  ARTISTS FILTER: " + contextual.Title + " / " + contextual.Subtitle);
                Check.Equal(BotText.Key(intended), BotText.Key(contextual.Artist?.Name ?? ""), "song-title Artists filter leads with its credited performer, not a literal artist-name match");
            }
            else if (part.StartsWith("plot twis", StringComparison.OrdinalIgnoreCase)) Check.True(top.Track?.Song.Artists.Any(a => a.Name == "Drake") == true, "owner #188: \"" + part + "\" leads with Drake's Plot Twist");
            else if (part.Equals("kanye", StringComparison.OrdinalIgnoreCase) || part.Equals("kany", StringComparison.OrdinalIgnoreCase)) Check.True(top.Artist?.Name == "Kanye West", "owner #188: \"" + part + "\" leads with Kanye West");
            else if (part == "Take Care") Check.True(top.Kind == "Albums" && top.Album?.Artist.Name == "Drake", "album title preserves provider relevance and leads with Drake's album");
            else Check.True(top.Artist is not null && BotText.Key(top.Title) == BotText.Key(part), "artist query leads with its exact artist");
            var repeat = await catalog.SearchSongsAsync(query.Term, stop.Token); Check.True(repeat.Cached, "repeat live query reuses its saved metadata");
            using var downloads = new Downloads(_ => { });
            var actions = new DiscoveryActions(new SpotifyPlaylistTests.Library(), downloads,
                _ => throw new InvalidOperationException("No audio in the metadata probe"), () => folder, () => false);
            using var pages = new CatalogPages(() => catalog, actions, _ => { }, () => true);
            var native = new FinderOnlineSearch(() => catalog, actions, pages, () => false, () => { });
            try
            {
                Noctis.Services.Search.OnlineSearchResult? final = null;
                await foreach (var update in native.SearchAsync(part, stop.Token)) final = update;
                foreach (var artist in final?.Artists.Take(6) ?? []) Console.WriteLine($"  NATIVE ARTIST {artist.Score:R}: {artist.Id} / {artist.Name}");
                Check.Equal(SearchRanking.Artists(hits).First().Artist?.Id, final?.Artists.First().Id,
                    "native catalog bridge preserves the same leading artist");
            }
            finally { native.Detach(); }
        }
        if (parts.Any(p => p.Contains("=>"))) Console.WriteLine($"TOP RESULTS: {parts.Count(p => p.Contains("=>")) - misses.Count} of {parts.Count(p => p.Contains("=>"))} as meant" + (misses.Count > 0 ? "; missed: " + string.Join(" | ", misses) : ""));
        Console.WriteLine($"{Check.Passed} ok, {Check.Failed} wrong. Public metadata only; zero Telegram requests, audio downloads or library writes.");
        if (Check.Failed > 0) throw new InvalidOperationException("Live unified search checks failed.");
    }
    private static JsonElement? Find(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault() : null;
}
