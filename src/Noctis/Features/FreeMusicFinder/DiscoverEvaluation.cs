using System.Collections.Concurrent;
using System.Diagnostics;

namespace FreeMusicFinder;

/// <summary>
/// One page's measurements (Milestone C, section 5 of docs/fork/discover-plan.md). Fractions are of the page's cards.
/// <paramref name="Top3"/>: cards among the first three songs of their artist's catalog list; <paramref name="MeanPlace"/>: their
/// mean place in it (1 = the artist's most popular song); <paramref name="MedianRank"/>: median Deezer rank of the cards that have one.
/// <paramref name="Uncredited"/> (a diagnostic added after the criteria were set): cards whose credits do not name the artist whose
/// list they came from, such as a compilation's song. <paramref name="Placed"/>: cards whose place in their artist's list is known;
/// <paramref name="Top3"/> and <paramref name="MeanPlace"/> are of those (songs like a liked recording come from no artist's list, so
/// their place is looked up; one not found there counts as below the list, one whose list cannot be read is left out).
/// </summary>
internal sealed record DiscoverPageMetrics(int Cards, double Duplicates, double Owned, double Heard, double Repeat, int Artists, int MaxPerArtist,
    int Seeds, int MaxPerSeed, double OwnArtists, double Top3, double MeanPlace, double MedianRank, double Uncredited = 0, double Placed = 0)
{
    public static DiscoverPageMetrics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public override string ToString() =>
        $"cards {Cards,2} · dup {Duplicates:P0} · owned {Owned:P0} · heard {Heard:P0} · repeat {Repeat:P0} · artists {Artists,2} (max {MaxPerArtist}) · "
        + $"seeds {Seeds,2} (max {MaxPerSeed}) · own-artist {OwnArtists:P0} · top-3 {Top3:P0} of {Placed:P0} placed · mean place {MeanPlace:0.0} · median rank {MedianRank:0} · uncredited {Uncredited:P0}";

    /// <summary>The cards' seed: the listener's artist they came through (from the reason for pages that did not store it).</summary>
    public static string SeedOf(DiscoverSong song) => BotText.Key(song.Seed is { Length: > 0 } seed ? seed
        : song.Because.StartsWith("Because you play ", StringComparison.Ordinal) ? song.Because["Because you play ".Length..]
        : song.Because.StartsWith("More from ", StringComparison.Ordinal) ? song.Because["More from ".Length..] : "");

    /// <param name="previous">Recordings that were in view on the previous page and not used.</param>
    /// <param name="place">An artist-list place per song id, when the catalog's order is known.</param>
    public static DiscoverPageMetrics Of(IReadOnlyList<DiscoverSong> page, IReadOnlySet<string> owned, IReadOnlySet<string> heard,
        IReadOnlySet<string>? previous = null, IReadOnlyDictionary<string, int>? place = null, IReadOnlyDictionary<string, string>? listedUnder = null)
    {
        if (page.Count == 0) return Empty;
        var keys = page.Select(s => DiscoverIdentity.Key(s.Track.Song)).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int duplicates = keys.Count(k => k.Length > 0 && !seen.Add(k));
        var artists = page.GroupBy(s => DiscoverIdentity.Artist(s.Track.Song.Artists.FirstOrDefault()?.Name)).ToList();
        var seeds = page.GroupBy(SeedOf).ToList();
        var places = place is null ? [] : page.Select(s => place.TryGetValue(s.Track.Song.Id, out var p) ? p : 0).Where(p => p > 0).ToList();
        var ranks = page.Select(s => s.Track.Song.Popularity).Where(r => r > 0).Order().ToList();
        double n = page.Count;
        return new(page.Count, duplicates / n, keys.Count(owned.Contains) / n, keys.Count(heard.Contains) / n,
            previous is null ? 0 : keys.Count(previous.Contains) / n, artists.Count, artists.Max(g => g.Count()), seeds.Count, seeds.Max(g => g.Count()),
            page.Count(s => s.Own) / n, places.Count == 0 ? 0 : places.Count(p => p <= 3) / (double)places.Count, places.Count == 0 ? 0 : places.Average(),
            ranks.Count == 0 ? 0 : ranks.Count % 2 == 1 ? ranks[ranks.Count / 2] : (ranks[ranks.Count / 2 - 1] + ranks[ranks.Count / 2]) / 2.0,
            listedUnder is null ? 0 : page.Count(s => listedUnder.TryGetValue(s.Track.Song.Id, out var a) && !s.Track.Song.Artists.Any(c => BotText.Key(c.Name) == a)) / n,
            places.Count / n);
    }

    /// <summary>A page the shelf recorded (for-you-feedback.json): what M1–M6 can tell from the cards' keys, artists and seeds.</summary>
    public static DiscoverPageMetrics Of(IReadOnlyList<DiscoverShown> cards, IReadOnlySet<string> owned, IReadOnlySet<string> heard, IReadOnlySet<string>? previous = null)
    {
        if (cards.Count == 0) return Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var artists = cards.GroupBy(c => c.Artist).ToList();
        var seeds = cards.GroupBy(c => BotText.Key(c.Seed)).ToList();
        double n = cards.Count;
        return new(cards.Count, cards.Count(c => !seen.Add(c.Key)) / n, cards.Count(c => owned.Contains(c.Key)) / n, cards.Count(c => heard.Contains(c.Key)) / n,
            previous is null ? 0 : cards.Count(c => previous.Contains(c.Key)) / n, artists.Count, artists.Max(g => g.Count()), seeds.Count, seeds.Max(g => g.Count()),
            cards.Count(c => c.Own) / n, 0, 0, 0);
    }

    public static DiscoverPageMetrics Mean(IReadOnlyList<DiscoverPageMetrics> pages)
    {
        var p = pages.Where(m => m.Cards > 0).ToList();
        if (p.Count == 0) return Empty;
        return new((int)Math.Round(p.Average(m => m.Cards)), p.Average(m => m.Duplicates), p.Average(m => m.Owned), p.Average(m => m.Heard), p.Average(m => m.Repeat),
            (int)Math.Round(p.Average(m => m.Artists)), p.Max(m => m.MaxPerArtist), (int)Math.Round(p.Average(m => m.Seeds)), p.Max(m => m.MaxPerSeed),
            p.Average(m => m.OwnArtists), p.Average(m => m.Top3), p.Average(m => m.MeanPlace), p.Average(m => m.MedianRank), p.Average(m => m.Uncredited), p.Average(m => m.Placed));
    }
}

/// <summary>Passes every call through and remembers each song's place in its artist's list (1 = first), for M7, and whose list it was.</summary>
internal sealed class PlaceRecorder(IDiscoverCatalog inner) : IDiscoverCatalog
{
    public ConcurrentDictionary<string, int> Places { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> ListedUnder { get; } = new(StringComparer.Ordinal);
    public string Notice => inner.Notice;
    public bool HasLastFm => inner.HasLastFm;
    public Task<CatalogArtist?> FindAsync(TasteSeed seed, CancellationToken ct) => inner.FindAsync(seed, ct);
    public Task<CatalogArtist?> FindByNameAsync(string name, CancellationToken ct) => inner.FindByNameAsync(name, ct);
    public Task<List<SimilarArtist>> SimilarAsync(TasteSeed seed, CatalogArtist? artist, CancellationToken ct) => inner.SimilarAsync(seed, artist, ct);
    public async Task<List<CatalogSearchSong>> TopSongsAsync(CatalogArtist artist, CancellationToken ct) => Remember(await inner.TopSongsAsync(artist, ct), artist.Name);
    public async Task<CatalogArtistSongs> SongsByNameAsync(string name, CancellationToken ct)
    {
        var songs = await inner.SongsByNameAsync(name, ct);
        Remember(songs.Songs, name);
        return songs;
    }
    /// <summary>Passed through; a source's order is no artist's list, so nothing is placed here (see <see cref="PlaceAsync"/>).</summary>
    public Task<List<CatalogSearchSong>> SongsLikeAsync(TasteRecording liked, CancellationToken ct) => inner.SongsLikeAsync(liked, ct);

    /// <summary>Places the page's cards that came from no artist's list: in their artist's list, by recording, or below it when not
    /// there. A card whose artist's list cannot be read stays unplaced.</summary>
    public async Task PlaceAsync(IEnumerable<DiscoverSong> page, CancellationToken ct)
    {
        foreach (var card in page.Where(c => !Places.ContainsKey(c.Track.Song.Id) && c.Track.Song.Artists.Count > 0).ToList())
        {
            List<CatalogSearchSong> list;
            try { list = (await SongsByNameAsync(Taste.MainArtist(card.Track.Song.Artists[0].Name), ct)).Songs; }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { continue; }
            if (list.Count == 0 || Places.ContainsKey(card.Track.Song.Id)) continue;
            var key = DiscoverIdentity.Key(card.Track.Song);
            var at = list.FindIndex(s => DiscoverIdentity.Key(s.Song) == key);
            Places[card.Track.Song.Id] = at >= 0 ? at + 1 : list.Count + 1;
        }
    }

    private List<CatalogSearchSong> Remember(List<CatalogSearchSong> songs, string artist)
    {
        for (int i = 0; i < songs.Count; i++)
        {
            Places.AddOrUpdate(songs[i].Song.Id, i + 1, (_, old) => Math.Min(old, i + 1));
            ListedUnder.TryAdd(songs[i].Song.Id, BotText.Key(artist));
        }
        return songs;
    }
}

/// <summary>What one engine did over a run of pages: per-page metrics, and what the first page's actions changed on the second.</summary>
internal sealed record DiscoverRun(string Name, List<DiscoverPageMetrics> Pages, int DismissedReturned, (int Before, int After) DismissedArtist,
    (int Before, int After) EnjoyedArtist, double Streak, List<TimeSpan> Times, long Allocated, string Notice)
{
    public override string ToString() =>
        $"{Name,-7} {DiscoverPageMetrics.Mean(Pages)}\n        second-page repeat {(Pages.Count > 1 ? Pages[1].Repeat : 0):P0} · in view on all of the last 3 pages {Streak:P0} · "
        + $"dismissed songs back {DismissedReturned} · twice-dismissed artist {DismissedArtist.Before}→{DismissedArtist.After} cards · "
        + $"enjoyed artist {EnjoyedArtist.Before}→{EnjoyedArtist.After} cards · build {(Times.Count == 0 ? 0 : Times.Average(t => t.TotalMilliseconds)):0} ms, {Allocated / 1024.0 / 1024:0.0} MB allocated";
}

/// <summary>
/// The baseline-versus-current comparison (Milestone C), with Milestone C's artist-only engine ("artists") beside the current one
/// ("now", which adds songs like the listener's best-liked recordings). All engines get the same listener, catalog and clock and the same
/// script: each page is shown with its first <see cref="InView"/> cards in view; on the first page the listener dismisses
/// the first card and the next one by the same artist, and finishes one other card; nothing else is used. The current
/// engine records what the shelf records (shown, seen, actions); the baseline has only its dismissals, as in 2.2.0.
/// Offline: every lookup goes to <paramref name="catalog"/>. State is written only under the work folder.
/// </summary>
internal static class DiscoverEvaluation
{
    public const int InView = 12, Pages = 3;

    public static async Task<List<DiscoverRun>> RunAsync(IReadOnlyList<TastePlay> plays, IReadOnlyList<TastePlay> plays220, IReadOnlyList<LocalSong> library,
        IReadOnlyList<TastePlaylist> playlists, Func<IDiscoverCatalog> catalog, string work, DateTimeOffset start,
        string? feedbackFrom = null, string? hiddenFrom = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(work);
        var owned = library.Select(s => DiscoverIdentity.Key(s.Title, s.Artist.Length > 0 ? s.Artist : s.AlbumArtist)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        Task<HashSet<string>> Unavailable(IReadOnlyList<CatalogSong> songs, CancellationToken _) =>
            Task.FromResult(songs.Where(s => owned.Contains(DiscoverIdentity.Key(s))).Select(s => s.Id).ToHashSet(StringComparer.Ordinal));
        var runs = new List<DiscoverRun>();
        foreach (var name in new[] { "before", "artists", "now" })
        {
            bool current = name != "before";
            var clock = start;
            string Copy(string? from, string file)
            {
                var path = Path.Combine(work, name + "-" + file);
                if (File.Exists(path)) File.Delete(path);
                if (from is not null && File.Exists(from)) File.Copy(from, path);
                return path;
            }
            var hidden = new DiscoverHidden(Copy(hiddenFrom, "for-you-hidden.json"));
            var feedback = new DiscoverFeedback(Copy(current ? feedbackFrom : null, "for-you-feedback.json"), () => clock);
            var recorder = new PlaceRecorder(catalog());
            var listened = new List<TastePlay>();
            var pages = new List<DiscoverPageMetrics>();
            var seenPages = new List<HashSet<string>>();
            var times = new List<TimeSpan>();
            var dismissed = new List<string>();
            string dismissedArtist = "", enjoyedArtist = "";
            int returned = 0, dismissedBefore = 0, dismissedAfter = 0, enjoyedBefore = 0, enjoyedAfter = 0;
            double streak = 0;
            long allocated = 0;
            string notice = "";
            for (int p = 0; p < Pages; p++)
            {
                clock = start + TimeSpan.FromDays(p);
                var history = (current ? plays : plays220).Concat(listened).Where(x => x.At < clock.UtcDateTime).ToList();
                long before = GC.GetTotalAllocatedBytes(true);
                var watch = Stopwatch.StartNew();
                var result = current
                    ? await Discover.ForListenerAsync(history, library, playlists, feedback, hidden, recorder, Unavailable, null, clock, ct, songs: name == "now")
                    : await DiscoverBaseline.ForListenerAsync(history, library, hidden, recorder, Unavailable, clock, ct);
                times.Add(watch.Elapsed);
                allocated += GC.GetTotalAllocatedBytes(true) - before;
                if (p == 0) notice = result.Notice;
                var page = result.Songs.Where(s => !hidden.HidesSong(s.Track.Song)).Take(Discover.Shelf).ToList();
                var keys = page.Select(s => DiscoverIdentity.Key(s.Track.Song)).ToList();
                await recorder.PlaceAsync(page, ct);
                string ArtistOf(DiscoverSong s) => DiscoverIdentity.Artist(s.Track.Song.Artists.FirstOrDefault()?.Name);
                pages.Add(DiscoverPageMetrics.Of(page, owned, TasteSources.Familiar(plays.Concat(listened).Where(x => x.At < clock.UtcDateTime)),
                    seenPages.Count > 0 ? seenPages[^1] : null, recorder.Places, recorder.ListedUnder));
                if (p == 1)
                {
                    returned = keys.Count(dismissed.Contains);
                    dismissedAfter = page.Count(s => dismissedArtist.Length > 0 && ArtistOf(s) == dismissedArtist);
                    enjoyedAfter = page.Count(s => enjoyedArtist.Length > 0 && ArtistOf(s) == enjoyedArtist);
                }
                if (p == Pages - 1 && seenPages.Count >= 2 && page.Count > 0)
                    streak = keys.Count(k => seenPages.TakeLast(2).All(s => s.Contains(k))) / (double)page.Count;
                if (current && result.Batch is { Length: > 0 } batch)
                {
                    feedback.Shown(batch, page.Select((s, i) => new DiscoverShown(keys[i], s.Track.Song.Id, ArtistOf(s), s.Seed, s.Own, i)).ToList());
                    feedback.Seen(batch, Enumerable.Range(0, Math.Min(InView, page.Count)));
                }
                var used = new HashSet<int>();
                if (p == 0 && page.Count > 0)
                {
                    dismissedArtist = ArtistOf(page[0]);
                    var twice = page.FindIndex(1, s => ArtistOf(s) == dismissedArtist);
                    foreach (var i in twice > 0 ? new[] { 0, twice } : [0])
                    {
                        hidden.Hide(page[i].Track.Song); dismissed.Add(keys[i]); used.Add(i);
                        if (current) feedback.Record(result.Batch ?? "", keys[i], ArtistOf(page[i]), page[i].Track.Song.Title, DiscoverAction.Dismiss);
                    }
                    dismissedBefore = page.Count(s => ArtistOf(s) == dismissedArtist);
                    var enjoy = page.FindIndex(s => ArtistOf(s) != dismissedArtist);
                    if (enjoy >= 0 && enjoy < InView)
                    {
                        var song = page[enjoy].Track.Song;
                        enjoyedArtist = ArtistOf(page[enjoy]); used.Add(enjoy);
                        enjoyedBefore = page.Count(s => ArtistOf(s) == enjoyedArtist);
                        if (current) feedback.Record(result.Batch ?? "", keys[enjoy], enjoyedArtist, song.Title, DiscoverAction.Play);
                        listened.Add(new(song.Artists[0].Name, clock.UtcDateTime.AddMinutes(10), 1.5, song.Title, true));
                    }
                }
                seenPages.Add(Enumerable.Range(0, Math.Min(InView, page.Count)).Where(i => !used.Contains(i)).Select(i => keys[i]).ToHashSet(StringComparer.Ordinal));
            }
            await feedback.Flushed;
            runs.Add(new(name, pages, returned, (dismissedBefore, dismissedAfter), (enjoyedBefore, enjoyedAfter), streak, times, allocated, notice));
        }
        return runs;
    }
}
