using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>Opt-in (FMF_PLAYLIST_SEARCH_LIVE="hype drake|gym"): owner #69's playlist search against the real Spotify
/// and Deezer, public metadata only. Prints each answer with its saves, song count and timing, then reads the top
/// playlist as the preview would. No Telegram, audio, library or settings.</summary>
internal static class LivePlaylistSearchTest
{
    public static async Task RunAsync(string queries, string? proxy)
    {
        using var catalog = new PlaylistCatalog(PlaylistCatalog.MetadataHandler(proxy ?? "", route => Console.WriteLine("  route: " + route)));
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try { Console.WriteLine("Spotify direct: " + (await catalog.SpotifySearchAsync("hype drake", 24, stop.Token)).Count); }
        catch (Exception ex) { Console.WriteLine("Spotify direct FAILED: " + ex); }
        foreach (var query in queries.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var (found, source) = await catalog.SearchPlaylistsAsync(query, 24, stop.Token);
            Console.WriteLine($"[{query}] {source}: {found.Count} playlists in {timer.ElapsedMilliseconds} ms ({PlaylistCatalog.LastCounts})");
            foreach (var p in found.Take(12)) Console.WriteLine($"  {p.Saves,10} saves {p.Tracks,5} songs  {p.Name} ({p.Owner}) {(p.Artwork.Length > 0 ? "" : "NO COVER")}");
            Check.True(found.Count > 0, "live playlist search answers: " + query);
            timer.Restart();
            (found, _) = await catalog.SearchPlaylistsAsync(query, 24, stop.Token);
            Console.WriteLine($"  again: {timer.ElapsedMilliseconds} ms (token and counts remembered)");
            timer.Restart();
            var deezer = await catalog.DeezerSearchAsync(query, 10, stop.Token);
            Console.WriteLine($"  Deezer backup: {deezer.Count} in {timer.ElapsedMilliseconds} ms: " + string.Join(" | ", deezer.Take(5).Select(d => $"{d.Name} [{d.Tracks}]")));
            Check.True(deezer.Count > 0, "Deezer backup answers: " + query);
        }
        var first = (await catalog.SearchPlaylistsAsync(queries.Split('|')[0], 24, stop.Token)).Playlists[0];
        MusicLink.TryParse(first.Url, out var link);
        var read = System.Diagnostics.Stopwatch.StartNew();
        var manifest = await catalog.ReadAsync(link!, null, _ => { }, _ => { }, stop.Token);
        Console.WriteLine($"Preview read of \"{first.Name}\": {manifest.Total} entries, {manifest.Entries.Count(e => e.Request is not null)} with metadata, {read.ElapsedMilliseconds} ms");
        Check.True(manifest.CompleteIds && manifest.Entries.Count == manifest.Total, "the top playlist reads in full for the preview");
    }
}
