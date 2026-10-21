using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>How well one way of choosing seed artists did over a replayed listening history.</summary>
internal sealed record ReplayScore(string Name, int Cutoffs, double Recall, double ReturnRecall, double SkipShare, double Artists)
{
    public override string ToString() =>
        $"{Name,-10} recall {Recall:P1} · returning favourites {ReturnRecall:P1} · seeds skipped next {SkipShare:P1} · artists/seed list {Artists:0.0} ({Cutoffs} cutoffs)";
}

/// <summary>
/// Offline, chronological check of For you's taste step. At each cutoff only the plays before it are used; the next
/// fortnight's listening says which artists the listener went on to enjoy (two or more good plays) and which they only
/// skipped. Catalog lookups are not replayed (no network), so this measures the seeds that candidate retrieval starts
/// from, not the songs. Read only; nothing leaves the computer.
/// </summary>
internal static class DiscoverReplay
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    public static List<ReplayScore> Run(IReadOnlyList<TastePlay> plays, IReadOnlyList<LocalSong> library, IReadOnlyList<TastePlaylist>? playlists = null,
        int seeds = Discover.SeedArtistsUsed)
    {
        var ordered = plays.Where(p => Taste.MainArtist(p.Artist).Length > 0).OrderBy(p => p.At).ToList();
        var methods = new (string Name, Func<List<TastePlay>, DateTime, List<string>> Pick)[]
        {
            ("before", (history, at) => Baseline(history, library, at, seeds)),
            ("now", (history, at) => Taste.Seeds(history, library, at, seeds, playlists).Select(s => BotText.Key(s.Name)).ToList()),
        };
        var totals = methods.Select(_ => (Cutoffs: 0, Hit: 0, Liked: 0, ReturnHit: 0, Returning: 0, Skipped: 0, Seeds: 0)).ToArray();
        if (ordered.Count == 0) return methods.Select(m => new ReplayScore(m.Name, 0, 0, 0, 0, 0)).ToList();
        for (var cutoff = ordered[0].At.Date.AddDays(60); cutoff < ordered[^1].At; cutoff += Window)
        {
            var history = ordered.TakeWhile(p => p.At < cutoff).ToList();
            var next = ordered.SkipWhile(p => p.At < cutoff).TakeWhile(p => p.At < cutoff + Window).ToList();
            var byArtist = next.GroupBy(p => BotText.Key(Taste.MainArtist(p.Artist))).ToList();
            var liked = byArtist.Where(g => g.Count(p => p.Weight >= 1) >= 2).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            var skipped = byArtist.Where(g => g.All(p => p.Weight < 0)).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            if (liked.Count == 0) continue;
            // Favourites of old: good plays on five or more days between a year and two months before the cutoff.
            var lasting = history.Where(p => p.Weight >= 1 && p.At >= cutoff.AddDays(-365) && p.At < cutoff.AddDays(-60))
                .GroupBy(p => BotText.Key(Taste.MainArtist(p.Artist))).Where(g => g.Select(p => p.At.Date).Distinct().Count() >= 5).Select(g => g.Key);
            var returning = lasting.Where(liked.Contains).ToHashSet(StringComparer.Ordinal);
            for (int m = 0; m < methods.Length; m++)
            {
                var chosen = methods[m].Pick(history, cutoff).ToHashSet(StringComparer.Ordinal);
                var t = totals[m];
                totals[m] = (t.Cutoffs + 1, t.Hit + chosen.Count(liked.Contains), t.Liked + liked.Count, t.ReturnHit + chosen.Count(returning.Contains),
                    t.Returning + returning.Count, t.Skipped + chosen.Count(skipped.Contains), t.Seeds + chosen.Count);
            }
        }
        return methods.Select((m, i) =>
        {
            var t = totals[i];
            return new ReplayScore(m.Name, t.Cutoffs, Ratio(t.Hit, t.Liked), Ratio(t.ReturnHit, t.Returning), Ratio(t.Skipped, t.Seeds), t.Cutoffs == 0 ? 0 : (double)t.Seeds / t.Cutoffs);
        }).ToList();
    }

    private static double Ratio(int a, int b) => b == 0 ? 0 : (double)a / b;

    /// <summary>For you's seed step as it was in 2.2.0, kept for comparison.</summary>
    internal static List<string> Baseline(IEnumerable<TastePlay> plays, IReadOnlyList<LocalSong> library, DateTime nowUtc, int count) =>
        DiscoverBaseline.Seeds(plays, library, nowUtc, count).Select(s => BotText.Key(Taste.MainArtist(s.Name))).ToList();

    /// <summary>
    /// The local report on a COPY of the listener's data folder (Milestone C): the seed replay above, the pages the shelf
    /// recorded, the saved page, and both engines run on the saved catalog. Offline and read-only: every HTTP request goes to
    /// a handler that refuses it before anything is sent, so lookups the saved catalog cannot answer just fail; the shelf's
    /// state is copied to a temporary folder before the engines use it. Prints counts and percentages only.
    /// </summary>
    public static async Task ReportAsync(string noctisData, TextWriter output, CancellationToken ct = default)
    {
        var finder = new[] { Path.Combine(noctisData, "plugin-data", "dev.moshi.freemusicfinder"), noctisData }.First(d => Directory.Exists(d));
        string? Optional(string name) => Path.Combine(finder, name) is var path && File.Exists(path) ? path : null;
        var (library, albums) = ReadLibrary(Path.Combine(noctisData, "library.json"));
        var plays = TasteSources.Load(noctisData);
        var playlists = TasteSources.Playlists(noctisData);
        output.WriteLine($"Replaying {plays.Count} plays, {library.Count} library songs, {playlists.Count} playlists ({playlists.Count(p => p.Imported)} imported).");
        output.WriteLine("Library marks and playlists are today's, applied at every cutoff; plays are strictly before each cutoff.");
        foreach (var score in Run(plays, library, playlists)) output.WriteLine("  " + score);
        output.WriteLine("recall: share of artists enjoyed in the next 14 days that were seeds. returning favourites: the same for artists");
        output.WriteLine("loved months earlier and played again. seeds skipped next: seeds that were only skipped in the next 14 days.");
        var owned = library.Select(s => DiscoverIdentity.Key(s.Title, s.Artist.Length > 0 ? s.Artist : s.AlbumArtist)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);

        if (Optional("for-you-feedback.json") is { } feedbackPath)
        {
            var shelf = new DiscoverFeedback(feedbackPath);
            output.WriteLine();
            output.WriteLine("Shelf: " + shelf.Summary(shelf.Learn(plays, library, playlists)));
            var pages = new List<DiscoverPageMetrics>();
            var dismissed = new HashSet<string>(StringComparer.Ordinal);
            int back = 0;
            HashSet<string>? previous = null;
            foreach (var batch in shelf.Batches.OrderBy(b => b.At))
            {
                var heard = TasteSources.Familiar(plays.Where(p => p.At < batch.At.UtcDateTime));
                var cards = batch.Shown.OrderBy(c => c.Position).Take(Discover.Shelf).ToList();
                back += cards.Count(c => dismissed.Contains(c.Key));
                pages.Add(DiscoverPageMetrics.Of(cards, owned, heard, previous));
                var used = shelf.Interactions.Where(x => x.Batch == batch.Id).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
                dismissed.UnionWith(shelf.Interactions.Where(x => x.Batch == batch.Id && x.Action == DiscoverAction.Dismiss).Select(x => x.Key));
                previous = cards.Where(c => (batch.Seen?.Contains(c.Position) ?? c.Position < DiscoverFeedback.FrontOfShelf) && !used.Contains(c.Key)).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            }
            output.WriteLine($"Recorded pages (mean of {pages.Count}): {DiscoverPageMetrics.Mean(pages)}");
            output.WriteLine($"  dismissed songs shown again on a later page: {back}. Owned is today's library, so a song bought after it was shown counts too.");
        }

        ForYouCache.Page? saved = null;
        try { if (Optional("for-you-last.json") is { } last) saved = JsonSerializer.Deserialize<ForYouCache.Page>(File.ReadAllText(last)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        if (saved?.Result.Songs is { Count: > 0 } songs)
        {
            var heard = TasteSources.Familiar(plays.Where(p => p.At < saved.At.UtcDateTime));
            output.WriteLine();
            output.WriteLine($"Saved page ({saved.At:yyyy-MM-dd}): {DiscoverPageMetrics.Of(songs.Take(Discover.Shelf).ToList(), owned, heard)}");
        }

        var catalogFolder = Path.Combine(finder, "artist-catalog");
        output.WriteLine();
        if (!Directory.Exists(catalogFolder))
        {
            output.WriteLine("No artist-catalog folder in the copy, so the engines were not compared song by song.");
            return;
        }
        var asOf = saved?.At ?? DateTimeOffset.UtcNow;
        var refuse = new Refusing();
        using var catalog = new ArtistCatalog(catalogFolder, refuse, () => asOf, TimeSpan.Zero);
        using var similar = new SimilarArtists(catalogFolder, refuse, () => asOf, TimeSpan.Zero);
        using var lastFm = File.Exists(Path.Combine(catalogFolder, "lastfm.json")) ? new LastFmSimilar(catalogFolder, refuse, () => new string('0', 32), () => asOf, TimeSpan.Zero) : null;
        var discover = new CatalogForDiscover(() => catalog, name => albums.GetValueOrDefault(BotText.Key(Taste.MainArtist(name))) ?? [], () => similar, () => lastFm);
        var work = Path.Combine(Path.GetTempPath(), "noctis-discover-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var before = plays.Where(p => p.At < asOf.UtcDateTime).ToList();
            var runs = await DiscoverEvaluation.RunAsync(before, DiscoverBaseline.Plays(noctisData).Where(p => p.At < asOf.UtcDateTime).ToList(), library, playlists,
                () => discover, work, asOf, Optional("for-you-feedback.json"), Optional("for-you-hidden.json"), ct);
            output.WriteLine($"Both engines on your saved catalog, as of {asOf:yyyy-MM-dd} ({DiscoverEvaluation.Pages} pages; each shows its first {DiscoverEvaluation.InView} cards; "
                + "on page 1 the first card and the next by its artist are dismissed and one other is finished):");
            foreach (var run in runs) output.WriteLine("  " + run.ToString().Replace("\n", "\n  "));
            output.WriteLine($"  {refuse.Refused} lookups were not in the saved catalog and were refused locally (nothing was sent); those artists count as unavailable.");
            output.WriteLine("  Owned leakage uses today's library by title and artist; the app also checks the files and the download queue.");
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    /// <summary>Every request fails before it leaves: the replay never uses the network.</summary>
    private sealed class Refusing : HttpMessageHandler
    {
        private int _refused;
        public int Refused => _refused;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _refused);
            throw new HttpRequestException("Offline replay: not in the saved catalog.");
        }
    }

    /// <summary>library.json: the songs with the listener's marks, and the album titles per artist (namesake checks).</summary>
    internal static (List<LocalSong> Songs, Dictionary<string, List<string>> Albums) ReadLibrary(string path)
    {
        var library = new List<LocalSong>();
        var albums = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return (library, albums);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (library, albums);
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                JsonElement? P(string name) => t.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault();
                string S(string name) => P(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";
                var song = new LocalSong(Guid.TryParse(S("id"), out var id) ? id : Guid.NewGuid(), S("artist"), S("title"), S("filePath"), TimeSpan.Zero,
                    AlbumArtist: S("albumArtist"), Favorite: P("isFavorite") is { ValueKind: JsonValueKind.True },
                    Rating: P("rating") is { ValueKind: JsonValueKind.Number } r && r.TryGetInt32(out var rating) ? rating : 0, Disliked: P("isDisliked") is { ValueKind: JsonValueKind.True });
                library.Add(song);
                var key = BotText.Key(Taste.MainArtist(song.Artist.Length > 0 ? song.Artist : song.AlbumArtist));
                if (key.Length > 0 && S("album") is { Length: > 0 } album)
                {
                    if (!albums.TryGetValue(key, out var list)) albums[key] = list = [];
                    if (!list.Contains(album)) list.Add(album);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return (library, albums);
    }
}
