using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>
/// For you as it was in 2.2.0 (11995e63), kept runnable so the current engine can be compared with it on the same data and
/// catalog (Milestone C). Frozen: fix nothing here, or the comparison stops meaning anything. Only the evaluation and the
/// local replay call it; the shelf never does.
/// </summary>
internal static class DiscoverBaseline
{
    /// <summary>2.2.0's reading of the plays: listens.jsonl with its old weights (a quick Next −0.3, any "Finished" +1.5,
    /// duplicates counted twice), and play_history.json before it.</summary>
    public static List<TastePlay> Plays(string? noctisData)
    {
        if (noctisData is null) return [];
        var listens = ShuffleListens(Path.Combine(noctisData, "plugin-data", "dev.moshi.trueshuffle", "listens.jsonl"));
        var since = listens.Count > 0 ? listens.Min(p => p.At) : DateTime.MaxValue;
        return TasteSources.NoctisHistory(Path.Combine(noctisData, "play_history.json"), since).Concat(listens).ToList();
    }

    /// <summary>2.2.0's weight for one listens.jsonl row.</summary>
    public static double Weight(string end, double coverage, double heard) => end == "Finished" ? 1.5
        : coverage >= 0.5 || heard >= 240_000 ? 1.0
        : end == "Skipped" ? (heard < 3_000 ? -0.3 : coverage < 0.25 ? -1.5 : 0.3)
        : heard >= 30_000 ? 0.3 : 0;

    private static List<TastePlay> ShuffleListens(string path)
    {
        var result = new List<TastePlay>();
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length >= 256L * 1024 * 1024) return result;
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var e = doc.RootElement;
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    string S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    double N(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                    var artist = S("artist");
                    if (artist.Length == 0 || !e.TryGetProperty("startUtc", out var start) || start.ValueKind != JsonValueKind.String || !start.TryGetDateTime(out var at)) continue;
                    at = at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : DateTime.SpecifyKind(at, DateTimeKind.Utc);
                    var w = Weight(S("end"), N("coverage"), N("listenedMs"));
                    if (w != 0) result.Add(new(artist, at, w, S("title")));
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>2.2.0's seed step: one 30-day horizon, every replay counted in full, plus <c>0.4·ln(1+owned)</c>.</summary>
    public static List<TasteSeed> Seeds(IEnumerable<TastePlay> plays, IReadOnlyList<LocalSong> library, DateTime nowUtc, int count = Discover.SeedArtistsUsed)
    {
        var weight = new Dictionary<string, double>(StringComparer.Ordinal);
        var names = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var titles = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        void Title(string key, string title, double w)
        {
            if (title.Trim().Length == 0) return;
            if (!titles.TryGetValue(key, out var t)) titles[key] = t = new(StringComparer.Ordinal);
            t[title] = t.GetValueOrDefault(title) + w;
        }
        void Name(string display)
        {
            var key = BotText.Key(display);
            if (key.Length == 0) return;
            if (!names.TryGetValue(key, out var spellings)) names[key] = spellings = new(StringComparer.Ordinal);
            spellings[display] = spellings.GetValueOrDefault(display) + 1;
        }
        foreach (var play in plays)
        {
            var main = Taste.MainArtist(play.Artist);
            var key = BotText.Key(main);
            if (key.Length == 0) continue;
            var age = nowUtc - play.At;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            weight[key] = weight.GetValueOrDefault(key) + play.Weight * Math.Pow(0.5, age / TimeSpan.FromDays(30));
            Name(main);
            if (play.Weight > 0) Title(key, play.Title, play.Weight);
        }
        var owned = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var song in library)
        {
            var main = Taste.MainArtist(song.Artist.Length > 0 ? song.Artist : song.AlbumArtist);
            var key = BotText.Key(main);
            if (key.Length == 0) continue;
            owned[key] = owned.GetValueOrDefault(key) + 1;
            Name(main);
            Title(key, song.Title, 0.01);
        }
        foreach (var (key, n) in owned) weight[key] = weight.GetValueOrDefault(key) + 0.4 * Math.Log(1 + n);
        return weight.Where(w => w.Value > 0.2).OrderByDescending(w => w.Value).Take(count)
            .Select(w => new TasteSeed(names[w.Key].OrderByDescending(n => n.Value).First().Key, Math.Round(w.Value, 3), owned.GetValueOrDefault(w.Key),
                titles.TryGetValue(w.Key, out var t) ? t.OrderByDescending(x => x.Value).Select(x => x.Key).Take(3).ToList() : []))
            .ToList();
    }

    /// <summary>2.2.0's page for a listener: its seeds and its ranker. No familiarity, exposure or outcome memory.</summary>
    public static Task<DiscoverResult> ForListenerAsync(IReadOnlyList<TastePlay> plays, IReadOnlyList<LocalSong> library, DiscoverHidden hidden, IDiscoverCatalog catalog,
        Func<IReadOnlyList<CatalogSong>, CancellationToken, Task<HashSet<string>>> unavailable, DateTimeOffset now, CancellationToken ct) =>
        BuildAsync(Seeds(plays, library, now.UtcDateTime), catalog, unavailable, hidden, null, ct);

    /// <summary>2.2.0's ranker, unchanged but for dismissals read through <see cref="DiscoverHidden.HidesEdition"/> (2.2.0's rule).</summary>
    public static async Task<DiscoverResult> BuildAsync(IReadOnlyList<TasteSeed> seeds, IDiscoverCatalog catalog,
        Func<IReadOnlyList<CatalogSong>, CancellationToken, Task<HashSet<string>>> unavailable, DiscoverHidden hidden,
        IProgress<string>? progress, CancellationToken ct, int songCount = 40)
    {
        if (seeds.Count == 0) return new([], [], "Play some music first: For you learns from what you listen to and what you own.");
        var score = new Dictionary<string, double>(StringComparer.Ordinal);
        var because = new Dictionary<string, (string Seed, double Weight)>(StringComparer.Ordinal);
        var display = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolved = new Dictionary<string, CatalogArtist>(StringComparer.Ordinal);
        var own = new HashSet<string>(StringComparer.Ordinal);
        int found = 0, failed = 0;
        string lastError = "";
        void Add(string name, double value, string seed)
        {
            var key = BotText.Key(name);
            if (key.Length == 0) return;
            display.TryAdd(key, name);
            score[key] = score.GetValueOrDefault(key) + value;
            if (!because.TryGetValue(key, out var best) || value > best.Weight) because[key] = (seed, value);
        }
        var used = seeds.Take(Discover.SeedArtistsUsed).ToList();
        foreach (var seed in used) own.Add(BotText.Key(seed.Name));
        var lists = new List<(TasteSeed Seed, List<SimilarArtist> Similar)>();
        int complete = 0;
        using var concurrent = new SemaphoreSlim(Discover.ArtistLookups, Discover.ArtistLookups);
        var relations = await Task.WhenAll(used.Select(async seed =>
        {
            await concurrent.WaitAsync(ct);
            try
            {
                return (Seed: seed, Similar: LastFmSimilar.Filter(await catalog.SimilarAsync(seed, null, ct), seed.Name, used.Select(s => s.Name)).Take(40).ToList(), Error: "");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return (Seed: seed, Similar: new List<SimilarArtist>(), Error: ArtistCatalog.Explain(ex)); }
            finally { progress?.Report($"Finding artists like the ones you play… {Interlocked.Increment(ref complete)} of {used.Count}"); concurrent.Release(); }
        }));
        foreach (var row in relations)
        {
            if (row.Error.Length > 0) { failed++; lastError = row.Error; }
            lists.Add((row.Seed, row.Similar));
            Add(row.Seed.Name, Math.Sqrt(Math.Max(0, row.Seed.Weight)) * 0.6, row.Seed.Name);
        }
        var onLists = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, similar) in lists)
            foreach (var key in similar.Select(a => BotText.Key(a.Name)).Distinct()) onLists[key] = onLists.GetValueOrDefault(key) + 1;
        foreach (var (seed, similar) in lists)
        {
            double voice = Math.Sqrt(Math.Max(0, seed.Weight));
            for (int j = 0; j < similar.Count; j++)
            {
                var other = similar[j];
                double common = onLists.GetValueOrDefault(BotText.Key(other.Name), 1);
                Add(other.Name, voice * (0.4 + 0.6 * other.Score) * (1 - j / 60.0) / (1 + 0.6 * (common - 1)), seed.Name);
            }
        }
        var ranked = score.OrderByDescending(s => s.Value).Take(Discover.CandidateArtists).ToList();
        var songs = new List<(CatalogSearchSong Track, double Score, string Key)>();
        int picked = 0;
        using var lookups = new SemaphoreSlim(Discover.ArtistLookups, Discover.ArtistLookups);
        var rows = await Task.WhenAll(ranked.Select(async row =>
        {
            await lookups.WaitAsync(ct);
            try
            {
                if (own.Contains(row.Key))
                {
                    var artist = await catalog.FindAsync(used.First(s => BotText.Key(s.Name) == row.Key), ct);
                    return (Artist: artist, Songs: artist is null ? [] : await catalog.TopSongsAsync(artist, ct), Error: "");
                }
                var relatedSongs = await catalog.SongsByNameAsync(display[row.Key], ct);
                return (relatedSongs.Artist, relatedSongs.Songs, Error: "");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return (Artist: (CatalogArtist?)null, Songs: new List<CatalogSearchSong>(), Error: ArtistCatalog.Explain(ex)); }
            finally { progress?.Report($"Picking songs you don't have… {Interlocked.Increment(ref picked)} of {ranked.Count}"); lookups.Release(); }
        }));
        for (int k = 0; k < ranked.Count; k++)
        {
            var (key, value) = ranked[k];
            var (artist, candidates, error) = rows[k];
            if (error.Length > 0) { failed++; lastError = error; continue; }
            if (artist is null) continue;
            resolved[key] = artist; found++;
            var top = candidates.Where(s => !hidden.HidesEdition(s.Song)).Take(Discover.SongsPerArtist).ToList();
            for (int n = 0; n < top.Count; n++) songs.Add((top[n], value * (1 - 0.07 * n), key));
        }
        var distinct = SearchRanking.DeduplicateSongs(songs.Select(s => s.Track)).Select(t => t.Song.Id).ToHashSet(StringComparer.Ordinal);
        songs = songs.Where(s => distinct.Remove(s.Track.Song.Id)).ToList();
        var gone = songs.Count == 0 ? [] : await unavailable(songs.Select(s => s.Track.Song).ToList(), ct);
        var pool = songs.Where(s => !gone.Contains(s.Track.Song.Id)).OrderByDescending(s => s.Score).ToList();

        var result = new List<DiscoverSong>();
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        string? previous = null;
        while (result.Count < songCount && pool.Count > 0)
        {
            int pick = pool.FindIndex(s => s.Key != previous && count.GetValueOrDefault(s.Key) < 3);
            if (pick < 0) pick = pool.FindIndex(s => s.Key != previous);
            if (pick < 0) break;
            var chosen = pool[pick]; pool.RemoveAt(pick);
            bool mine = own.Contains(chosen.Key);
            var reason = mine ? "More from " + resolved[chosen.Key].Name : "Because you play " + because[chosen.Key].Seed;
            // Seed and Own are filled for the comparison only; 2.2.0 had the reason text alone.
            result.Add(new(chosen.Track, Math.Round(chosen.Score, 4), reason, mine ? resolved[chosen.Key].Name : because[chosen.Key].Seed, mine));
            count[chosen.Key] = count.GetValueOrDefault(chosen.Key) + 1;
            previous = chosen.Key;
        }
        var newArtists = ranked.Where(r => !own.Contains(r.Key) && resolved.ContainsKey(r.Key)).Take(12)
            .Select(r => new DiscoverArtist(resolved[r.Key], "Similar to " + because[r.Key].Seed)).ToList();
        var notice = found == 0 ? failed > 0 ? "For you could not reach the music catalogs: " + lastError : "None of your artists could be matched in the catalogs yet."
            : result.Count == 0 ? songs.Count == 0 ? "The catalogs returned no usable song suggestions this time. Try refreshing later."
                : "Everything For you found is already in your library or download queue. Refresh after listening some more."
            : failed > 0 ? $"Some artists could not be loaded ({lastError}); these are from the rest." : "";
        return new(result, newArtists, notice, catalog.HasLastFm, Guid.NewGuid().ToString("N"));
    }
}
