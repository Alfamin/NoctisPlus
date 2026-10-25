using System.Text.Json;

namespace FreeMusicFinder.Tests;

/// <summary>Explicit, offline TEST-only comparison using the SAME candidate builder and diversity selection as the shelf.
/// Current public metadata is replayed, not historical catalog snapshots. No hidden actions or current favorites leak
/// backward into taste. A small TEST history is a diagnostic, not a population-level promotion claim.</summary>
internal static class SonicReplayTest
{
    private sealed record Event(string key, string artist, string title, DateTime at, int label);
    private sealed record InputSong(string key, string artist, string title);
    private sealed record Input(List<Event> events, List<InputSong> tracks);
    private sealed class Refusing : HttpMessageHandler
    {
        public int Misses;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Misses); throw new HttpRequestException("Offline sonic replay: metadata cache miss"); }
    }
    private sealed class Scorer(Dictionary<string, SonicVector> vectors, double blend) : IDiscoverSonic
    {
        public int Pool, Covered;
        public List<CatalogSong> Candidates = [];
        public Task<IReadOnlyDictionary<string, double>> RankAsync(IReadOnlyList<CatalogSong> candidates,
            IReadOnlyList<TasteRecording> liked, IReadOnlyList<TastePlay> plays, CancellationToken ct)
        {
            Candidates = candidates.ToList(); Pool = candidates.Count;
            Covered = candidates.Count(s => vectors.ContainsKey(DiscoverIdentity.Key(s)));
            var taste = liked.Where(l => vectors.ContainsKey(DiscoverIdentity.Key(l.Title, l.Artist)))
                .Select(l => new SonicExample(DiscoverIdentity.Key(l.Title, l.Artist), vectors[DiscoverIdentity.Key(l.Title, l.Artist)], l.Weight)).ToList();
            foreach (var g in plays.Where(p => p.Weight < 0).GroupBy(p => DiscoverIdentity.Key(p.Title, p.Artist)))
                if (vectors.TryGetValue(g.Key, out var v)) taste.Add(new(g.Key, v, Math.Max(-1, g.Sum(p => p.Weight))));
            return Task.FromResult<IReadOnlyDictionary<string, double>>(SonicRanking.Multipliers(candidates, vectors, taste, blend));
        }
    }
    private sealed record Row(string Model, double Blend, string Day, int Candidates, int Covered, int EligibleHeldOut,
        int PoolHeldOut, int Hit10, int Hit20, double Ndcg20, double Mrr, int Artists, int MaxPerArtist);

    public static async Task RunAsync(string work)
    {
        work = Path.GetFullPath(work);
        string profile = Environment.GetEnvironmentVariable("FMF_SONIC_TEST_PROFILE") ?? "";
        if (new DirectoryInfo(profile).Name != "profile" || new DirectoryInfo(profile).Parent?.Name != "NoctisPlus-Test")
            throw new ArgumentException("FMF_SONIC_TEST_PROFILE must be the in-place NoctisPlus-Test/profile");
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(Path.Combine(work, "manifest.json")))!;
        var names = input.tracks.ToDictionary(t => t.key, t => DiscoverIdentity.Key(t.title, t.artist));
        var events = input.events.OrderBy(e => e.at).ToList();
        var days = events.Select(e => e.at.Date).Distinct().Skip(1).ToList();
        var models = Directory.GetFiles(work, "*-vectors.json").ToDictionary(p => Path.GetFileName(p)[..^"-vectors.json".Length], p =>
            (JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(p)) ?? [])
                .Where(v => names.ContainsKey(v.Key)).GroupBy(v => names[v.Key]).ToDictionary(g => g.Key, g => SonicVector.Create(Path.GetFileName(p), "preview", g.First().Value)!)
                .Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value));
        var allCandidates = new Dictionary<string, CatalogSong>();
        var results = new List<Row>();
        var (library, albums) = DiscoverReplay.ReadLibrary(Path.Combine(profile, "library.json"));
        using var refuse = new Refusing();
        // Today's metadata is deliberately separate from the history cutoff. No historical catalog is available.
        var cacheTime = DateTimeOffset.UtcNow;
        var folder = Path.Combine(profile, "plugin-data", "dev.moshi.freemusicfinder", "artist-catalog");
        // FMF_SONIC_REPLAY_CATALOG: a copy of the public cache (made on first use) so FMF_SONIC_REPLAY_FILL=1 can add the
        // catalog pages the replay's artists need without writing the TEST profile. Last.fm stays cache-only either way.
        if (Environment.GetEnvironmentVariable("FMF_SONIC_REPLAY_CATALOG") is { Length: > 0 } copy)
        {
            if (!Directory.Exists(copy))
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(copy, Path.GetRelativePath(folder, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
            folder = Path.GetFullPath(copy);
        }
        bool fill = Environment.GetEnvironmentVariable("FMF_SONIC_REPLAY_FILL") == "1" && folder != Path.Combine(profile, "plugin-data", "dev.moshi.freemusicfinder", "artist-catalog");
        string proxy = Environment.GetEnvironmentVariable("FMF_LIVE_PROXY") ?? "";
        using var catalog = fill ? new ArtistCatalog(folder, PlaylistCatalog.MetadataHandler(proxy)) : new ArtistCatalog(folder, refuse, () => cacheTime, TimeSpan.Zero);
        using var similar = fill ? new SimilarArtists(folder, PlaylistCatalog.MetadataHandler(proxy)) : new SimilarArtists(folder, new Refusing(), () => cacheTime, TimeSpan.Zero);
        // Fixture token only enables reading saved PUBLIC relations; the refusing handler guarantees it never leaves
        // this process. Never read the owner's real key, credentials or settings for an offline replay.
        using var lastFm = File.Exists(Path.Combine(folder, "lastfm.json"))
            ? new LastFmSimilar(folder, new Refusing(), () => new string('0', 32), () => cacheTime, TimeSpan.Zero) : null;
        var provider = new CatalogForDiscover(() => catalog, name => albums.GetValueOrDefault(BotText.Key(Taste.MainArtist(name))) ?? [], () => similar, () => lastFm);
        // What the live shelf's sound lane gets today: the same taste sources, read-only. Fewer than two liked songs keeps it idle.
        var nowPlays = TasteSources.Load(profile);
        var nowLiked = new List<TasteRecording>();
        Taste.Seeds(nowPlays, library, DateTime.UtcNow, playlists: TasteSources.Playlists(profile), liked: nowLiked);
        Console.WriteLine($"live taste now: {nowPlays.Count} plays ({nowPlays.Count(p => p.Weight > 0)} positive), {library.Count} library songs, {nowLiked.Count} liked recordings"
            + $" (preview vectors for {nowLiked.Count(l => models.Values.Any(m => m.ContainsKey(DiscoverIdentity.Key(l.Title, l.Artist))))})");
        foreach (var day in days)
        {
            var before = events.Where(e => e.at.Date < day).Select(e => new TastePlay(e.artist, e.at, e.label > 0 ? 1.5 : -.7, e.title, Heard: e.label > 0, Sampled: e.label < 0)).ToList();
            if (before.Count(p => p.Weight > 0) < 2) continue;
            var known = before.Where(p => p.Heard).Select(p => DiscoverIdentity.Key(p.Title, p.Artist)).ToHashSet();
            var held = events.Where(e => e.at.Date == day && e.label > 0).Select(e => DiscoverIdentity.Key(e.title, e.artist)).Where(k => !known.Contains(k)).ToHashSet();
            if (held.Count == 0) continue;
            foreach (var model in new[] { "baseline" }.Concat(fill ? [] : models.Keys))
            foreach (double blend in model == "baseline" ? new[] { 0d } : new[] { .1, .2, .35 })
            {
                var scorer = new Scorer(model == "baseline" ? [] : models[model], blend);
                var feedback = new DiscoverFeedback(Path.Combine(work, "empty-feedback.json"), () => new DateTimeOffset(day, TimeSpan.Zero));
                // No current library/favorites/playlist marks are used: their historical state is unknown.
                var result = await Discover.ForListenerAsync(before, [], [], feedback, new DiscoverHidden(Path.Combine(work, "empty-hidden.json")), provider,
                    (_, _) => Task.FromResult(new HashSet<string>()), null, new DateTimeOffset(day, TimeSpan.Zero), CancellationToken.None, sonic: scorer);
                foreach (var s in scorer.Candidates) allCandidates[s.Id] = s;
                var page = result.Songs.Take(Discover.Shelf).ToList();
                var keys = page.Select(s => DiscoverIdentity.Key(s.Track.Song)).ToList();
                double ideal = Enumerable.Range(0, Math.Min(20, held.Count)).Sum(i => 1 / Math.Log2(i + 2));
                double dcg = keys.Select((k, i) => held.Contains(k) ? 1 / Math.Log2(i + 2) : 0).Sum();
                int first = keys.FindIndex(held.Contains);
                var metrics = DiscoverPageMetrics.Of(page, new HashSet<string>(), known);
                results.Add(new(model, blend, day.ToString("yyyy-MM-dd"), scorer.Pool, scorer.Covered, held.Count,
                    scorer.Candidates.Count(s => held.Contains(DiscoverIdentity.Key(s))), keys.Take(10).Count(held.Contains), keys.Count(held.Contains),
                    ideal > 0 ? dcg / ideal : 0, first >= 0 ? 1d / (first + 1) : 0, metrics.Artists, metrics.MaxPerArtist));
            }
        }
        var report = new { Note = "TEST-only chronological diagnostic. Today's cached metadata, no historical ownership, no historical favorites. Audit recordings excluded upstream. Model/blend tuning must use earlier folds; later folds are holdout. Missing candidates are retrieval failures, not ranking failures.",
            Rows = results, MetadataMisses = refuse.Misses, PoolRecordings = allCandidates.Count };
        File.WriteAllText(Path.Combine(work, "engine-replay.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(work, "pool-songs.json"), JsonSerializer.Serialize(allCandidates.Values));
        foreach (var g in results.GroupBy(r => (r.Model, r.Blend)))
            Console.WriteLine($"{g.Key.Model} blend {g.Key.Blend:0.00}: {g.Count()} folds, hit20 {g.Sum(r => r.Hit20)}/{g.Sum(r => r.EligibleHeldOut)}, ndcg20 {g.Average(r => r.Ndcg20):0.000}, mrr {g.Average(r => r.Mrr):0.000}, coverage {g.Sum(r => r.Covered)}/{g.Sum(r => r.Candidates)}, artists {g.Average(r => r.Artists):0.0}");
        Console.WriteLine($"{allCandidates.Count} pool recordings; metadata misses {refuse.Misses}; no network requests sent.");
    }
}
