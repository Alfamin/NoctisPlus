using System.Text.Json;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>Explicit opt-in public metadata probe. Never opens Telegram or reads music files.</summary>
internal static class LiveDiscoveryTest
{
    public static async Task RunAsync(string names)
    {
        var folder = Environment.GetEnvironmentVariable("FMF_DISCOVERY_CACHE") ?? throw new ArgumentException("Set an isolated FMF_DISCOVERY_CACHE directory.");
        var proxy = "";
        if (Environment.GetEnvironmentVariable("FMF_DISCOVERY_SETTINGS") is { Length: > 0 } path)
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(path));
            if (Find(settings.RootElement, "pluginSettingValues") is {} plugins && Find(plugins, "dev.moshi.freemusicfinder") is {} values && Find(values, "proxy") is {} setting && setting.ValueKind == JsonValueKind.String) proxy = setting.GetString() ?? "";
        }
        var ways = new List<string>(); var route = PlaylistCatalog.MetadataHandler(proxy, way => { ways.Add(way); Console.WriteLine("  " + way); });
        using var catalog = new ArtistCatalog(folder, route);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        foreach (var input in names.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = input.Split('=', 2); var name = pieces[0];
            var search = await catalog.SearchAsync(name, false, stop.Token);
            var exact = search.Value.Where(a => BotText.Key(a.Name) == BotText.Key(name) && (pieces.Length == 1 || a.Id == pieces[1])).ToArray();
            Check.Equal(1, exact.Length, "live metadata: one exact artist identity for " + name);
            if (exact.Length != 1) continue;
            if (Environment.GetEnvironmentVariable("FMF_IDENTITY_ALBUMS") is { Length: > 0 } evidence)
            {
                var previous = Environment.GetEnvironmentVariable("FMF_IDENTITY_OLD_ID");
                var identity = await catalog.IdentifyAsync(name, evidence.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), previous is null ? null : new(previous, name), stop.Token, search.Value);
                Check.Equal(exact[0].Id, identity.Artist?.Id, "live metadata: local album evidence selects the intended namesake");
                Check.True(identity.LibraryMatch, "live metadata: identity is supported by the artist's own releases");
                Console.WriteLine($"  IDENTITY {previous ?? "unselected"} → {identity.Artist?.Id}; {identity.Albums.Count} matching album(s).");
            }
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var artist = await catalog.ArtistAsync(exact[0], false, stop.Token);
            Console.WriteLine($"  PERF  {(artist.Cached ? "cached" : "network")} artist metadata ready: {elapsed.Elapsed.TotalMilliseconds:F1} ms");
            Check.True(artist.Value.Releases.Count > 0, "live metadata: artist releases for " + name);
            var title = Environment.GetEnvironmentVariable("FMF_DISCOVERY_ALBUM");
            var release = artist.Value.Releases.First(a => a.Count > 0 && (string.IsNullOrEmpty(title) || CatalogEditions.Same(a.Title, title)));
            var album = await catalog.AlbumAsync(release, false, stop.Token);
            Check.True(album.Value.Songs.Count > 0 && album.Value.Songs.All(s => !string.IsNullOrWhiteSpace(s.Request.Artist) && !string.IsNullOrWhiteSpace(s.Request.Title)), "live metadata: album songs produce usable recording requests");
            Console.WriteLine($"  {artist.Value.Artist.Name}: {artist.Value.Artist.Id.Split(':')[0]}, {artist.Value.Releases.Count} releases; {album.Value.Release.Title}: {album.Value.Songs.Count}/{album.Value.Release.Count} songs. {artist.Value.Notice} {album.Value.Notice}");
            var again = await catalog.AlbumAsync(release, false, stop.Token);
            Check.True(again.Cached, "live metadata: opening release again reuses saved catalog");
            if (!string.IsNullOrEmpty(title)) Check.Equal(1, artist.Value.Releases.Count(a => CatalogEditions.Same(a.Title, title)), "live metadata: one preferred album tile across clean/explicit variants");
            if (Environment.GetEnvironmentVariable("FMF_DISCOVERY_ART") == "1")
            {
                var hq = Environment.GetEnvironmentVariable("FMF_DISCOVERY_HQ") == "1";
                Task Show(string file) { var size = CatalogArtwork.Dimensions(File.ReadAllBytes(file)); Console.WriteLine($"  COVER {size.Width}x{size.Height}, {new FileInfo(file).Length} bytes"); return Task.CompletedTask; }
                var art = hq ? await catalog.ProgressiveArtworkAsync(album.Value.Release.Artwork, Show, stop.Token) : await catalog.ArtworkAsync(album.Value.Release.Artwork, stop.Token);
                Check.True(art is not null && File.Exists(art), "live metadata: album cover saved through the selected proxy route");
                Check.Equal(art, hq ? await catalog.ProgressiveArtworkAsync(album.Value.Release.Artwork, _ => Task.CompletedTask, stop.Token) : await catalog.ArtworkAsync(album.Value.Release.Artwork, stop.Token), "live metadata: repeated album cover uses cached file");
                if (hq && art is not null) Check.True(CatalogArtwork.Dimensions(File.ReadAllBytes(art)).Width >= 1000, "live artwork: final album cover is genuinely at least 1000 pixels");
                if (art is not null) Console.WriteLine("  Cover: " + new FileInfo(art).Length + " bytes in isolated metadata cache.");
            }
            if (Environment.GetEnvironmentVariable("FMF_DISCOVERY_PORTRAIT") == "1")
            {
                var hq = Environment.GetEnvironmentVariable("FMF_DISCOVERY_HQ") == "1";
                Task Show(string file) { var size = CatalogArtwork.Dimensions(File.ReadAllBytes(file)); Console.WriteLine($"  PORTRAIT {size.Width}x{size.Height}, {new FileInfo(file).Length} bytes"); return Task.CompletedTask; }
                var portrait = await catalog.PortraitAsync(exact[0], stop.Token, hq ? Show : null);
                Check.True(portrait is not null && File.Exists(portrait), "live metadata: exact artist portrait loads through selected proxy route");
                Check.Equal(portrait, await catalog.PortraitAsync(exact[0], stop.Token, hq ? _ => Task.CompletedTask : null), "live metadata: repeated artist portrait uses cached file");
                if (hq && portrait is not null) Check.True(CatalogArtwork.Dimensions(File.ReadAllBytes(portrait)).Width >= 640, "live artwork: final artist portrait is genuinely at least 640 pixels");
                if (portrait is not null) Console.WriteLine("  Portrait: " + new FileInfo(portrait).Length + " bytes in isolated metadata cache.");
            }
        }
        Console.WriteLine($"{Check.Passed} ok, {Check.Failed} wrong. Public metadata only; zero Telegram requests, zero audio downloads, zero library writes.");
        if (Check.Failed > 0) throw new InvalidOperationException("Live metadata checks failed.");
    }
    private static JsonElement? Find(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault() : null;
}
