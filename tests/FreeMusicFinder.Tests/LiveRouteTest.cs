using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>
/// Optional: reads the metadata of public Spotify or Deezer links the way the plugin does on this
/// computer and says which way it went. FMF_LIVE_ROUTE holds the links, FMF_LIVE_PROXY the "Proxy"
/// setting to try (empty: the computer's own proxy). Metadata only: no bot, no audio.
/// </summary>
internal static class LiveRouteTest
{
    public static async Task RunAsync(string links, string? setting)
    {
        Check.Section($"live: reading metadata with the Proxy setting \"{setting}\"");
        foreach (var text in links.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!MusicLink.TryParse(text, out var link)) throw new ArgumentException("Not a Spotify or Deezer link: " + text);
            var ways = new List<string>();
            using var catalog = new PlaylistCatalog(PlaylistCatalog.MetadataHandler(setting ?? "", way => { lock (ways) ways.Add(way); }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            string result;
            try
            {
                var manifest = await catalog.ReadAsync(link!, null, _ => { }, _ => { }, timeout.Token);
                result = $"{manifest.Total} entries, {manifest.Entries.Count(e => e.Request is not null)} with title and artist";
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidDataException)
            {
                result = ex.Message;
            }
            Console.WriteLine($"  {link!.Service} {link.Kind}: {(ways.Count == 0 ? "no connection was opened" : string.Join("; ", ways))}\n        -> {result}");
        }
    }
}
