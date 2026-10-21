using System.Text.Json;
using FreeMusicFinder;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Milestone C: the 2.2.0 engine (<see cref="DiscoverBaseline"/>) against the current one on synthetic listeners and a
/// synthetic catalog, scored by the metrics and promotion criteria in docs/fork/discover-plan.md section 5, which were written
/// before this ran. The world is mine, so it can favour the new engine; the owner's replay on real data is the check on that.
/// Set DISCOVER_EVAL_REPORT to a file path to get the table.
/// </summary>
public class DiscoverEvaluationTests(ITestOutputHelper output) : IDisposable
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "noctis-discover-c-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    /// <summary>
    /// Four genres of twelve artists and three megastars on every similar-artist list. Each artist's catalog list is
    /// popularity ordered (the first song is the hit) and carries the traps real lists have: song 1 again as a remaster on
    /// another provider, a compilation song that does not credit the artist, and a live recording of song 2.
    /// </summary>
    internal sealed class World : IDiscoverCatalog
    {
        public static readonly string[] Genres = ["Ambient", "Rock", "Hip", "Folk"];
        private readonly Dictionary<string, CatalogArtist> _artists = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CatalogSearchSong>> _songs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SimilarArtist>> _similar = new(StringComparer.Ordinal);
        public Func<string, CancellationToken, Task>? Delay { get; init; }
        public int Calls;

        public World()
        {
            var megas = Enumerable.Range(0, 3).Select(i => "Mega " + i).ToList();
            foreach (var name in megas) Artist(name, 990_000);
            for (int g = 0; g < Genres.Length; g++)
                for (int i = 0; i < 12; i++) Artist($"{Genres[g]} {i}", 800_000 - i * 40_000);
            for (int g = 0; g < Genres.Length; g++)
                for (int i = 0; i < 12; i++)
                {
                    var list = Enumerable.Range(0, 12).Where(j => j != i).OrderBy(j => Math.Abs(j - i)).ThenBy(j => j).Select(j => $"{Genres[g]} {j}").ToList();
                    list.Insert(2, megas[0]); list.Insert(5, megas[1]); list.Insert(8, megas[2]);
                    list.Add($"{Genres[(g + 1) % Genres.Length]} {i}");
                    _similar[$"{Genres[g]} {i}"] = list.Select((n, k) => new SimilarArtist(n, 1 - k * 0.05)).ToList();
                }
        }

        private void Artist(string name, int reach)
        {
            var artist = new CatalogArtist("deezer:" + name.Replace(' ', '-'), name);
            var copy = new CatalogArtist("apple:" + name.Replace(' ', '-'), name);
            _artists[name] = artist;
            var songs = new List<CatalogSong>();
            for (int n = 1; n <= 15; n++) songs.Add(new($"deezer:{name}:{n}", $"{name} Song {n}", [artist], 180 + n, true, Popularity: Math.Max(1_000, reach - (n - 1) * 45_000)));
            songs.Insert(1, new($"apple:{name}:1", $"{name} Song 1 (Remastered 2011)", [copy], 182, true, Popularity: 0));
            songs.Insert(5, new($"deezer:{name}:comp", $"{name} Song 4", [new("deezer:va", "Various Artists")], 185, true, Popularity: reach / 2));
            songs.Insert(7, new($"deezer:{name}:live", $"{name} Song 2 (Live)", [artist], 240, true, Popularity: reach / 3));
            _songs[name] = songs.Select(s => new CatalogSearchSong(s, null)).ToList();
        }

        private async Task Wait(string what, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Delay is { } delay) await delay(what, ct);
        }
        public async Task<CatalogArtist?> FindAsync(TasteSeed seed, CancellationToken ct) { await Wait("find:" + seed.Name, ct); return _artists.GetValueOrDefault(seed.Name); }
        public async Task<CatalogArtist?> FindByNameAsync(string name, CancellationToken ct) { await Wait("find:" + name, ct); return _artists.GetValueOrDefault(name); }
        public async Task<List<SimilarArtist>> SimilarAsync(TasteSeed seed, CatalogArtist? artist, CancellationToken ct) { await Wait("similar:" + seed.Name, ct); return [.. _similar.GetValueOrDefault(seed.Name) ?? []]; }
        public async Task<List<CatalogSearchSong>> TopSongsAsync(CatalogArtist artist, CancellationToken ct) { await Wait("top:" + artist.Name, ct); return [.. _songs.GetValueOrDefault(artist.Name) ?? []]; }
    }

    internal sealed record Persona(string Name, string Folder, List<LocalSong> Library);

    internal static string Listen(string artist, string title, DateTimeOffset at, string end, double coverage, int ms) => JsonSerializer.Serialize(new
    {
        trackId = Guid.NewGuid().ToString(), title, artist, startUtc = at.UtcDateTime.ToString("O"), end, coverage, listenedMs = ms,
    });
    private static LocalSong Owned(string artist, int n, bool favorite = false, int rating = 0) =>
        new(Guid.NewGuid(), artist, $"{artist} Song {n}", $"C:/Music/{artist}/{n}.flac", TimeSpan.FromSeconds(180 + n), Favorite: favorite, Rating: rating);

    /// <summary>Writes a listener's files the way Noctis and True Shuffle keep them.</summary>
    internal static Persona Make(string root, string name)
    {
        var folder = Path.Combine(root, name);
        var shuffle = Path.Combine(folder, "plugin-data", "dev.moshi.trueshuffle");
        Directory.CreateDirectory(shuffle);
        var lines = new List<string>();
        var library = new List<LocalSong>();
        var playlists = new List<object>();
        void Finish(string artist, int song, DateTimeOffset at) => lines.Add(Listen(artist, $"{artist} Song {song}", at, "Finished", 1, 200_000));
        switch (name)
        {
            case "steady": // years of two rock bands, a three-week hip-hop phase, rock hits already streamed but not owned
                for (int d = 400; d > 0; d -= 3) { Finish("Rock 0", 1 + d % 6, Now.AddDays(-d)); Finish("Rock 1", 1 + d % 5, Now.AddDays(-d).AddHours(1)); }
                for (int d = 21; d > 0; d--) for (int a = 0; a < 6; a++) Finish($"Hip {a}", 1 + d % 4, Now.AddDays(-d).AddMinutes(a * 4));
                for (int a = 2; a <= 5; a++) for (int n = 1; n <= 3; n++) Finish($"Rock {a}", n, Now.AddDays(-90 - a * 3 - n));
                foreach (var a in new[] { "Rock 0", "Rock 1" }) for (int n = 1; n <= 5; n++) library.Add(Owned(a, n, favorite: n == 1));
                break;
            case "streamer": // no library; folk finishes written twice, failed starts and empty "Finished" streams
                for (int d = 60; d > 0; d--)
                {
                    var at = Now.AddDays(-d);
                    var line = Listen($"Folk {d % 4}", $"Folk {d % 4} Song {1 + d % 3}", at, "Finished", 1, 210_000);
                    lines.Add(line); lines.Add(line);
                }
                for (int a = 1; a <= 6; a++) for (int n = 1; n <= 4; n++) Finish($"Folk {a}", n, Now.AddDays(-30 - a - n));
                for (int i = 0; i < 10; i++) lines.Add(Listen("Folk 4", $"Folk 4 Song {1 + i % 5}", Now.AddDays(-2).AddMinutes(i), "Skipped", 0, 800));
                for (int i = 0; i < 8; i++) lines.Add(Listen("Ambient 0", $"Ambient 0 Song {1 + i % 3}", Now.AddDays(-1).AddMinutes(i), "Finished", 0, 1_000));
                break;
            case "collector": // owns whole catalogs, rates them, a big Spotify import and one playlist of their own; few plays
                for (int a = 0; a < 6; a++) for (int n = 1; n <= 15; n++) library.Add(Owned($"Ambient {a}", n, favorite: n <= 2 && a < 2, rating: a == 0 ? 5 : 0));
                for (int d = 10; d > 0; d -= 2) Finish("Ambient 0", 1 + d % 5, Now.AddDays(-d));
                playlists.Add(new { Name = "Spotify likes", Description = "Imported from Spotify", TrackIds = library.Select(s => s.Id).ToList() });
                playlists.Add(new { Name = "Mine", Description = "", TrackIds = library.Where(s => s.Artist == "Ambient 3").Select(s => s.Id).Take(4).ToList() });
                break;
            case "tiny": // one owned song, played once
                library.Add(Owned("Folk 7", 1));
                Finish("Folk 7", 1, Now.AddDays(-3));
                break;
        }
        File.WriteAllLines(Path.Combine(shuffle, "listens.jsonl"), lines);
        File.WriteAllText(Path.Combine(folder, "playlists.json"), JsonSerializer.Serialize(playlists));
        return new(name, folder, library);
    }

    internal static Task<List<DiscoverRun>> Compare(Persona persona, string work, Func<IDiscoverCatalog>? catalog = null)
    {
        var world = catalog ?? (() => new World());
        return DiscoverEvaluation.RunAsync(TasteSources.Load(persona.Folder), DiscoverBaseline.Plays(persona.Folder), persona.Library,
            TasteSources.Playlists(persona.Folder), world, work, Now);
    }

    /// <summary>
    /// The hard criteria (P1, P2) fail the test. The others are written to the report as PASS or FAIL against the thresholds set
    /// beforehand; the test then only guards that the current engine stays no worse than the baseline on them, so a failed
    /// threshold is reported, not hidden by retuning (S3).
    /// </summary>
    [Fact]
    public async Task NewEngine_AgainstThe220Baseline_OnThePromotionCriteria()
    {
        var report = new List<string>();
        var broken = new List<string>();
        foreach (var name in new[] { "steady", "streamer", "collector", "tiny" })
        {
            var persona = Make(_folder, name);
            var runs = await Compare(persona, Path.Combine(_folder, name + "-work"));
            var (before, now) = (runs[0], runs[1]);
            report.Add($"[{name}]"); report.AddRange(runs.Select(r => "  " + r.ToString().Replace("\n", "\n  ")));
            void Verdict(string id, bool pass, string detail) => report.Add($"  {id} {(pass ? "PASS" : "FAIL")}: {detail}");
            bool clean = now.Pages.All(p => p.Duplicates == 0 && p.Owned == 0 && p.Heard == 0);
            Verdict("P1", clean, "no duplicate, owned or heard cards on any page");
            Verdict("P2", now.DismissedReturned == 0, $"{now.DismissedReturned} dismissed songs back");
            if (!clean || now.DismissedReturned != 0) broken.Add($"{name}: P1/P2");
            double limit = Math.Max(0.25, before.Pages[1].Repeat / 2);
            Verdict("P3", now.Pages[1].Repeat <= limit, $"second-page repeat {now.Pages[1].Repeat:P0}, limit {limit:P0} (baseline {before.Pages[1].Repeat:P0})");
            if (now.Pages[1].Repeat > before.Pages[1].Repeat) broken.Add($"{name}: repeats more than the baseline");
            // "When the pool allows it": enough seeds on the page for 20 cards at 6 each.
            bool full = now.Pages[0].Cards >= Discover.Shelf && now.Pages.All(p => p.Seeds * Discover.PerSeed >= p.Cards);
            bool spread = now.Pages.All(p => p.MaxPerArtist <= 3 && p.MaxPerSeed <= Discover.PerSeed) && now.Pages[0].Artists >= before.Pages[0].Artists;
            Verdict("P4", !full || spread, full ? $"max {now.Pages.Max(p => p.MaxPerArtist)} per artist, {now.Pages.Max(p => p.MaxPerSeed)} per seed, "
                + $"{now.Pages[0].Artists} artists vs {before.Pages[0].Artists}" : "too few seeds for the caps (filled anyway)");
            if (full && !spread) broken.Add($"{name}: P4");
            var (b, n) = (DiscoverPageMetrics.Mean(before.Pages), DiscoverPageMetrics.Mean(now.Pages));
            Verdict("P5", n.Top3 <= b.Top3 + 1e-9, $"top-3 share {n.Top3:P0} vs {b.Top3:P0} (uncredited cards {n.Uncredited:P0} vs {b.Uncredited:P0}); "
                + $"S2 threshold 50 %: {(n.Top3 > 0.5 ? "over" : "under")}");
            // P5 is measured, not guarded: retrieval is unchanged in C. Uncredited cards are a regression guard of their own.
            if (n.Uncredited > 0) broken.Add($"{name}: uncredited cards");
        }
        output.WriteLine(string.Join("\n", report));
        if (Environment.GetEnvironmentVariable("DISCOVER_EVAL_REPORT") is { Length: > 0 } path) File.WriteAllLines(path, report);
        Assert.Empty(broken);
    }

    [Fact]
    public void SeedReplay_MeetsP6_OnTheSyntheticPersonas()
    {
        var report = new List<string>();
        foreach (var name in new[] { "steady", "streamer", "collector" })
        {
            var persona = Make(_folder, name);
            var scores = DiscoverReplay.Run(TasteSources.Load(persona.Folder), persona.Library, TasteSources.Playlists(persona.Folder));
            report.Add($"[{name}] " + string.Join(" | ", scores));
            if (scores[0].Cutoffs == 0) continue; // too little history to replay
            Assert.True(scores[1].Recall >= scores[0].Recall - 0.02, $"P6 recall {name}");
            Assert.True(scores[1].SkipShare <= scores[0].SkipShare + 0.02, $"P6 skips {name}");
            Assert.True(scores[1].ReturnRecall >= scores[0].ReturnRecall, $"P6 returning {name}");
        }
        output.WriteLine(string.Join("\n", report));
        if (Environment.GetEnvironmentVariable("DISCOVER_EVAL_REPORT") is { Length: > 0 } path) File.AppendAllLines(path, report);
    }

    /// <summary>
    /// M10 / P7: the local part of a build at the edge of what For you keeps (a 50,000-song library, 100,000 plays, a full
    /// feedback file, 5,000 dismissed songs), against an in-memory catalog. The assertion is loose so a slow CI runner does not
    /// fail it; the report has the numbers checked against P7.
    /// </summary>
    [Fact]
    public async Task Cost_OfALargeProfile_IsBounded()
    {
        var folder = Path.Combine(_folder, "large");
        var shuffle = Path.Combine(folder, "plugin-data", "dev.moshi.trueshuffle");
        Directory.CreateDirectory(shuffle);
        var artists = World.Genres.SelectMany(g => Enumerable.Range(0, 12).Select(i => $"{g} {i}")).ToList();
        var random = new Random(7);
        File.WriteAllLines(Path.Combine(shuffle, "listens.jsonl"), Enumerable.Range(0, 100_000).Select(i =>
        {
            var artist = i % 3 == 0 ? artists[random.Next(artists.Count)] : $"Library {random.Next(2_000)}";
            var heard = random.Next(4) switch { 0 => 1_000, 1 => 20_000, _ => 200_000 };
            return Listen(artist, $"{artist} Track {random.Next(40)}", Now.AddMinutes(-i * 5), heard > 100_000 ? "Finished" : "Skipped", heard / 210_000.0, heard);
        }));
        var library = Enumerable.Range(0, 50_000).Select(i => new LocalSong(Guid.NewGuid(), $"Library {i % 2_000}", $"Library {i % 2_000} Track {i / 2_000}",
            $"C:/Music/{i}.flac", TimeSpan.FromMinutes(3), Favorite: i % 97 == 0, Rating: i % 13 == 0 ? 5 : 0)).ToList();
        File.WriteAllText(Path.Combine(folder, "playlists.json"), JsonSerializer.Serialize(new object[]
        {
            new { Name = "Import", Description = "Imported from Spotify", TrackIds = library.Select(s => s.Id).ToList() },
            new { Name = "Mine", Description = "", TrackIds = library.Take(300).Select(s => s.Id).ToList() },
        }));
        // The feedback file at its bounds (120 pages of 40 cards, 3,000 actions), written directly as the shelf writes it.
        var clock = Now;
        var batches = Enumerable.Range(0, DiscoverFeedback.MaxBatches).Select(b => new DiscoverBatch("b" + b, Now.AddDays(-100 + b * 0.8),
            Enumerable.Range(0, 40).Select(i => new DiscoverShown(DiscoverIdentity.Key($"Seen {b} {i}", artists[i % artists.Count]), "x", DiscoverIdentity.Artist(artists[i % artists.Count]), "", false, i)).ToList(),
            Enumerable.Range(0, 12).ToList())).ToList();
        var actions = batches.SelectMany(b => b.Shown.Take(25).Select(c => new DiscoverInteraction(b.Id, c.Key, c.Artist, "t", (DiscoverAction)(c.Position % 5), b.At.AddMinutes(1)))).ToList();
        File.WriteAllText(Path.Combine(folder, "for-you-feedback.json"), JsonSerializer.Serialize(new { Version = 1, Batches = batches, Interactions = actions }));
        var feedback = new DiscoverFeedback(Path.Combine(folder, "for-you-feedback.json"), () => clock);
        Assert.Equal((DiscoverFeedback.MaxBatches, DiscoverFeedback.MaxInteractions), (feedback.Batches.Count, feedback.Interactions.Count));
        // The dismissed songs file at its bound, written directly (5,000 Hide calls would only time the test's own setup).
        var ids = Enumerable.Range(0, DiscoverHidden.MaxSongs).Select(i => "deezer:h" + i).ToList();
        File.WriteAllText(Path.Combine(folder, "for-you-hidden.json"), JsonSerializer.Serialize(new
        {
            Songs = ids, Version = 3,
            Recordings = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => $"hidden {x.i}|{BotText.Key(artists[x.i % artists.Count])}"),
            Identities = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => new { Key = DiscoverIdentity.Key($"Hidden {x.i}", artists[x.i % artists.Count]), Seconds = 200.0 }),
        }));
        var hidden = new DiscoverHidden(Path.Combine(folder, "for-you-hidden.json"));
        Assert.Equal(DiscoverHidden.MaxSongs, hidden.Count);

        long allocated = GC.GetTotalAllocatedBytes(true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var plays = TasteSources.Load(folder);
        var playlists = TasteSources.Playlists(folder);
        var read = watch.Elapsed;
        var world = new World();
        var page = await Discover.ForListenerAsync(plays, library, playlists, new DiscoverFeedback(Path.Combine(folder, "for-you-feedback.json"), () => clock), hidden, world,
            (_, _) => Task.FromResult(new HashSet<string>()), null, Now, CancellationToken.None);
        var total = watch.Elapsed;
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        // Ranking alone, with taste and learning already worked out.
        watch.Restart();
        var learning = feedback.Learn(plays, library, playlists);
        var learned = watch.Elapsed; watch.Restart();
        var seeds = Taste.Seeds(plays.Concat(learning.Saves), library, Now.UtcDateTime, playlists: playlists);
        var tasted = watch.Elapsed; watch.Restart();
        var familiar = TasteSources.Familiar(plays);
        var known = watch.Elapsed; watch.Restart();
        await Discover.BuildAsync(seeds, world, (_, _) => Task.FromResult(new HashSet<string>()), hidden, null, CancellationToken.None, context: new(familiar, learning, Now));
        var ranking = watch.Elapsed;
        var line = $"[cost] {plays.Count} plays, {library.Count} songs, {DiscoverFeedback.MaxBatches} pages, {hidden.Count} dismissed: reading files {read.TotalMilliseconds:0} ms, "
            + $"whole local build {total.TotalMilliseconds:0} ms ({allocated / 1024.0 / 1024:0} MB allocated), ranking alone {ranking.TotalMilliseconds:0} ms "
            + $"(learning {learned.TotalMilliseconds:0} ms, taste {tasted.TotalMilliseconds:0} ms, heard set {known.TotalMilliseconds:0} ms); {page.Songs.Count} songs";
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("DISCOVER_EVAL_REPORT") is { Length: > 0 } path) File.AppendAllLines(path, [line]);
        Assert.Equal(40, page.Songs.Count);
        Assert.True(total < TimeSpan.FromSeconds(20) && ranking < TimeSpan.FromSeconds(5), line);
    }
}
