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
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        foreach (var part in text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var query = FinderQuery.Parse(part); var timer = System.Diagnostics.Stopwatch.StartNew();
            var artists = await catalog.SearchAsync(query.Artist.Length > 0 ? query.Artist : query.Text, false, stop.Token);
            var songs = await catalog.SearchSongsAsync(query.Term, stop.Token);
            var albums = await catalog.SearchAlbumsAsync(query.Term, stop.Token);
            var hits = SearchRanking.Build(query, artists.Value, songs.Value, albums.Value);
            if (hits.FirstOrDefault()?.Artist is {} leading && BotText.Key(leading.Name) == BotText.Key(query.Text))
            {
                var byArtist = await catalog.ArtistSongsAsync(leading, stop.Token);
                songs = songs with { Value = byArtist.Value.Concat(songs.Value).DistinctBy(s => s.Song.Id).ToList() };
                hits = SearchRanking.Build(query, artists.Value, songs.Value, albums.Value);
                Check.True(byArtist.Value.Count > 0 && byArtist.Value.All(s => s.Song.Artists.Any(a => a.Id == leading.Id)), "artist query retrieves songs using its exact source identity");
            }
            Check.True(hits.Count > 0, "live unified query has public results: " + part);
            var top = hits.First(); Console.WriteLine($"  TOP {top.Kind}: {top.Title} / {top.Subtitle}; {artists.Value.Count} artists, {songs.Value.Count} songs, {albums.Value.Count} albums; {timer.Elapsed.TotalSeconds:F1}s");
            if (query.Artist.Length > 0) Check.True(top.Track is not null && top.Track.Song.Artists.Any(a => BotText.Key(a.Name) == BotText.Key(query.Artist)), "song-by-artist query leads with the named artist's recording");
            else if (part is "Flaws and Sins" or "God's Plan")
            {
                var intended = part == "Flaws and Sins" ? "Juice WRLD" : "Drake";
                var contextual = SearchRanking.Artists(hits).First();
                Console.WriteLine("  ARTISTS FILTER: " + contextual.Title + " / " + contextual.Subtitle);
                Check.Equal(intended, contextual.Artist?.Name, "song-title Artists filter leads with its credited performer, not a literal artist-name match");
            }
            else if (part == "Take Care") Check.True(top.Kind == "Albums" && top.Album?.Artist.Name == "Drake", "album title preserves provider relevance and leads with Drake's album");
            else Check.True(top.Artist is not null && BotText.Key(top.Title) == BotText.Key(part), "artist query leads with its exact artist");
            var repeat = await catalog.SearchSongsAsync(query.Term, stop.Token); Check.True(repeat.Cached, "repeat live query reuses its saved metadata");
        }
        Console.WriteLine($"{Check.Passed} ok, {Check.Failed} wrong. Public metadata only; zero Telegram requests, audio downloads or library writes.");
        if (Check.Failed > 0) throw new InvalidOperationException("Live unified search checks failed.");
    }
    private static JsonElement? Find(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault() : null;
}
