using System.Text.Json;

namespace FreeMusicFinder.Tests;

/// <summary>Owner #172: opt-in, offline replay of For you over the listener's own history, with the SAME page builder as the
/// shelf. For each day, pages are built from the plays before it and refreshed the way the shelf does (kept pages and cards
/// shown without being used sink); it counts which songs the listener went on to play turn up, and after how many songs.
/// Inputs: a manifest of classified listens (artifacts), the profile's library, and a copy of the public catalog cache.
/// Today's catalog is replayed, not a historical one. Never reads keys, settings or Telegram files.</summary>
internal static class ForYouReplayTest
{
    private sealed record Event(string key, string artist, string title, DateTime at, int label);
    private sealed record Input(List<Event> events);
    private sealed class Refusing : HttpMessageHandler
    {
        public int Misses;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Misses); throw new HttpRequestException("Offline For you replay: metadata cache miss"); }
    }
    private sealed record Row(string Day, int HeldOut, int[] FoundBySongs, int[] SongsSeen, int Pooled, double Artists, double Favourite, double Known, double New);

    /// <summary>Refreshes in a row; 10 x 25 = 250 songs (owner: 200+). FMF_FORYOU_REPLAY_REFRESHES overrides.</summary>
    private static readonly int Refreshes = int.TryParse(Environment.GetEnvironmentVariable("FMF_FORYOU_REPLAY_REFRESHES"), out var n) && n is > 0 and <= 50 ? n : 10;

    public static async Task RunAsync(string work)
    {
        work = Path.GetFullPath(work);
        string profile = Path.GetFullPath(Environment.GetEnvironmentVariable("FMF_FORYOU_PROFILE") ?? "");
        // The TEST profile in place, or a read-only copy of the main profile's replay files kept under artifacts.
        var parent = new DirectoryInfo(profile).Parent?.Name;
        bool allowed = new DirectoryInfo(profile).Name == "profile" && (parent == "NoctisPlus-Test"
            || parent == "NoctisPlus-Main" && profile.Contains(@"\artifacts\", StringComparison.OrdinalIgnoreCase));
        if (!allowed) throw new ArgumentException("FMF_FORYOU_PROFILE must be NoctisPlus-Test/profile or an artifacts copy of the main profile");
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(Path.Combine(work, "manifest.json")))!;
        var events = input.events.OrderBy(e => e.at).ToList();
        var days = events.Select(e => e.at.Date).Distinct().Skip(1).ToList();
        var (library, albums) = DiscoverReplay.ReadLibrary(Path.Combine(profile, "library.json"));
        var source = Path.Combine(profile, "plugin-data", "dev.moshi.freemusicfinder", "artist-catalog");
        // A copy of the public cache, so FMF_FORYOU_REPLAY_FILL=1 can add the catalog pages new picks need without
        // writing the profile. Last.fm stays cache-only either way.
        var folder = Path.Combine(work, "replay-catalog");
        if (!Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(folder, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        bool fill = Environment.GetEnvironmentVariable("FMF_FORYOU_REPLAY_FILL") == "1";
        string proxy = Environment.GetEnvironmentVariable("FMF_LIVE_PROXY") ?? "";
        var cacheTime = DateTimeOffset.UtcNow;
        using var refuse = new Refusing();
        using var catalog = fill ? new ArtistCatalog(folder, PlaylistCatalog.MetadataHandler(proxy)) : new ArtistCatalog(folder, refuse, () => cacheTime, TimeSpan.Zero);
        using var similar = fill ? new SimilarArtists(folder, PlaylistCatalog.MetadataHandler(proxy)) : new SimilarArtists(folder, new Refusing(), () => cacheTime, TimeSpan.Zero);
        // Fixture token: only the saved PUBLIC relations are read; the refusing handler means nothing leaves this process.
        using var lastFm = File.Exists(Path.Combine(folder, "lastfm.json"))
            ? new LastFmSimilar(folder, new Refusing(), () => new string('0', 32), () => cacheTime, TimeSpan.Zero) : null;
        var provider = new CatalogForDiscover(() => catalog, name => albums.GetValueOrDefault(BotText.Key(Taste.MainArtist(name))) ?? [], () => similar, () => lastFm);
        // FMF_FORYOU_REPLAY_OWNED=1: the library as it was on each day (each song's date added); owned songs are left out as in
        // the app, and only later plays of songs not owned yet count. Without it, ownership is ignored (any later first play).
        bool ownedMode = Environment.GetEnvironmentVariable("FMF_FORYOU_REPLAY_OWNED") == "1";
        var added = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var t in JsonDocument.Parse(File.ReadAllBytes(Path.Combine(profile, "library.json"))).RootElement.EnumerateArray())
            if (t.TryGetProperty("dateAdded", out var d) && d.ValueKind == JsonValueKind.String && DateTime.TryParse(d.GetString(), out var at)
                && DiscoverIdentity.Key(t.GetProperty("title").GetString(), t.GetProperty("artist").GetString()) is { Length: > 0 } k)
                added[k] = added.TryGetValue(k, out var was) && was < at.ToUniversalTime() ? was : at.ToUniversalTime();
        Console.WriteLine($"{(ownedMode ? "discovery scoring (songs not owned yet)" : "taste scoring (any later first play)")}; {Refreshes} refreshes of {Discover.Shelf}");
        var results = new List<Row>();
        foreach (var day in days)
        {
            var before = events.Where(e => e.at.Date < day).Select(e => new TastePlay(e.artist, e.at, e.label > 0 ? 1.5 : -.7, e.title, Heard: e.label > 0, Sampled: e.label < 0)).ToList();
            if (before.Count(p => p.Weight > 0) < 2) continue;
            var known = before.Where(p => p.Heard).Select(p => DiscoverIdentity.Key(p.Title, p.Artist)).ToHashSet();
            var ownedThen = ownedMode ? library.Where(s => added.TryGetValue(DiscoverIdentity.Key(s.Title, s.Artist), out var a) && a < day).ToList() : [];
            var ownedKeys = ownedThen.Select(s => DiscoverIdentity.Key(s.Title, s.Artist)).ToHashSet(StringComparer.Ordinal);
            var held = events.Where(e => e.at.Date == day && e.label > 0).Select(e => DiscoverIdentity.Key(e.title, e.artist)).Where(k => !known.Contains(k) && !ownedKeys.Contains(k)).ToHashSet();
            if (held.Count == 0) continue;
            var history = Path.Combine(work, "replay-refresh", $"{day:yyyyMMdd}{(ownedMode ? "-owned" : "")}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(history)!);
            File.Delete(history);
            var clock = new DateTimeOffset(day, TimeSpan.Zero);
            var feedback = new DiscoverFeedback(history, () => clock);
            var hidden = new DiscoverHidden(Path.Combine(work, "empty-hidden.json"));
            var kept = new List<List<string>>();
            var found = new int[Refreshes];
            var seen = new int[Refreshes];
            var reached = new HashSet<string>();
            var shownArtists = new List<double>();
            var mix = new List<(double F, double K, double N)>();
            var playedArtists = before.Select(p => DiscoverIdentity.Artist(p.Artist)).Concat(ownedThen.Select(s => DiscoverIdentity.Artist(s.Artist))).ToHashSet(StringComparer.Ordinal);
            for (int r = 0; r < Refreshes; r++)
            {
                var onPages = kept.TakeLast(ForYouSource.PagesKept).SelectMany(k => k).ToHashSet(StringComparer.Ordinal);
                // No current favorites/playlist marks: their historical state is unknown.
                var built = await Discover.ForListenerAsync(before, ownedThen, [], feedback, hidden, provider,
                    (songs, _) => Task.FromResult(songs.Where(s => ownedKeys.Contains(DiscoverIdentity.Key(s))).Select(s => s.Id).ToHashSet()),
                    null, clock, CancellationToken.None, onPages: onPages);
                var shown = built.Songs.Take(Discover.Shelf).ToList();
                var keys = shown.Select(s => DiscoverIdentity.Key(s.Track.Song)).ToList();
                foreach (var (s, i) in shown.Select((s, i) => (s, i)).Where(x => held.Contains(keys[x.i]) && !reached.Contains(keys[x.i])))
                    Console.WriteLine($"  found {day:MM-dd} refresh {r + 1} place {i + 1}: {(s.Own ? "own artist" : playedArtists.Contains(DiscoverIdentity.Artist(s.Track.Song.Artists.FirstOrDefault()?.Name)) ? "artist you play" : "new artist")}, {s.Because}");
                reached.UnionWith(keys.Where(held.Contains));
                found[r] = reached.Count;
                seen[r] = (r > 0 ? seen[r - 1] : 0) + shown.Count;
                kept.Add(keys);
                if (shown.Count > 0)
                {
                    var artists = shown.Select(s => DiscoverIdentity.Artist(s.Track.Song.Artists.FirstOrDefault()?.Name)).ToList();
                    shownArtists.Add(artists.Distinct().Count());
                    mix.Add((shown.Count(s => s.Own) / (double)shown.Count, artists.Zip(shown).Count(x => !x.Second.Own && playedArtists.Contains(x.First)) / (double)shown.Count,
                        artists.Count(a => !playedArtists.Contains(a)) / (double)shown.Count));
                }
                if (built.Batch is { Length: > 0 } batch)
                {
                    feedback.Shown(batch, shown.Select((s, i) => new DiscoverShown(keys[i], s.Track.Song.Id,
                        DiscoverIdentity.Artist(s.Track.Song.Artists.FirstOrDefault()?.Name), s.Seed, s.Own, i)).ToList());
                    feedback.Seen(batch, Enumerable.Range(0, Math.Min(DiscoverEvaluation.InView, shown.Count)));
                }
                clock = clock.AddMinutes(10);
            }
            // FMF_FORYOU_REPLAY_MISSES=1: for each song never shown, what kind of artist it is and where it stands in that artist's
            // catalog list (cache only): a song deep in a known artist's list needs depth, an unknown artist needs reach.
            if (Environment.GetEnvironmentVariable("FMF_FORYOU_REPLAY_MISSES") == "1")
                foreach (var e in events.Where(e => e.at.Date == day && held.Contains(DiscoverIdentity.Key(e.title, e.artist)) && !reached.Contains(DiscoverIdentity.Key(e.title, e.artist))).DistinctBy(e => e.key))
                {
                    var a = DiscoverIdentity.Artist(e.artist);
                    string kind = playedArtists.Contains(a) ? "artist you play" : "new artist";
                    string place;
                    try
                    {
                        var list = await catalog.SongsByArtistNameAsync(Taste.MainArtist(e.artist), CancellationToken.None);
                        int i = list.Value.Songs.FindIndex(s => DiscoverIdentity.Key(s.Song) == DiscoverIdentity.Key(e.title, e.artist));
                        place = i >= 0 ? $"#{i + 1} of {list.Value.Songs.Count} in the artist's list" : $"not in the artist's top {list.Value.Songs.Count}";
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { place = "artist not cached"; }
                    Console.WriteLine($"  missed {day:MM-dd}: {kind}, {place}");
                }
            results.Add(new(day.ToString("yyyy-MM-dd"), held.Count, found, seen, 0, shownArtists.DefaultIfEmpty().Average(),
                mix.Select(m => m.F).DefaultIfEmpty().Average(), mix.Select(m => m.K).DefaultIfEmpty().Average(), mix.Select(m => m.N).DefaultIfEmpty().Average()));
        }
        File.WriteAllText(Path.Combine(work, ownedMode ? "foryou-replay-owned.json" : "foryou-replay-taste.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        if (results.Count == 0) { Console.WriteLine("no day with held-out plays"); return; }
        int held2 = results.Sum(r => r.HeldOut);
        Console.WriteLine($"{results.Count} days, {held2} held out; found after "
            + string.Join(", ", Enumerable.Range(0, Refreshes).Select(i => $"{results[0].SongsSeen[i]}: {results.Sum(r => r.FoundBySongs[i])}")));
        Console.WriteLine($"per page: {results.Average(r => r.Artists):0.0} artists; your own artists {results.Average(r => r.Favourite):P0}, other artists you play {results.Average(r => r.Known):P0}, new to you {results.Average(r => r.New):P0}");
        // Earlier and later days apart: shares are set on the earlier half and only checked on the later.
        int half = results.Count / 2;
        foreach (var (name, part) in new[] { ("earlier days", results.Take(half).ToList()), ("later days", results.Skip(half).ToList()) })
            if (part.Count > 0) Console.WriteLine($"  {name}: {part.Sum(r => r.FoundBySongs[^1])}/{part.Sum(r => r.HeldOut)} found by {part[0].SongsSeen[^1]} songs");
        Console.WriteLine($"metadata misses {refuse.Misses}{(fill ? " (fill mode: misses fetched)" : "; no network requests sent")}.");
    }
}
