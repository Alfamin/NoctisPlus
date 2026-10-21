using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>One play of something by an artist, and what it says: finished or kept on is positive, an early skip negative.
/// <paramref name="Heard"/>: the listener audibly heard this recording, so it is no discovery any more.</summary>
internal sealed record TastePlay(string Artist, DateTime At, double Weight, string Title = "", bool Heard = false);

/// <summary>A playlist of the listener's: made by them (each song a deliberate choice) or imported from a streaming service.</summary>
internal sealed record TastePlaylist(bool Imported, IReadOnlyList<Guid> Tracks, IReadOnlyDictionary<Guid, DateTime>? Added = null);

/// <summary>An artist the listener likes, with how much (decayed plays plus how many of their songs they own), and a few of
/// their song titles the listener has (they tell a namesake apart).</summary>
internal sealed record TasteSeed(string Name, double Weight, int Owned, IReadOnlyList<string>? Titles = null)
{
    public IReadOnlyList<string> SongTitles => Titles ?? [];
}

/// <summary>A song the listener does not have yet, and why it was picked.</summary>
/// <paramref name="Seed"/> is the listener's artist it came through; <paramref name="Own"/>: it is one of their own artists' songs.
internal sealed record DiscoverSong(CatalogSearchSong Track, double Score, string Because, string Seed = "", bool Own = false);
internal sealed record DiscoverArtist(CatalogArtist Artist, string Because);
/// <paramref name="Batch"/> names this page, so showing it again is not counted as showing it twice. <paramref name="Unreachable"/>:
/// no catalog answered, so this empty page is an outage, not an answer; the shelf keeps the picks it has.
internal sealed record DiscoverResult(List<DiscoverSong> Songs, List<DiscoverArtist> Artists, string Notice, bool LastFm = false, string Batch = "", bool Unreachable = false);

/// <summary>What Discover leaves out or rests besides owned and dismissed songs: recordings already heard, and what earlier pages taught.</summary>
internal sealed record DiscoverContext(IReadOnlySet<string> Familiar, DiscoverLearning Learning, DateTimeOffset Now);

/// <summary>The listener's taste in artists, from what they play and what they own. Nothing leaves the computer.</summary>
internal static class Taste
{
    private static readonly TimeSpan HalfLife = TimeSpan.FromDays(30), LongHalfLife = TimeSpan.FromDays(365);
    /// <summary>Seed places kept for lasting interests (years of one artist) that a recent phase would push out.</summary>
    public const int DurableSeeds = 5;

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
        // Owner #94: "Big Sean, Drake" and "Drake/ WizKid/ Kyla" were taste seeds of their own: no catalog knows them,
        // and every lookup fell through to the slow Apple lane. "Tyler, The Creator" and "AC/DC" stay one artist.
        var parts = System.Text.RegularExpressions.Regex.Split(a, @"\s*(?:,|/)\s+");
        if (parts.Length > 1) a = parts[1].StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? parts[0] + ", " + parts[1] : parts[0];
        return a.Trim();
    }

    /// <summary>A track's evidence counts with diminishing returns: replays add, but one looped song cannot outweigh many liked ones.</summary>
    private static double Compress(double x) => x >= 0 ? 2 * Math.Log(1 + x / 2) : Math.Max(x, -3);

    /// <summary>
    /// The listener's artists, from two horizons: what they play lately (30-day half-life) and what they kept coming back to
    /// over the year (365 days, only for artists played on three or more days). Favorites, ratings and songs put in their own
    /// playlists are deliberate and count fully; imported playlists and owning files count a little. The last
    /// <see cref="DurableSeeds"/> places go to lasting interests that a recent phase pushed down, so one is not averaged away.
    /// </summary>
    public static List<TasteSeed> Seeds(IEnumerable<TastePlay> plays, IReadOnlyList<LocalSong> library, DateTime nowUtc, int count = 15,
        IReadOnlyList<TastePlaylist>? playlists = null)
    {
        var names = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var titles = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        var recent = new Dictionary<(string Artist, string Track), double>();
        var durable = new Dictionary<(string Artist, string Track), double>();
        var deliberate = new Dictionary<(string Artist, string Track), double>();
        var imported = new Dictionary<(int List, string Artist), double>();
        var days = new Dictionary<string, HashSet<DateOnly>>(StringComparer.Ordinal);
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
        static double Decay(TimeSpan age, TimeSpan halfLife) => Math.Pow(0.5, (age < TimeSpan.Zero ? TimeSpan.Zero : age) / halfLife);
        foreach (var play in plays)
        {
            var main = MainArtist(play.Artist);
            var key = BotText.Key(main);
            if (key.Length == 0) continue;
            var track = (key, DiscoverIdentity.Title(play.Title));
            var age = nowUtc - play.At;
            recent[track] = recent.GetValueOrDefault(track) + play.Weight * Decay(age, HalfLife);
            durable[track] = durable.GetValueOrDefault(track) + play.Weight * Decay(age, LongHalfLife);
            if (play.Weight > 0)
            {
                if (!days.TryGetValue(key, out var d)) days[key] = d = [];
                d.Add(DateOnly.FromDateTime(play.At));
            }
            Name(main);
            if (play.Weight > 0) Title(key, play.Title, play.Weight);
        }
        var owned = new Dictionary<string, int>(StringComparer.Ordinal);
        var byId = new Dictionary<Guid, (string Artist, string Track)>();
        foreach (var song in library)
        {
            var main = MainArtist(song.Artist.Length > 0 ? song.Artist : song.AlbumArtist);
            var key = BotText.Key(main);
            if (key.Length == 0) continue;
            owned[key] = owned.GetValueOrDefault(key) + 1;
            Name(main);
            Title(key, song.Title, 0.01);
            var track = (key, DiscoverIdentity.Title(song.Title));
            byId.TryAdd(song.Id, track);
            // Said outright, so it does not fade: a favorite, a rating, a "dislike" of that one song.
            double said = (song.Favorite ? 2 : 0) + song.Rating switch { >= 5 => 1.5, 4 => 1, > 0 and <= 2 => -1, _ => 0 } + (song.Disliked ? -1.5 : 0);
            if (said != 0) deliberate[track] = deliberate.GetValueOrDefault(track) + said;
        }
        for (int i = 0; i < (playlists?.Count ?? 0); i++)
        {
            var list = playlists![i];
            foreach (var id in list.Tracks.Distinct())
            {
                if (!byId.TryGetValue(id, out var track)) continue;
                // Thousands of songs imported from Spotify are one decision, not thousands of endorsements.
                if (list.Imported) { var k = (i, track.Artist); imported[k] = Math.Min(0.5, imported.GetValueOrDefault(k) + 0.05); continue; }
                var added = list.Added?.TryGetValue(id, out var at) == true ? Decay(nowUtc - at, LongHalfLife) : 0.5;
                deliberate[track] = Math.Min(deliberate.GetValueOrDefault(track) + 0.6 * added, 3.5);
            }
        }
        var weight = new Dictionary<string, double>(StringComparer.Ordinal);
        var lasting = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (track, v) in recent) weight[track.Artist] = weight.GetValueOrDefault(track.Artist) + Compress(v);
        foreach (var (track, v) in durable)
            if (days.GetValueOrDefault(track.Artist)?.Count >= 3) lasting[track.Artist] = lasting.GetValueOrDefault(track.Artist) + 0.5 * Compress(v);
        foreach (var (track, v) in deliberate) lasting[track.Artist] = lasting.GetValueOrDefault(track.Artist) + 0.6 * Compress(v);
        foreach (var ((_, artist), v) in imported) lasting[artist] = lasting.GetValueOrDefault(artist) + v;
        // Owning many songs of an artist is taste too, but less than playing them: 20 songs count like one recent finish.
        foreach (var (key, n) in owned) lasting[key] = lasting.GetValueOrDefault(key) + 0.4 * Math.Log(1 + n);
        foreach (var (key, v) in lasting) weight[key] = weight.GetValueOrDefault(key) + v;

        var eligible = weight.Where(w => w.Value > 0.2 && names.ContainsKey(w.Key)).ToList();
        var chosen = eligible.OrderByDescending(w => w.Value).ThenBy(w => w.Key, StringComparer.Ordinal).Take(Math.Max(0, count - DurableSeeds)).Select(w => w.Key).ToList();
        chosen.AddRange(eligible.Where(w => !chosen.Contains(w.Key) && lasting.GetValueOrDefault(w.Key) > 0.5)
            .OrderByDescending(w => lasting[w.Key]).ThenBy(w => w.Key, StringComparer.Ordinal).Take(Math.Min(DurableSeeds, count)).Select(w => w.Key).ToList());
        chosen.AddRange(eligible.Where(w => !chosen.Contains(w.Key)).OrderByDescending(w => w.Value).ThenBy(w => w.Key, StringComparer.Ordinal)
            .Take(Math.Max(0, count - chosen.Count)).Select(w => w.Key).ToList());
        return chosen.OrderByDescending(k => weight[k]).ThenBy(k => k, StringComparer.Ordinal)
            .Select(k => new TasteSeed(names[k].OrderByDescending(n => n.Value).First().Key, Math.Round(weight[k], 3), owned.GetValueOrDefault(k),
                titles.TryGetValue(k, out var t) ? t.OrderByDescending(x => x.Value).Select(x => x.Key).Take(3).ToList() : []))
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
        return Distinct(NoctisHistory(Path.Combine(noctisData, "play_history.json"), since).Concat(listens));
    }

    /// <summary>One play once: the same recording starting within the same two seconds is one event written twice.</summary>
    public static List<TastePlay> Distinct(IEnumerable<TastePlay> plays)
    {
        var seen = new HashSet<(string, long)>();
        return plays.Where(p => seen.Add((DiscoverIdentity.Key(p.Title, p.Artist) is { Length: > 0 } k ? k : BotText.Key(p.Artist), p.At.Ticks / (2 * TimeSpan.TicksPerSecond)))).ToList();
    }

    /// <summary>Recordings the listener has heard (by <see cref="DiscoverIdentity.Key(string?, string?)"/>): not owning one does not make it new to them.</summary>
    public static HashSet<string> Familiar(IEnumerable<TastePlay> plays) =>
        plays.Where(p => p.Heard).Select(p => DiscoverIdentity.Key(p.Title, p.Artist)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>Noctis's playlists.json: the listener's own playlists and the imported ones ("Imported from …"). Smart playlists are
    /// rules, not choices, and are left out. Read only.</summary>
    public static List<TastePlaylist> Playlists(string? noctisData)
    {
        var result = new List<TastePlaylist>();
        if (noctisData is null) return result;
        var path = Path.Combine(noctisData, "playlists.json");
        try
        {
            if (!Readable(path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var lists = doc.RootElement;
            if (Prop(lists, "playlists") is { } inner) lists = inner;
            if (lists.ValueKind != JsonValueKind.Array) return result;
            foreach (var list in lists.EnumerateArray())
            {
                if (list.ValueKind != JsonValueKind.Object || Bool(list, "IsSmartPlaylist")) continue;
                if (Prop(list, "TrackIds") is not { ValueKind: JsonValueKind.Array } ids) continue;
                var tracks = ids.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String && Guid.TryParse(i.GetString(), out var g) ? g : Guid.Empty)
                    .Where(g => g != Guid.Empty).ToList();
                var added = new Dictionary<Guid, DateTime>();
                if (Prop(list, "TrackAddedAt") is { ValueKind: JsonValueKind.Object } when)
                    foreach (var p in when.EnumerateObject())
                        if (Guid.TryParse(p.Name, out var id) && p.Value.ValueKind == JsonValueKind.String && p.Value.TryGetDateTime(out var at))
                            added[id] = at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : DateTime.SpecifyKind(at, DateTimeKind.Utc);
                bool imported = Str(list, "Description").StartsWith("Imported from ", StringComparison.Ordinal);
                if (tracks.Count > 0) result.Add(new(imported, tracks, added));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return result;
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
                // Older and coarser: it says a song was started, not how much of it was heard. A completed-looking
                // entry still means the listener knows the song.
                bool skipped = Bool(e, "Skipped");
                result.Add(new(artist, when, skipped ? -0.7 : 1.0, Str(e, "Title"), Heard: !skipped));
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
                    // "Finished" with little heard is a stream that failed or a length that was unknown, not a finish.
                    bool finished = end == "Finished" && (coverage >= 0.5 || heard >= 30_000);
                    // Under three audible seconds says nothing either way: a slip, a failed start or buffering, never a dislike.
                    double w = finished ? 1.5
                        : coverage >= 0.5 || heard >= 240_000 ? 1.0
                        : heard < 3_000 ? 0
                        : end == "Skipped" ? (coverage < 0.25 ? -1.5 : 0.3)
                        : heard >= 30_000 ? 0.3 : 0;
                    // Three audible seconds or more, or a real finish: this recording is known, whatever the verdict.
                    bool audible = finished || heard >= 3_000;
                    if (w != 0 || audible) result.Add(new(artist, when, w, Str(e, "title"), audible));
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
/// own artists that they are missing. Owned, queued, dismissed and already heard songs are left out, and so is a song
/// whose credits do not name the artist it was found for; one recording comes once whatever album or provider lists it.
/// Songs passed over on earlier pages rest for a while. The same artist never comes twice in a row or more than three
/// times, and no one of the listener's artists fills the shelf. Nothing is downloaded here.
/// </summary>
internal static class Discover
{
    public const int SeedArtistsUsed = 15, CandidateArtists = 30, SongsPerArtist = 8, ArtistLookups = 6;
    /// <summary>The cards the shelf shows; within them at most <see cref="PerSeed"/> come through one of the listener's artists
    /// (its own songs and its neighbours' together) and at most <see cref="OwnArtists"/> are their own artists' songs, while
    /// other songs are left. With nothing else left the shelf is still filled.</summary>
    public const int Shelf = 20, PerSeed = 6, OwnArtists = 8;

    /// <summary>The page for a listener: what earlier pages taught, their taste, and the songs. <see cref="ForYouSource"/>'s build,
    /// and the evaluation's, so both run the same steps.</summary>
    public static Task<DiscoverResult> ForListenerAsync(IReadOnlyList<TastePlay> plays, IReadOnlyList<LocalSong> library, IReadOnlyList<TastePlaylist> playlists,
        DiscoverFeedback feedback, DiscoverHidden hidden, IDiscoverCatalog catalog, Func<IReadOnlyList<CatalogSong>, CancellationToken, Task<HashSet<string>>> unavailable,
        IProgress<string>? progress, DateTimeOffset now, CancellationToken ct)
    {
        var learning = feedback.Learn(plays, library, playlists);
        var seeds = Taste.Seeds(plays.Concat(learning.Saves), library, now.UtcDateTime, playlists: playlists);
        return BuildAsync(seeds, catalog, unavailable, hidden, progress, ct, context: new(TasteSources.Familiar(plays), learning, now));
    }

    public static async Task<DiscoverResult> BuildAsync(IReadOnlyList<TasteSeed> seeds, IDiscoverCatalog catalog,
        Func<IReadOnlyList<CatalogSong>, CancellationToken, Task<HashSet<string>>> unavailable, DiscoverHidden hidden,
        IProgress<string>? progress, CancellationToken ct, int songCount = 40, DiscoverContext? context = null)
    {
        var familiar = context?.Familiar ?? new HashSet<string>();
        var learning = context?.Learning ?? DiscoverLearning.None;
        var now = context?.Now ?? DateTimeOffset.UtcNow;
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
        using var concurrent = new SemaphoreSlim(ArtistLookups, ArtistLookups);
        var relations = await Task.WhenAll(used.Select(async seed =>
        {
            await concurrent.WaitAsync(ct);
            try
            {
                return (Seed: seed, Similar: LastFmSimilar.Filter(await catalog.SimilarAsync(seed, null, ct), seed.Name, used.Select(s => s.Name)).Take(40).ToList(), Error: "");
            }
            catch (Exception ex) when (Failed(ex, ct)) { return (Seed: seed, Similar: new List<SimilarArtist>(), Error: ArtistCatalog.Explain(ex)); }
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
        int heard = 0, rested = 0;
        // Owner #94: every artist's songs at once (the catalog itself keeps to its few connections), merged in rank
        // order afterwards so network timing never changes the picks.
        int picked = 0;
        using var lookups = new SemaphoreSlim(ArtistLookups, ArtistLookups);
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
            catch (Exception ex) when (Failed(ex, ct)) { return (Artist: (CatalogArtist?)null, Songs: new List<CatalogSearchSong>(), Error: ArtistCatalog.Explain(ex)); }
            finally { progress?.Report($"Picking songs you don't have… {Interlocked.Increment(ref picked)} of {ranked.Count}"); lookups.Release(); }
        }));
        for (int k = 0; k < ranked.Count; k++)
        {
            var (key, value) = ranked[k];
            var (artist, candidates, error) = rows[k];
            if (error.Length > 0) { failed++; lastError = error; continue; }
            if (artist is null) continue;
            resolved[key] = artist; found++;
            // Scored by the catalog's own order, so leaving one song out never lifts or lowers the others.
            var top = new List<(CatalogSearchSong Song, double Kept)>();
            for (int n = 0; n < candidates.Count && top.Count < SongsPerArtist; n++)
            {
                var s = candidates[n];
                if (hidden.HidesSong(s.Song) || !Credits(s.Song, artist, key)) continue;
                var identity = DiscoverIdentity.Key(s.Song);
                if (familiar.Contains(identity)) { heard++; continue; }
                var rest = learning.Rest(identity, now);
                if (rest <= 0) { rested++; continue; }
                top.Add((s, rest * (1 - 0.07 * Math.Min(n, 12))));
            }
            foreach (var (song, kept) in top)
                songs.Add((song, value * kept * learning.ArtistFactor(DiscoverIdentity.Artist(song.Song.Artists[0].Name)), key));
        }
        // One recording once (its best place, explicit preferred), across albums and providers; then leave out what is owned or queued.
        songs = DiscoverIdentity.Distinct(songs.OrderByDescending(s => s.Score), s => s.Track.Song);
        var gone = songs.Count == 0 ? [] : await unavailable(songs.Select(s => s.Track.Song).ToList(), ct);
        var pool = songs.Where(s => !gone.Contains(s.Track.Song.Id)).OrderByDescending(s => s.Score).ToList();

        var result = new List<DiscoverSong>();
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        var perSeed = new Dictionary<string, int>(StringComparer.Ordinal);
        int ownCount = 0;
        string? previous = null;
        string SeedOf(string key) => own.Contains(key) ? key : BotText.Key(because[key].Seed);
        bool Varied(string key) => result.Count >= Shelf || perSeed.GetValueOrDefault(SeedOf(key)) < PerSeed && (!own.Contains(key) || ownCount < OwnArtists);
        while (result.Count < songCount && pool.Count > 0)
        {
            int pick = pool.FindIndex(s => s.Key != previous && count.GetValueOrDefault(s.Key) < 3 && Varied(s.Key));
            if (pick < 0) pick = pool.FindIndex(s => s.Key != previous && count.GetValueOrDefault(s.Key) < 3);
            if (pick < 0) pick = pool.FindIndex(s => s.Key != previous);
            if (pick < 0) break; // only songs of the artist just placed are left
            var chosen = pool[pick]; pool.RemoveAt(pick);
            bool mine = own.Contains(chosen.Key);
            var reason = mine ? "More from " + resolved[chosen.Key].Name : "Because you play " + because[chosen.Key].Seed;
            result.Add(new(chosen.Track, Math.Round(chosen.Score, 4), reason, mine ? resolved[chosen.Key].Name : because[chosen.Key].Seed, mine));
            count[chosen.Key] = count.GetValueOrDefault(chosen.Key) + 1;
            perSeed[SeedOf(chosen.Key)] = perSeed.GetValueOrDefault(SeedOf(chosen.Key)) + 1;
            if (mine) ownCount++;
            previous = chosen.Key;
        }
        var newArtists = ranked.Where(r => !own.Contains(r.Key) && resolved.ContainsKey(r.Key)).Take(12)
            .Select(r => new DiscoverArtist(resolved[r.Key], "Similar to " + because[r.Key].Seed)).ToList();
        var notice = found == 0 ? failed > 0 ? "For you could not reach the music catalogs: " + lastError : "None of your artists could be matched in the catalogs yet."
            : result.Count == 0 ? songs.Count == 0 ? heard + rested > 0 ? "You have heard or recently passed over every song For you found. Refresh after listening some more."
                    : "The catalogs returned no usable song suggestions this time. Try refreshing later."
                : "Everything For you found is already in your library or download queue. Refresh after listening some more."
            : failed > 0 ? $"Some artists could not be loaded ({lastError}); these are from the rest." : "";
        if (catalog.Notice.Length > 0) notice = (notice + " " + catalog.Notice).Trim();
        return new(result, newArtists, notice, catalog.HasLastFm, Guid.NewGuid().ToString("N"), Unreachable: found == 0 && failed > 0);
    }

    /// <summary>One artist's lookup failed, and the others go on: an error, or a provider's own timeout (an HTTP client's timeout is
    /// a cancellation nobody asked for). A real cancellation, and Offline mode, stop the whole build.</summary>
    private static bool Failed(Exception ex, CancellationToken ct) =>
        ex is not OperationCanceledException || ex is not Noctis.Services.OfflineModeException && !ct.IsCancellationRequested;

    /// <summary>The song credits the artist it was found for. A namesake's or a compilation's song is left out rather than guessed.</summary>
    private static bool Credits(CatalogSong song, CatalogArtist artist, string key) =>
        song.Artists.Count > 0 && song.Artists.Any(a => a.Id == artist.Id || BotText.Key(a.Name) is var k && k.Length > 0 && (k == key || k == BotText.Key(artist.Name)));
}

/// <summary>
/// Song-only dismissals ("Not for me"). Each hides that one recording, on any album or provider and as a remaster or clean
/// edition, but never its live, remix or other versions, its artist, album or neighbours. They last until Undo or a reset of
/// For you's learning; they do not expire. Legacy automatic artist exclusions are intentionally ignored.
/// </summary>
internal sealed class DiscoverHidden
{
    internal sealed record Identity(string Key, double? Seconds);
    private sealed record Saved(List<string> Songs, Dictionary<string, string>? Recordings = null, int Version = 3, Dictionary<string, Identity>? Identities = null);
    public const int MaxSongs = 5000;
    private readonly string _path;
    private readonly HashSet<string> _songs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _recordings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Identity> _identities = new(StringComparer.Ordinal);
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
                foreach (var (k, v) in saved.Identities ?? []) if (_songs.Contains(k) && v is { Key.Length: > 0 and <= 2000 }) _identities[k] = v;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public bool HidesSong(string id) { lock (_lock) return _songs.Contains(id); }
    public bool HidesSong(CatalogSong song)
    {
        lock (_lock)
        {
            if (_songs.Contains(song.Id) || _recordings.ContainsValue(Recording(song))) return true;
            var key = DiscoverIdentity.Key(song);
            return key.Length > 0 && _identities.Values.Any(i => i.Key == key && DiscoverIdentity.Same(song, new CatalogSong("", song.Title, song.Artists, i.Seconds, null)));
        }
    }
    /// <summary>2.2.0's rule (the song id, or the same edition key and artist), for <see cref="DiscoverBaseline"/> only.</summary>
    internal bool HidesEdition(CatalogSong song) { lock (_lock) return _songs.Contains(song.Id) || _recordings.ContainsValue(Recording(song)); }
    public int Count { get { lock (_lock) return _songs.Count; } }
    private static string Recording(CatalogSong song) => CatalogEditions.Key(song.Title) + "|" + BotText.Key(song.Artists.FirstOrDefault()?.Name ?? "");

    /// <summary>Returns true only for a new dismissal; Undo must not erase an older preference.</summary>
    public bool Hide(CatalogSong song)
    {
        lock (_lock)
        {
            if (HidesSong(song)) return false;
            if (_songs.Count >= MaxSongs) { var dropped = _songs.First(); _songs.Remove(dropped); _recordings.Remove(dropped); _identities.Remove(dropped); }
            _songs.Add(song.Id); _recordings[song.Id] = Recording(song); _identities[song.Id] = new(DiscoverIdentity.Key(song), song.Seconds); Save(); return true;
        }
    }
    public void Undo(CatalogSong song) { lock (_lock) { _songs.Remove(song.Id); _recordings.Remove(song.Id); _identities.Remove(song.Id); Save(); } }
    public void Clear() { lock (_lock) { _songs.Clear(); _recordings.Clear(); _identities.Clear(); Save(); } }
    private void Save()
    {
        try
        {
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(_path))!); dir is not null; dir = dir.Parent)
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return;
            AtomicJson.Write(_path, new Saved([.. _songs], new(_recordings), 3, new(_identities)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // kept for this session at least
    }
}

/// <summary>For you as the plugin offers it: the listener's taste, the catalog, the library check and the dismissed songs.</summary>
internal sealed class ForYouSource(Func<ArtistCatalog> catalog, DiscoveryActions? actions, Func<string, IReadOnlyList<string>> localAlbums,
    IPlaylistLibrary library, string? noctisData, string hiddenPath, Func<SimilarArtists?>? similar = null,
    Func<LastFmSimilar?>? lastFm = null, Func<DateTimeOffset>? now = null, Func<IDiscoverCatalog>? discover = null,
    Func<IReadOnlyList<CatalogSong>, CancellationToken, Task<HashSet<string>>>? unavailable = null) : IDisposable
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
    /// <summary>Which pages were shown and what was done with them; the next page learns from it.</summary>
    public DiscoverFeedback Feedback { get; } = new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(hiddenPath))!, "for-you-feedback.json"), now);
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

    /// <summary>The last saved page even when it has expired: what the shelf shows when a refresh
    /// cannot reach the catalog, instead of going blank.</summary>
    public DiscoverResult? LastSaved()
    {
        lock (_sync)
        {
            var page = _last ?? _cache.Read(_now(), expired: true);
            return page is null ? null : Filter(page.Result);
        }
    }

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
            // Reading the history, learning, taste and ranking are seconds of work on a large profile: none of it on the UI
            // thread (awaiting there would bring every step after the first lookup back to it).
            var result = await Task.Run(() => Discover.ForListenerAsync(TasteSources.Load(noctisData), songs, TasteSources.Playlists(noctisData), Feedback, Hidden,
                discover?.Invoke() ?? new CatalogForDiscover(catalog, localAlbums, similar ?? (() => null), lastFm), unavailable ?? Unavailable, progress, _now(), ct), ct);
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_disposed || invalidation != _invalidation) return Filter(result);
                // A transient outage never replaces a useful offline page with an empty failure, and is no news for the shelf.
                if (result.Songs.Count == 0) return Filter(result);
                var until = _now().AddDays(7);
                if (lastFm?.Invoke() is { } last && last.DataExpires < until) until = last.DataExpires;
                _last = new(1, _now(), until, result); _revision++;
                if (until > _now()) _cache.Write(_last);
            }
            PicksReady?.Invoke(result);
            return Filter(result);
        }
        finally { _build.Release(); }
    }

    public void Forget() { lock (_sync) { _last = null; _invalidation++; } }

    /// <summary>Forgets what For you learned here: dismissed songs, shown pages and their actions, and the saved page.
    /// Listening history, favorites and playlists belong to the library and are not touched.</summary>
    public void ResetLearning()
    {
        Hidden.Clear(); Feedback.Reset();
        lock (_sync) { _last = null; _loaded = true; _invalidation++; _cache.Delete(); }
    }

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
