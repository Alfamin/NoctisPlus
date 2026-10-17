using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>One play of something by an artist, and what it says: finished or kept on is positive, an early skip negative.</summary>
internal sealed record TastePlay(string Artist, DateTime At, double Weight, string Title = "");

/// <summary>An artist the listener likes, with how much (decayed plays plus how many of their songs they own), and a few of
/// their song titles the listener has (they tell a namesake apart).</summary>
internal sealed record TasteSeed(string Name, double Weight, int Owned, IReadOnlyList<string>? Titles = null)
{
    public IReadOnlyList<string> SongTitles => Titles ?? [];
}

/// <summary>A song the listener does not have yet, and why it was picked.</summary>
internal sealed record DiscoverSong(CatalogSearchSong Track, double Score, string Because);
internal sealed record DiscoverArtist(CatalogArtist Artist, string Because);
internal sealed record DiscoverResult(List<DiscoverSong> Songs, List<DiscoverArtist> Artists, string Notice, bool LastFm = false);

/// <summary>The listener's taste in artists, from what they play and what they own. Nothing leaves the computer.</summary>
internal static class Taste
{
    private static readonly TimeSpan HalfLife = TimeSpan.FromDays(30);

    /// <summary>The first credited artist: "Juice WRLD feat. Halsey" and "Juice WRLD; Halsey" are Juice WRLD.</summary>
    public static string MainArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        var a = artist.Trim();
        foreach (var cut in new[] { ";", " feat.", " feat ", " ft.", " ft ", " featuring ", " with ", " (feat" })
        {
            int i = a.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
            if (i > 0) a = a[..i];
        }
        return a.Trim();
    }

    public static List<TasteSeed> Seeds(IEnumerable<TastePlay> plays, IReadOnlyList<LocalSong> library, DateTime nowUtc, int count = 15)
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
            var main = MainArtist(play.Artist);
            var key = BotText.Key(main);
            if (key.Length == 0) continue;
            var age = nowUtc - play.At;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            weight[key] = weight.GetValueOrDefault(key) + play.Weight * Math.Pow(0.5, age / HalfLife);
            Name(main);
            if (play.Weight > 0) Title(key, play.Title, play.Weight);
        }
        var owned = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var song in library)
        {
            var main = MainArtist(song.Artist.Length > 0 ? song.Artist : song.AlbumArtist);
            var key = BotText.Key(main);
            if (key.Length == 0) continue;
            owned[key] = owned.GetValueOrDefault(key) + 1;
            Name(main);
            Title(key, song.Title, 0.01);
        }
        // Owning many songs of an artist is taste too, but less than playing them: 20 songs count like one recent finish.
        foreach (var (key, n) in owned) weight[key] = weight.GetValueOrDefault(key) + 0.4 * Math.Log(1 + n);
        return weight.Where(w => w.Value > 0.2).OrderByDescending(w => w.Value).Take(count)
            .Select(w => new TasteSeed(names[w.Key].OrderByDescending(n => n.Value).First().Key, Math.Round(w.Value, 3), owned.GetValueOrDefault(w.Key),
                titles.TryGetValue(w.Key, out var t) ? t.OrderByDescending(x => x.Value).Select(x => x.Key).Take(3).ToList() : []))
            .ToList();
    }
}

/// <summary>Where the plays come from: True Shuffle's listening log (exact) and Noctis's own play history (before it).</summary>
internal static class TasteSources
{
    public static List<TastePlay> Load(string? noctisData)
    {
        if (noctisData is null) return [];
        var listens = ShuffleListens(Path.Combine(noctisData, "plugin-data", "dev.moshi.trueshuffle", "listens.jsonl"));
        var since = listens.Count > 0 ? listens.Min(p => p.At) : DateTime.MaxValue;
        return NoctisHistory(Path.Combine(noctisData, "play_history.json"), since).Concat(listens).ToList();
    }

    private static bool Readable(string path) => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && new FileInfo(path).Length < 256L * 1024 * 1024;

    /// <summary>play_history.json: when, and whether Noctis marked it skipped. Read only.</summary>
    public static List<TastePlay> NoctisHistory(string path, DateTime before)
    {
        var result = new List<TastePlay>();
        try
        {
            if (!Readable(path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var artist = Str(e, "Artist"); var at = Time(e, "PlayedAtUtc");
                if (artist.Length == 0 || at is not { } when || when >= before) continue;
                result.Add(new(artist, when, Bool(e, "Skipped") ? -0.7 : 1.0, Str(e, "Title")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return result;
    }

    /// <summary>True Shuffle's listens.jsonl: how much was heard and how each play ended. Read only.</summary>
    public static List<TastePlay> ShuffleListens(string path)
    {
        var result = new List<TastePlay>();
        try
        {
            if (!Readable(path)) return result;
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var e = doc.RootElement;
                    var artist = Str(e, "artist"); var at = Time(e, "startUtc");
                    if (artist.Length == 0 || at is not { } when) continue;
                    var end = Str(e, "end"); double coverage = Num(e, "coverage"), heard = Num(e, "listenedMs");
                    double w = end == "Finished" ? 1.5
                        : coverage >= 0.5 || heard >= 240_000 ? 1.0
                        : end == "Skipped" ? (heard < 3_000 ? -0.3 : coverage < 0.25 ? -1.5 : 0.3)
                        : heard >= 30_000 ? 0.3 : 0;
                    if (w != 0) result.Add(new(artist, when, w, Str(e, "title")));
                }
                catch (JsonException) { } // a line cut by a crash
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty(name, out var v)) return v;
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
    private static string Str(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";
    private static bool Bool(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.True };
    private static double Num(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : 0;
    private static DateTime? Time(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.String } v && v.TryGetDateTime(out var t)
        ? (t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null;
}

/// <summary>What Discover needs from the catalog (an interface, so the ranking can be tested without a network).</summary>
internal interface IDiscoverCatalog
{
    string Notice => "";
    bool HasLastFm => false;
    /// <summary>The catalog artist for one of the listener's artists, or null when the name is ambiguous (never a guessed namesake).</summary>
    Task<CatalogArtist?> FindAsync(TasteSeed seed, CancellationToken ct);
    /// <summary>The catalog artist with exactly this name (a similar artist, known by name).</summary>
    Task<CatalogArtist?> FindByNameAsync(string name, CancellationToken ct);
    /// <summary>Artists whose listeners also play this one, most similar first.</summary>
    Task<List<SimilarArtist>> SimilarAsync(TasteSeed seed, CatalogArtist? artist, CancellationToken ct);
    Task<List<CatalogSearchSong>> TopSongsAsync(CatalogArtist artist, CancellationToken ct);
    async Task<CatalogArtistSongs> SongsByNameAsync(string name, CancellationToken ct)
    {
        var artist = await FindByNameAsync(name, ct);
        return new(artist, artist is null ? [] : await TopSongsAsync(artist, ct));
    }
}

/// <summary>
/// The real catalog: the listener's artists identified with their own albums (namesake-safe); similar artists from
/// ListenBrainz (and Deezer's related artists when Deezer can be reached); songs from the music catalog (Deezer, else Apple).
/// </summary>
internal sealed class CatalogForDiscover(Func<ArtistCatalog> catalog, Func<string, IReadOnlyList<string>> localAlbums, Func<SimilarArtists?> similar,
    Func<LastFmSimilar?>? lastFm = null) : IDiscoverCatalog
{
    public string Notice => lastFm?.Invoke()?.Notice ?? "";
    public bool HasLastFm => lastFm?.Invoke()?.Enabled == true;
    public async Task<CatalogArtist?> FindAsync(TasteSeed seed, CancellationToken ct) => (await catalog().IdentifyAsync(seed.Name, localAlbums(seed.Name), null, ct)).Artist;
    public async Task<CatalogArtist?> FindByNameAsync(string name, CancellationToken ct)
        => (await catalog().SearchAsync(name, false, ct)).Value.FirstOrDefault(a => BotText.Key(a.Name) == BotText.Key(name));
    public async Task<List<SimilarArtist>> SimilarAsync(TasteSeed seed, CatalogArtist? artist, CancellationToken ct)
    {
        var result = new List<SimilarArtist>();
        Exception? failure = null;
        if (lastFm?.Invoke() is { Enabled: true } last)
        {
            try
            {
                result = LastFmSimilar.Filter(await last.SimilarAsync(seed.Name, ct), seed.Name, []);
                if (result.Count > 0) return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { failure = ex; }
        }
        if (similar() is { } listenBrainz)
        {
            try
            {
                if (await listenBrainz.ArtistIdAsync(seed.Name, seed.SongTitles, ct) is { } mbid) result.AddRange(await listenBrainz.SimilarAsync(mbid, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { failure = ex; }
        }
        // Keyless/empty-provider fallback only. Successful Last.fm seeds never wait for a catalog identity lookup.
        if (result.Count == 0 && artist is null)
        {
            try { artist = await FindAsync(seed, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { failure ??= ex; }
        }
        if (artist is not null && artist.Id.StartsWith("deezer:", StringComparison.Ordinal))
        {
            try
            {
                var related = (await catalog().RelatedAsync(artist, ct)).Value;
                for (int i = 0; i < related.Count; i++)
                    if (!result.Any(r => BotText.Key(r.Name) == BotText.Key(related[i].Name))) result.Add(new(related[i].Name, Math.Max(0.05, 1 - i / 30.0)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { failure ??= ex; }
        }
        if (result.Count == 0 && failure is not null) throw failure;
        return result;
    }
    public async Task<List<CatalogSearchSong>> TopSongsAsync(CatalogArtist artist, CancellationToken ct) => (await catalog().ArtistSongsAsync(artist, ct)).Value;
    public async Task<CatalogArtistSongs> SongsByNameAsync(string name, CancellationToken ct) => (await catalog().SongsByArtistNameAsync(name, ct)).Value;
}

/// <summary>
/// Songs the listener does not have, from the artists they play: Deezer's related artists of each favorite artist
/// (weighted by how much the listener likes that artist and how high Deezer ranks the relation), plus the songs of their
/// own artists that they are missing. Owned, queued and dismissed songs are left out; the same artist never comes twice
/// in a row or more than three times. Nothing is downloaded here.
/// </summary>
internal static class Discover
{
    public const int SeedArtistsUsed = 15, CandidateArtists = 30, SongsPerArtist = 8;

    public static async Task<DiscoverResult> BuildAsync(IReadOnlyList<TasteSeed> seeds, IDiscoverCatalog catalog,
        Func<IReadOnlyList<CatalogSong>, CancellationToken, Task<HashSet<string>>> unavailable, DiscoverHidden hidden,
        IProgress<string>? progress, CancellationToken ct, int songCount = 40)
    {
        if (seeds.Count == 0) return new([], [], "Play some music first: For you learns from what you listen to and what you own.");
        // Candidate artists by name: the listener's own (their missing songs) and the ones their listeners also play.
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
        var used = seeds.Take(SeedArtistsUsed).ToList();
        foreach (var seed in used) own.Add(BotText.Key(seed.Name));
        var lists = new List<(TasteSeed Seed, List<SimilarArtist> Similar)>();
        int complete = 0;
        using var concurrent = new SemaphoreSlim(3, 3);
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
        // Merge in seed order, not completion order: network timing must not change ranking or reasons.
        foreach (var row in relations)
        {
            if (row.Error.Length > 0) { failed++; lastError = row.Error; }
            lists.Add((row.Seed, row.Similar));
            Add(row.Seed.Name, Math.Sqrt(Math.Max(0, row.Seed.Weight)) * 0.6, row.Seed.Name);
        }
        // An artist on nearly every list (the biggest names) says little about this listener: it counts for less.
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
        var ranked = score.OrderByDescending(s => s.Value).Take(CandidateArtists).ToList();
        var songs = new List<(CatalogSearchSong Track, double Score, string Key)>();
        for (int k = 0; k < ranked.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var (key, value) = ranked[k];
            progress?.Report($"Picking songs you don't have… {k + 1} of {ranked.Count}");
            try
            {
                CatalogArtist? artist;
                List<CatalogSearchSong> candidates;
                if (own.Contains(key))
                {
                    artist = await catalog.FindAsync(used.First(s => BotText.Key(s.Name) == key), ct);
                    if (artist is null) continue;
                    candidates = await catalog.TopSongsAsync(artist, ct);
                }
                else
                {
                    var relatedSongs = await catalog.SongsByNameAsync(display[key], ct);
                    artist = relatedSongs.Artist; candidates = relatedSongs.Songs;
                    if (artist is null) continue;
                }
                resolved[key] = artist; found++;
                var top = candidates.Where(s => !hidden.HidesSong(s.Song)).Take(SongsPerArtist).ToList();
                for (int n = 0; n < top.Count; n++) songs.Add((top[n], value * (1 - 0.07 * n), key));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { failed++; lastError = ArtistCatalog.Explain(ex); }
        }
        // One recording once, explicit preferred; then leave out what is owned or already queued.
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
            if (pick < 0) break; // only songs of the artist just placed are left
            var chosen = pool[pick]; pool.RemoveAt(pick);
            var reason = own.Contains(chosen.Key) ? "More from " + resolved[chosen.Key].Name : "Because you play " + because[chosen.Key].Seed;
            result.Add(new(chosen.Track, Math.Round(chosen.Score, 4), reason));
            count[chosen.Key] = count.GetValueOrDefault(chosen.Key) + 1;
            previous = chosen.Key;
        }
        var newArtists = ranked.Where(r => !own.Contains(r.Key) && resolved.ContainsKey(r.Key)).Take(12)
            .Select(r => new DiscoverArtist(resolved[r.Key], "Similar to " + because[r.Key].Seed)).ToList();
        var notice = found == 0 ? failed > 0 ? "For you could not reach the music catalogs: " + lastError : "None of your artists could be matched in the catalogs yet."
            : result.Count == 0 ? songs.Count == 0 ? "The catalogs returned no usable song suggestions this time. Try refreshing later."
                : "Everything For you found is already in your library or download queue. Refresh after listening some more."
            : failed > 0 ? $"Some artists could not be loaded ({lastError}); these are from the rest." : "";
        if (catalog.Notice.Length > 0) notice = (notice + " " + catalog.Notice).Trim();
        return new(result, newArtists, notice, catalog.HasLastFm);
    }
}

/// <summary>Song-only dismissals. Legacy automatic artist exclusions are intentionally ignored.</summary>
internal sealed class DiscoverHidden
{
    private sealed record Saved(List<string> Songs, Dictionary<string, string>? Recordings = null, int Version = 2);
    private readonly string _path;
    private readonly HashSet<string> _songs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _recordings = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public DiscoverHidden(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && new FileInfo(path).Length < 4 * 1024 * 1024
                && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is { } saved)
            {
                _songs.UnionWith((saved.Songs ?? []).Where(s => s is { Length: > 0 and <= 200 }));
                foreach (var (k, v) in saved.Recordings ?? []) if (_songs.Contains(k) && v is { Length: > 0 and <= 2000 }) _recordings[k] = v;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public bool HidesSong(string id) { lock (_lock) return _songs.Contains(id); }
    public bool HidesSong(CatalogSong song) { lock (_lock) return _songs.Contains(song.Id) || _recordings.ContainsValue(Recording(song)); }
    private static string Recording(CatalogSong song) => CatalogEditions.Key(song.Title) + "|" + BotText.Key(song.Artists.FirstOrDefault()?.Name ?? "");

    /// <summary>Returns true only for a new dismissal; Undo must not erase an older preference.</summary>
    public bool Hide(CatalogSong song)
    {
        lock (_lock)
        {
            if (HidesSong(song)) return false;
            _songs.Add(song.Id); _recordings[song.Id] = Recording(song); Save(); return true;
        }
    }
    public void Undo(CatalogSong song) { lock (_lock) { _songs.Remove(song.Id); _recordings.Remove(song.Id); Save(); } }
    private void Save()
    {
        try
        {
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(_path))!); dir is not null; dir = dir.Parent)
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return;
            AtomicJson.Write(_path, new Saved([.. _songs], new(_recordings)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // kept for this session at least
    }
}

/// <summary>For you as the plugin offers it: the listener's taste, the catalog, the library check and the dismissed songs.</summary>
internal sealed class ForYouSource(Func<ArtistCatalog> catalog, DiscoveryActions? actions, Func<string, IReadOnlyList<string>> localAlbums,
    IPlaylistLibrary library, string? noctisData, string hiddenPath, Func<SimilarArtists?>? similar = null,
    Func<LastFmSimilar?>? lastFm = null, Func<DateTimeOffset>? now = null) : IDisposable
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly ForYouCache _cache = new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(hiddenPath))!, "for-you-last.json"));
    private readonly SemaphoreSlim _build = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ForYouCache.Page? _last;
    private bool _loaded, _warming, _disposed;
    private long _revision, _invalidation;
    public DiscoverHidden Hidden { get; } = new(hiddenPath);
    public bool UsesLastFm => lastFm?.Invoke()?.Enabled == true;
    public event Action<DiscoverResult>? PicksReady;

    public Task<DiscoverResult?> SavedAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_loaded) { _last = _cache.Read(_now()); _loaded = true; }
            return _last is { } page && page.Until > _now() ? Filter(page.Result) : null;
        }
    }, ct);

    private DiscoverResult Filter(DiscoverResult result) => result with
    {
        Songs = result.Songs.Where(s => !Hidden.HidesSong(s.Track.Song)).ToList(),
    };

    public async Task<DiscoverResult> BuildAsync(bool refresh, IProgress<string>? progress, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token); ct = linked.Token;
        await SavedAsync(ct);
        long revision; lock (_sync) revision = _revision;
        await _build.WaitAsync(ct);
        try
        {
            long invalidation;
            lock (_sync)
            {
                if (_disposed) throw new OperationCanceledException(ct);
                if (_last is { } last && last.Until > _now() && (_revision != revision || !refresh && _now() - last.At < TimeSpan.FromHours(12))) return Filter(last.Result);
                invalidation = _invalidation;
            }
            progress?.Report("Learning your taste…");
            var songs = await library.SnapshotAsync();
            var plays = await Task.Run(() => TasteSources.Load(noctisData), ct);
            var seeds = Taste.Seeds(plays, songs, _now().UtcDateTime);
            var result = await Discover.BuildAsync(seeds, new CatalogForDiscover(catalog, localAlbums, similar ?? (() => null), lastFm), Unavailable, Hidden, progress, ct);
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_disposed || invalidation != _invalidation) return Filter(result);
                // A transient outage never replaces a useful offline page with an empty failure.
                if (result.Songs.Count > 0)
                {
                    var until = _now().AddDays(7);
                    if (lastFm?.Invoke() is { } last && last.DataExpires < until) until = last.DataExpires;
                    _last = new(1, _now(), until, result); _revision++;
                    if (until > _now()) _cache.Write(_last);
                }
            }
            PicksReady?.Invoke(result);
            return Filter(result);
        }
        finally { _build.Release(); }
    }

    public void Forget() { lock (_sync) { _last = null; _invalidation++; } }

    /// <summary>Metadata only; delayed so startup and the library scan keep priority. One build per source.
    /// The view decides when to adopt it, so a background result cannot move rows under the pointer.</summary>
    public void WarmUp(TimeSpan? delay = null)
    {
        lock (_sync) { if (_warming || _disposed) return; _warming = true; }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay ?? TimeSpan.FromSeconds(20), _lifetime.Token);
                await BuildAsync(false, null, _lifetime.Token);
            }
            catch (Exception) { /* Keep the saved page; the next foreground refresh reports exact provider failures. */ }
            finally { lock (_sync) _warming = false; }
        });
    }

    public void Dispose() { lock (_sync) _disposed = true; _lifetime.Cancel(); }

    private async Task<HashSet<string>> Unavailable(IReadOnlyList<CatalogSong> songs, CancellationToken ct)
    {
        if (actions is null) return [];
        var matches = await actions.MatchAsync(songs, ct);
        return matches.Where(m => m.Value.Local is not null || m.Value.Download is not null).Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
    }
}
