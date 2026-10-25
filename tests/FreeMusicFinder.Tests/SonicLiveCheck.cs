using System.Diagnostics;

namespace FreeMusicFinder.Tests;

/// <summary>Opt-in live check of the active sound lane: the TEST taste (read only), today's public catalog and real
/// previews, the real encoder, all written to a scratch folder. The TEST profile is never written; no key, no Telegram.</summary>
internal static class SonicLiveCheck
{
    public static async Task RunAsync(string work)
    {
        work = Path.GetFullPath(work);
        string profile = Environment.GetEnvironmentVariable("FMF_SONIC_TEST_PROFILE") ?? "";
        if (new DirectoryInfo(profile).Name != "profile" || new DirectoryInfo(profile).Parent?.Name != "NoctisPlus-Test")
            throw new ArgumentException("FMF_SONIC_TEST_PROFILE must be the in-place NoctisPlus-Test/profile");
        string model = Environment.GetEnvironmentVariable("FMF_SONIC_MODEL") ?? throw new ArgumentException("FMF_SONIC_MODEL required");
        string exe = Environment.GetEnvironmentVariable("NOCTIS_TEST_FFMPEG") ?? throw new ArgumentException("NOCTIS_TEST_FFMPEG required");
        string proxy = Environment.GetEnvironmentVariable("FMF_LIVE_PROXY") ?? "";
        Directory.CreateDirectory(work);
        // The public metadata cache is copied so lookups made here never land in the TEST profile.
        var source = Path.Combine(profile, "plugin-data", "dev.moshi.freemusicfinder", "artist-catalog");
        var folder = Path.Combine(work, "artist-catalog");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(folder, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(file, target);
        }
        var (library, albums) = DiscoverReplay.ReadLibrary(Path.Combine(profile, "library.json"));
        var plays = TasteSources.Load(profile);
        var playlists = TasteSources.Playlists(profile);
        using var catalog = new ArtistCatalog(folder, PlaylistCatalog.MetadataHandler(proxy));
        using var similar = new SimilarArtists(folder, PlaylistCatalog.MetadataHandler(proxy));
        // Saved public relations only: a fixture token that never leaves (the refusing route below is never asked when cached).
        using var lastFm = File.Exists(Path.Combine(folder, "lastfm.json"))
            ? new LastFmSimilar(folder, new Refusing(), () => new string('0', 32), () => DateTimeOffset.UtcNow, TimeSpan.Zero) : null;
        var provider = new CatalogForDiscover(() => catalog, name => albums.GetValueOrDefault(BotText.Key(Taste.MainArtist(name))) ?? [], () => similar, () => lastFm);
        using var metadata = new ArtistCatalog(Path.Combine(work, "sonic-metadata"), PlaylistCatalog.MetadataHandler(proxy));
        var lines = new List<string>();
        void Log(string line) { lock (lines) lines.Add(line); Console.WriteLine("  " + line); }
        using var service = new SonicService(new SonicCache(Path.Combine(work, "sonic-embeddings"), SonicFeature.ModelSpace),
            new SonicEncoder(model, SonicFeature.ModelSpace, () => exe, SonicFeature.ModelSha256),
            new SonicPreviews(PlaylistCatalog.MetadataHandler(proxy)), SonicFeature.Resolver(metadata), () => false, Log) { Enabled = true };
        var feedback = new DiscoverFeedback(Path.Combine(work, "feedback.json"));
        var hidden = new DiscoverHidden(Path.Combine(work, "hidden.json"));
        async Task<List<string>> Page(string name)
        {
            var clock = Stopwatch.StartNew();
            var result = await Discover.ForListenerAsync(plays, library, playlists, feedback, hidden, provider, (_, _) => Task.FromResult(new HashSet<string>()),
                null, DateTimeOffset.UtcNow, CancellationToken.None, sonic: service);
            Console.WriteLine($"{name}: {result.Songs.Count} songs in {clock.Elapsed.TotalSeconds:0.0} s");
            return result.Songs.Take(Discover.Shelf).Select(s => DiscoverIdentity.Key(s.Track.Song)).ToList();
        }
        var first = await Page("page 1 (cold)");
        var clock = Stopwatch.StartNew();
        await service.Completion.WaitAsync(TimeSpan.FromMinutes(4));
        Console.WriteLine($"warm pass finished in {clock.Elapsed.TotalSeconds:0} s; vectors on disk {Directory.GetFiles(Path.Combine(work, "sonic-embeddings"), "*.json", SearchOption.AllDirectories).Length}");
        var second = await Page("page 2 (warm)");
        await service.Completion.WaitAsync(TimeSpan.FromMinutes(4));
        Console.WriteLine($"top {Discover.Shelf}: {second.Intersect(first).Count()} shared, {second.Where((k, i) => i < first.Count && first[i] != k).Count()} places changed");
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("Sonic live check: Last.fm is read from its saved public cache only");
    }
}
