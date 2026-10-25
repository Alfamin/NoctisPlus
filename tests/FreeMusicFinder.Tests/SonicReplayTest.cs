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
    private sealed class Scorer(Dictionary<string, SonicVector> vectors, double blend, bool hold = false) : IDiscoverSonic
    {
        public int Pool, Covered;
        public List<CatalogSong> Candidates = [];
        /// <summary>The sound scores of the last build; with <c>hold</c> the page itself keeps the normal order.</summary>
        public IReadOnlyDictionary<string, double> Last = new Dictionary<string, double>();
        public Task<IReadOnlyDictionary<string, double>> RankAsync(IReadOnlyList<CatalogSong> candidates,
            IReadOnlyList<TasteRecording> liked, IReadOnlyList<TastePlay> plays, CancellationToken ct)
        {
            Candidates = candidates.ToList(); Pool = candidates.Count;
            Covered = candidates.Count(s => vectors.ContainsKey(DiscoverIdentity.Key(s)));
            var taste = liked.Where(l => vectors.ContainsKey(DiscoverIdentity.Key(l.Title, l.Artist)))
                .Select(l => new SonicExample(DiscoverIdentity.Key(l.Title, l.Artist), vectors[DiscoverIdentity.Key(l.Title, l.Artist)], l.Weight)).ToList();
            foreach (var g in plays.Where(p => p.Weight < 0).GroupBy(p => DiscoverIdentity.Key(p.Title, p.Artist)))
                if (vectors.TryGetValue(g.Key, out var v)) taste.Add(new(g.Key, v, Math.Max(-1, g.Sum(p => p.Weight))));
            Last = SonicRanking.Multipliers(candidates, vectors, taste, blend);
            return Task.FromResult(hold ? new Dictionary<string, double>() : Last);
        }
    }
    private sealed record Row(string Model, double Blend, string Day, int Candidates, int Covered, int EligibleHeldOut,
        int PoolHeldOut, int Hit10, int Hit20, double Ndcg20, double Mrr, int Artists, int MaxPerArtist, int[] FoundByRefresh, int PooledOverRefreshes, int FoundInPicks = 0);
    /// <summary>Pages built in a row with Refresh: 12 x 20 = 240 songs (owner: 200+). FMF_SONIC_REPLAY_REFRESHES overrides
    /// (8 reproduces the first TEST table).</summary>
    private static readonly int Refreshes = int.TryParse(Environment.GetEnvironmentVariable("FMF_SONIC_REPLAY_REFRESHES"), out var n) && n is > 0 and <= 50 ? n : 12;

    public static async Task RunAsync(string work)
    {
        work = Path.GetFullPath(work);
        string profile = Environment.GetEnvironmentVariable("FMF_SONIC_TEST_PROFILE") ?? "";
        // The TEST profile in place, or a read-only copy of the main profile's replay files under artifacts.
        bool mainCopy = new DirectoryInfo(profile).Parent?.Name == "NoctisPlus-Main" && Path.GetFullPath(profile).Contains(@"\artifacts\sonic\", StringComparison.OrdinalIgnoreCase);
        if (new DirectoryInfo(profile).Name != "profile" || !(new DirectoryInfo(profile).Parent?.Name == "NoctisPlus-Test" || mainCopy))
            throw new ArgumentException("FMF_SONIC_TEST_PROFILE must be the in-place NoctisPlus-Test/profile or an artifacts copy of the main profile");
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
        // FMF_SONIC_REPLAY_OWNED=1: the library as it was on each day (from each song's date added). Owned songs are then
        // filtered as in the app, and only later plays of songs not owned yet count: For you's real job, finding new music.
        bool ownedMode = Environment.GetEnvironmentVariable("FMF_SONIC_REPLAY_OWNED") == "1";
        var added = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        if (ownedMode)
            foreach (var t in JsonDocument.Parse(File.ReadAllBytes(Path.Combine(profile, "library.json"))).RootElement.EnumerateArray())
                if (t.TryGetProperty("dateAdded", out var d) && d.ValueKind == JsonValueKind.String && DateTime.TryParse(d.GetString(), out var at)
                    && DiscoverIdentity.Key(t.GetProperty("title").GetString(), t.GetProperty("artist").GetString()) is { Length: > 0 } k)
                    added[k] = added.TryGetValue(k, out var was) && was < at ? was : at.ToUniversalTime();
        // FMF_SONIC_REPLAY_TOPPICKS=K: the owner's layout. The normal page stays as it is and a row of K "Top picks" chosen
        // by sound from all the other candidates goes above it. Normal For you then shows K more of its own, so both show as many.
        int picks = int.TryParse(Environment.GetEnvironmentVariable("FMF_SONIC_REPLAY_TOPPICKS"), out var k0) && k0 is > 0 and <= 40 ? k0 : 0;
        if (picks > 0) Console.WriteLine($"top picks layout: {picks} picks + {Discover.Shelf} normal per refresh; normal For you shows {Discover.Shelf + picks}");
        Console.WriteLine(ownedMode ? $"owned mode: {added.Count} library recordings with a date added" : "taste mode: ownership ignored");
        foreach (var day in days)
        {
            var before = events.Where(e => e.at.Date < day).Select(e => new TastePlay(e.artist, e.at, e.label > 0 ? 1.5 : -.7, e.title, Heard: e.label > 0, Sampled: e.label < 0)).ToList();
            if (before.Count(p => p.Weight > 0) < 2) continue;
            var known = before.Where(p => p.Heard).Select(p => DiscoverIdentity.Key(p.Title, p.Artist)).ToHashSet();
            var ownedThen = ownedMode ? library.Where(s => added.TryGetValue(DiscoverIdentity.Key(s.Title, s.Artist), out var a) && a < day).ToList() : [];
            var ownedKeys = ownedThen.Select(s => DiscoverIdentity.Key(s.Title, s.Artist)).ToHashSet(StringComparer.Ordinal);
            var held = events.Where(e => e.at.Date == day && e.label > 0).Select(e => DiscoverIdentity.Key(e.title, e.artist)).Where(k => !known.Contains(k) && !ownedKeys.Contains(k)).ToHashSet();
            if (held.Count == 0) continue;
            foreach (var model in new[] { "baseline" }.Concat(fill ? [] : models.Keys))
            foreach (double blend in model == "baseline" ? new[] { 0d } : new[] { .1, .2, .35 })
            {
                var scorer = new Scorer(model == "baseline" ? [] : models[model], blend, hold: picks > 0);
                int inPicks = 0;
                // Each fold starts with no shelf history; Refresh then behaves as in the app: kept pages and cards
                // shown without being used sink, so later pages reach further into the candidates.
                var history = Path.Combine(work, "replay-refresh", $"{model}-{blend:0.00}-{day:yyyyMMdd}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(history)!);
                File.Delete(history);
                var clock = new DateTimeOffset(day, TimeSpan.Zero);
                var feedback = new DiscoverFeedback(history, () => clock);
                var hidden = new DiscoverHidden(Path.Combine(work, "empty-hidden.json"));
                var kept = new List<List<string>>();
                var found = new int[Refreshes];
                var reached = new HashSet<string>();
                var pooled = new HashSet<string>();
                List<DiscoverSong> page = [];
                List<string> keys = [];
                for (int r = 0; r < Refreshes; r++)
                {
                    var onPages = kept.TakeLast(ForYouSource.PagesKept).SelectMany(k => k).ToHashSet(StringComparer.Ordinal);
                    // No current library/favorites/playlist marks are used: their historical state is unknown.
                    var built = await Discover.ForListenerAsync(before, ownedThen, [], feedback, hidden, provider,
                        (songs, _) => Task.FromResult(songs.Where(s => ownedKeys.Contains(DiscoverIdentity.Key(s))).Select(s => s.Id).ToHashSet()),
                        null, clock, CancellationToken.None, onPages: onPages, sonic: scorer);
                    foreach (var s in scorer.Candidates) { allCandidates[s.Id] = s; if (held.Contains(DiscoverIdentity.Key(s))) pooled.Add(DiscoverIdentity.Key(s)); }
                    var shown = built.Songs.Take(Discover.Shelf + (picks > 0 && model == "baseline" ? picks : 0)).ToList();
                    var cards = shown.Select((s, i) => new DiscoverShown(DiscoverIdentity.Key(s.Track.Song), s.Track.Song.Id,
                        DiscoverIdentity.Artist(s.Track.Song.Artists.FirstOrDefault()?.Name), s.Seed, s.Own, i)).ToList();
                    if (picks > 0 && model != "baseline")
                    {
                        // Best sound scores among the candidates not on this page or a kept one; at most two per artist.
                        var taken = cards.Select(c => c.Key).Concat(onPages).ToHashSet(StringComparer.Ordinal);
                        var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);
                        var row = scorer.Candidates.Where(s => scorer.Last.ContainsKey(DiscoverIdentity.Key(s)) && taken.Add(DiscoverIdentity.Key(s)))
                            .OrderByDescending(s => scorer.Last[DiscoverIdentity.Key(s)])
                            .Where(s => { var a = DiscoverIdentity.Artist(s.Artists.FirstOrDefault()?.Name); perArtist[a] = perArtist.GetValueOrDefault(a) + 1; return perArtist[a] <= 2; })
                            .Take(picks).Select(s => new DiscoverShown(DiscoverIdentity.Key(s), s.Id, DiscoverIdentity.Artist(s.Artists.FirstOrDefault()?.Name), "", false, 0)).ToList();
                        inPicks += row.Count(c => held.Contains(c.Key) && !reached.Contains(c.Key));
                        cards = [.. row, .. cards];
                        cards = cards.Select((c, i) => c with { Position = i }).ToList();
                    }
                    var shownKeys = cards.Select(c => c.Key).ToList();
                    if (r == 0) { page = shown; keys = shownKeys; }
                    reached.UnionWith(shownKeys.Where(held.Contains));
                    found[r] = reached.Count;
                    kept.Add(shownKeys);
                    if (built.Batch is { Length: > 0 } batch)
                    {
                        feedback.Shown(batch, cards);
                        feedback.Seen(batch, Enumerable.Range(0, Math.Min(DiscoverEvaluation.InView, cards.Count)));
                    }
                    clock = clock.AddMinutes(10);
                }
                double ideal = Enumerable.Range(0, Math.Min(20, held.Count)).Sum(i => 1 / Math.Log2(i + 2));
                double dcg = keys.Select((k, i) => held.Contains(k) ? 1 / Math.Log2(i + 2) : 0).Sum();
                int first = keys.FindIndex(held.Contains);
                var metrics = DiscoverPageMetrics.Of(page, new HashSet<string>(), known);
                results.Add(new(model, blend, day.ToString("yyyy-MM-dd"), scorer.Pool, scorer.Covered, held.Count,
                    scorer.Candidates.Count(s => held.Contains(DiscoverIdentity.Key(s))), keys.Take(10).Count(held.Contains), keys.Count(held.Contains),
                    ideal > 0 ? dcg / ideal : 0, first >= 0 ? 1d / (first + 1) : 0, metrics.Artists, metrics.MaxPerArtist, found, pooled.Count, inPicks));
            }
        }
        var report = new { Note = "TEST-only chronological diagnostic. Today's cached metadata, no historical ownership, no historical favorites. Audit recordings excluded upstream. Model/blend tuning must use earlier folds; later folds are holdout. Missing candidates are retrieval failures, not ranking failures.",
            Rows = results, MetadataMisses = refuse.Misses, PoolRecordings = allCandidates.Count };
        File.WriteAllText(Path.Combine(work, "engine-replay.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(work, "pool-songs.json"), JsonSerializer.Serialize(allCandidates.Values));
        foreach (var g in results.GroupBy(r => (r.Model, r.Blend)))
            Console.WriteLine($"{g.Key.Model} blend {g.Key.Blend:0.00}: {g.Count()} folds, hit20 {g.Sum(r => r.Hit20)}/{g.Sum(r => r.EligibleHeldOut)}, ndcg20 {g.Average(r => r.Ndcg20):0.000}, mrr {g.Average(r => r.Mrr):0.000}, coverage {g.Sum(r => r.Covered)}/{g.Sum(r => r.Candidates)}, artists {g.Average(r => r.Artists):0.0}"
                + $"; over refreshes (20,40..{Refreshes * 20} songs): {string.Join(" ", Enumerable.Range(0, Refreshes).Select(i => g.Sum(r => r.FoundByRefresh[i])))}, in any pool {g.Sum(r => r.PooledOverRefreshes)}"
                + (picks > 0 && g.Key.Model != "baseline" ? $", {g.Sum(r => r.FoundInPicks)} first found in the top picks" : ""));
        Console.WriteLine($"{allCandidates.Count} pool recordings; metadata misses {refuse.Misses}; no network requests sent.");
    }
}
