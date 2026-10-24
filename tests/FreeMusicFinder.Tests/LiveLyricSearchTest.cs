using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>Owner #155, opt-in (FMF_LYRICS_LIVE="lyric=>Artist|..."): the unified search on public metadata (catalog +
/// Spotify's song search). No Telegram, audio or library access. FMF_LIVE_PROXY optionally names the proxy.</summary>
internal static class LiveLyricSearchTest
{
    public static async Task RunAsync(string spec, string? proxy)
    {
        var folder = Check.NewFolder("live-lyrics");
        using var catalog = new ArtistCatalog(Path.Combine(folder, "catalog"), PlaylistCatalog.MetadataHandler(proxy ?? ""));
        var sources = new MusicSources(new TransferTests.Source("DeezLoad"), new TransferTests.Source("Music Hunters"), _ => { });
        using var downloads = new Downloads(_ => { }, Path.Combine(folder, "queue.json"), sources.Track);
        using var spotify = new SpotifyPlaylists(Path.Combine(folder, "imports.json"), new SpotifyPlaylistTests.Library(), downloads, sources.Track,
            new PlaylistCatalog(PlaylistCatalog.MetadataHandler(proxy ?? "")));
        var actions = new DiscoveryActions(new SpotifyPlaylistTests.Library(), downloads, _ => throw new InvalidOperationException("No audio"), () => folder, () => false);
        using var pages = new CatalogPages(() => catalog, actions, _ => { }, () => true);
        var search = new FinderOnlineSearch(() => catalog, actions, pages, () => false, () => { }, () => spotify);
        try
        {
            for (var round = 0; round < 2; round++)
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                var hits = await spotify.SearchTracksAsync("you used to call me on my cell phone late night", 5, default);
                var spotifyMs = t.ElapsedMilliseconds; t.Restart();
                var copies = await catalog.SearchSongsAsync(hits[0].Artists[0] + " " + hits[0].Title, default);
                Console.WriteLine($"  timing round {round}: Spotify song search {spotifyMs} ms, catalog lookup {t.ElapsedMilliseconds} ms (cached {copies.Cached})");
            }
            foreach (var part in spec.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var split = part.Split("=>", 2, StringSplitOptions.TrimEntries);
                var (text, expected) = (split[0], split.Length > 1 ? split[1] : "");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Noctis.Services.Search.OnlineSearchResult? final = null; long? lyricAt = null;
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var steps = new List<string>();
                await foreach (var update in search.SearchAsync(text, stop.Token))
                {
                    steps.Add($"{clock.ElapsedMilliseconds}:{update.Songs.Count}s/{update.Artists.Count}a/{update.Albums.Count}al/{update.Playlists.Count}p{(update.LyricsMatch is null ? "" : "/L")}");
                    final = update;
                    if (lyricAt is null && update.LyricsMatch is not null) lyricAt = clock.ElapsedMilliseconds;
                }
                var top = final?.Songs.FirstOrDefault(s => s.Id == final.LyricsMatch);
                Console.WriteLine($"  \"{text}\" -> {(top is null ? "no lyrics match" : $"{top.ArtistDisplay} - {top.Title}")}" +
                    $" (lyrics at {lyricAt?.ToString() ?? "-"} ms, complete {clock.ElapsedMilliseconds} ms; first song: {final?.Songs.FirstOrDefault()?.Title})");
                Console.WriteLine("    steps " + string.Join(" ", steps));
                if (expected == "none") Check.True(top is null, "no lyrics match for a name search: " + text);
                else if (expected.Length > 0)
                    Check.True(top is not null && top.ArtistDisplay.Contains(expected, StringComparison.OrdinalIgnoreCase)
                        && final!.Top is ("song", var id) && id == top.Id, $"\"{text}\" leads with {expected}");
            }
        }
        finally { search.Detach(); }
    }
}
