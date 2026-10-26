using System.Collections.Concurrent;
using System.Diagnostics;

namespace FreeMusicFinder.Tests;

/// <summary>Owner #175: opt-in timing of real page builds on a profile copy, against the live catalogs, counting requests
/// per host. FMF_FORYOU_TIME = a catalog folder to use (a copy; it gets written), FMF_FORYOU_PROFILE = the profile copy.
/// Builds a first page, then a refresh. Never reads keys, settings or Telegram files; Last.fm stays cache-only.</summary>
internal static class ForYouTimingCheck
{
    private sealed class Counting(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public readonly ConcurrentDictionary<string, int> Hosts = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hosts.AddOrUpdate(request.RequestUri!.Host, 1, (_, n) => n + 1);
            if (request.RequestUri.Host.Contains("apple")) Console.WriteLine("  apple " + request.RequestUri.PathAndQuery[..Math.Min(90, request.RequestUri.PathAndQuery.Length)]);
            return base.SendAsync(request, ct);
        }
    }
    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("Last.fm is read from its saved public cache only");
    }

    public static async Task RunAsync(string folder)
    {
        string profile = Path.GetFullPath(Environment.GetEnvironmentVariable("FMF_FORYOU_PROFILE") ?? "");
        if (!profile.Contains(@"\artifacts\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("FMF_FORYOU_PROFILE must be a copy under artifacts");
        string proxy = Environment.GetEnvironmentVariable("FMF_LIVE_PROXY") ?? "";
        var (library, albums) = DiscoverReplay.ReadLibrary(Path.Combine(profile, "library.json"));
        var plays = TasteSources.Load(profile);
        var playlists = TasteSources.Playlists(profile);
        var counting = new Counting(PlaylistCatalog.MetadataHandler(proxy));
        using var catalog = new ArtistCatalog(folder, counting);
        using var similar = new SimilarArtists(folder, counting);
        using var lastFm = File.Exists(Path.Combine(folder, "lastfm.json"))
            ? new LastFmSimilar(folder, new Refusing(), () => new string('0', 32), () => DateTimeOffset.UtcNow, TimeSpan.Zero) : null;
        var provider = new CatalogForDiscover(() => catalog, name => albums.GetValueOrDefault(BotText.Key(Taste.MainArtist(name))) ?? [], () => similar, () => lastFm);
        var work = Path.Combine(Path.GetTempPath(), "foryou-time-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var feedback = new DiscoverFeedback(Path.Combine(work, "feedback.json"));
        var hidden = new DiscoverHidden(Path.Combine(work, "hidden.json"));
        var kept = new HashSet<string>(StringComparer.Ordinal);
        for (int page = 1; page <= 2; page++)
        {
            counting.Hosts.Clear();
            var clock = Stopwatch.StartNew();
            string last = "";
            var progress = new Progress<string>(p => last = p);
            var result = await Discover.ForListenerAsync(plays, library, playlists, feedback, hidden, provider, (_, _) => Task.FromResult(new HashSet<string>()),
                progress, DateTimeOffset.UtcNow, CancellationToken.None, onPages: kept);
            Console.WriteLine($"page {page}: {result.Songs.Count} songs in {clock.Elapsed.TotalSeconds:0.0} s; requests: "
                + string.Join(", ", counting.Hosts.OrderByDescending(h => h.Value).Select(h => $"{h.Key} {h.Value}")) + $"; last progress \"{last}\"");
            kept.UnionWith(result.Songs.Take(Discover.Shelf).Select(s => DiscoverIdentity.Key(s.Track.Song)));
        }
    }
}
