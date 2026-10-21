using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeMusicFinder;

/// <summary>A card that was put on the shelf: which recording, where, and why it was picked.</summary>
internal sealed record DiscoverShown(string Key, string SongId, string Artist, string Seed, bool Own, int Position);
/// <summary>One page of picks the listener was shown, recorded once however often it is shown again. <paramref name="Seen"/>:
/// the places that were actually scrolled into view (null for pages from before the view reported them).</summary>
internal sealed record DiscoverBatch(string Id, DateTimeOffset At, List<DiscoverShown> Shown, List<int>? Seen = null);

/// <summary>What became of a recommended recording, best first. Only played or saved cards get one: a card that was merely
/// shown has no outcome, which is not a negative.</summary>
internal enum DiscoverOutcome { None, Skipped, Sampled, Enjoyed, Saved, Kept }

[JsonConverter(typeof(JsonStringEnumConverter<DiscoverAction>))]
internal enum DiscoverAction { Play, Download, Queue, Open, Dismiss }
internal sealed record DiscoverInteraction(string Batch, string Key, string Artist, string Title, DiscoverAction Action, DateTimeOffset At);

/// <summary>
/// What the next page learns from the last ones. Exposure without a click is weak evidence: it only rests a song for a
/// while, it never counts against its artist. Only several dismissed songs of one artist lower that artist, and only a little.
/// </summary>
internal sealed class DiscoverLearning
{
    public static readonly DiscoverLearning None = new();
    /// <summary>Per recording key: in how many earlier pages it was shown near the front without being used, and when last.</summary>
    public Dictionary<string, (double Pages, DateTimeOffset Last)> Ignored { get; } = new(StringComparer.Ordinal);
    /// <summary>Per artist key: how many of their songs were dismissed lately.</summary>
    public Dictionary<string, int> Dismissed { get; } = new(StringComparer.Ordinal);
    /// <summary>Songs saved from the shelf: a deliberate act, counted like a fresh partial listen for that artist.</summary>
    public List<TastePlay> Saves { get; } = [];
    /// <summary>Per recording key of a played or saved card: the best that came of it.</summary>
    public Dictionary<string, DiscoverOutcome> Outcomes { get; } = new(StringComparer.Ordinal);
    /// <summary>Per artist key: how many of their recommended songs turned out well (enjoyed, saved, kept) or were skipped early.</summary>
    public Dictionary<string, (int Good, int Bad)> ArtistOutcomes { get; } = new(StringComparer.Ordinal);

    /// <summary>How much of its score a song keeps after being shown and passed over; 0 rests it for now.</summary>
    public double Rest(string key, DateTimeOffset now)
    {
        if (!Ignored.TryGetValue(key, out var seen)) return 1;
        var age = now - seen.Last;
        if (age > TimeSpan.FromDays(30)) return 1;
        return seen.Pages >= 3 ? 0 : seen.Pages >= 2 ? 0.5 : seen.Pages >= 1 ? 0.8 : 1;
    }

    /// <summary>
    /// How an artist's other songs fare on the next page. One dismissal or one early skip changes nothing beyond that song;
    /// each further one lowers the artist a little (never below 0.4). Recommended songs of theirs that the listener enjoyed,
    /// saved or kept lift the artist a little (at most 1.25): a new artist they took to is worth more of.
    /// </summary>
    public double ArtistFactor(string artistKey)
    {
        int dismissed = Dismissed.GetValueOrDefault(artistKey);
        var (good, bad) = ArtistOutcomes.GetValueOrDefault(artistKey);
        int against = Math.Max(0, dismissed + bad - good - 1);
        return against > 0 ? Math.Max(0.4, 1 / (1 + 0.3 * against)) : good > 0 ? Math.Min(1.25, 1 + 0.1 * good) : 1;
    }
}

/// <summary>
/// for-you-feedback.json: the pages that were shown and what was done with their cards. Bounded (180 days, 120 pages,
/// 3000 actions), written off the calling thread, never read by anything but For you. Reset deletes it.
/// </summary>
internal sealed class DiscoverFeedback
{
    private sealed record Saved(int Version, List<DiscoverBatch> Batches, List<DiscoverInteraction> Interactions);
    public static readonly TimeSpan Keep = TimeSpan.FromDays(180);
    public const int MaxBatches = 120, MaxInteractions = 3000, FrontOfShelf = 10;
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();
    private readonly List<DiscoverBatch> _batches = [];
    private readonly List<DiscoverInteraction> _interactions = [];
    private Task _write = Task.CompletedTask;
    private long _version;

    public DiscoverFeedback(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path; _now = now ?? (() => DateTimeOffset.UtcNow);
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && new FileInfo(path).Length < 8 * 1024 * 1024
                && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is { Version: 1 } saved)
            {
                _batches.AddRange((saved.Batches ?? []).Where(b => b is { Id.Length: > 0, Shown: not null }));
                _interactions.AddRange((saved.Interactions ?? []).Where(i => i is { Batch: not null, Key.Length: > 0, Artist: not null, Title: not null }));
                Prune();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public string FilePath => _path;
    /// <summary>The writes queued so far.</summary>
    public Task Flushed { get { lock (_lock) return _write; } }
    public IReadOnlyList<DiscoverBatch> Batches { get { lock (_lock) return _batches.ToList(); } }
    public IReadOnlyList<DiscoverInteraction> Interactions { get { lock (_lock) return _interactions.ToList(); } }

    /// <summary>Records a page as shown. False when this page was recorded already (shown again after navigating back).</summary>
    public bool Shown(string batch, IReadOnlyList<DiscoverShown> cards)
    {
        if (batch.Length == 0 || cards.Count == 0) return false;
        lock (_lock)
        {
            if (_batches.Any(b => b.Id == batch)) return false;
            _batches.Add(new(batch, _now(), cards.Where(c => c.Key.Length > 0).Take(40).ToList()));
            Prune(); Save(); return true;
        }
    }

    /// <summary>
    /// The places of a shown page that are in view now. Only these count as seen for resting songs. Called as the view
    /// scrolls; nothing is written unless a new place came into view.
    /// </summary>
    public bool Seen(string batch, IEnumerable<int> positions)
    {
        lock (_lock)
        {
            if (_batches.FirstOrDefault(b => b.Id == batch) is not { } shown) return false;
            int index = _batches.IndexOf(shown);
            var seen = shown.Seen ?? [];
            int before = seen.Count;
            foreach (var p in positions) if (!seen.Contains(p) && shown.Shown.Any(c => c.Position == p)) seen.Add(p);
            if (shown.Seen is not null && seen.Count == before) return false;
            _batches[index] = shown with { Seen = seen };
            Save(); return true;
        }
    }

    /// <summary>Records what was done with a card. Each kind of action counts once per card and page, however often it is clicked.</summary>
    public bool Record(string batch, string key, string artist, string title, DiscoverAction action)
    {
        if (key.Length == 0) return false;
        lock (_lock)
        {
            if (_interactions.Any(i => i.Batch == batch && i.Key == key && i.Action == action)) return false;
            _interactions.Add(new(batch, key, artist, title, action, _now()));
            Prune(); Save(); return true;
        }
    }

    /// <summary>Undo: the action is taken back as if it never happened.</summary>
    public void Retract(string batch, string key, DiscoverAction action)
    {
        lock (_lock) { if (_interactions.RemoveAll(i => i.Batch == batch && i.Key == key && i.Action == action) > 0) Save(); }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _batches.Clear(); _interactions.Clear(); _version++;
            var version = _version;
            _write = _write.ContinueWith(_ =>
            {
                try { lock (_lock) if (version == _version && File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) == 0) File.Delete(_path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// What the next page should know. <paramref name="plays"/>, <paramref name="library"/> and <paramref name="playlists"/>
    /// are the listener's own records; they tell how a played or saved card turned out (<see cref="Outcome"/>). Everything is
    /// worked out afresh from those records each time, so Undo, Reset and pruning can never leave stale statistics behind.
    /// </summary>
    public DiscoverLearning Learn(IReadOnlyList<TastePlay>? plays = null, IReadOnlyList<LocalSong>? library = null, IReadOnlyList<TastePlaylist>? playlists = null)
    {
        var learning = new DiscoverLearning();
        List<DiscoverInteraction> interactions;
        lock (_lock)
        {
            var now = _now();
            interactions = _interactions.ToList();
            var used = _interactions.Where(i => i.Action != DiscoverAction.Dismiss).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var batch in _batches)
                foreach (var card in batch.Shown.Where(c => (batch.Seen?.Contains(c.Position) ?? c.Position < FrontOfShelf) && !used.Contains(c.Key)).DistinctBy(c => c.Key))
                {
                    var seen = learning.Ignored.GetValueOrDefault(card.Key);
                    learning.Ignored[card.Key] = (seen.Pages + 1, seen.Last > batch.At ? seen.Last : batch.At);
                }
            foreach (var dismissal in _interactions.Where(i => i.Action == DiscoverAction.Dismiss && now - i.At < TimeSpan.FromDays(90)).DistinctBy(i => i.Key))
                learning.Dismissed[dismissal.Artist] = learning.Dismissed.GetValueOrDefault(dismissal.Artist) + 1;
            foreach (var save in _interactions.Where(i => i.Action is DiscoverAction.Download or DiscoverAction.Queue).DistinctBy(i => i.Key))
                learning.Saves.Add(new(save.Artist, save.At.UtcDateTime, 0.5, save.Title));
        }
        if (plays is null && library is null) return learning;
        var heard = (plays ?? []).Where(p => p.Weight != 0).Select(p => (Key: DiscoverIdentity.Key(p.Title, p.Artist), Play: p)).Where(p => p.Key.Length > 0)
            .ToLookup(p => p.Key, p => p.Play, StringComparer.Ordinal);
        var marked = Marked(library ?? [], playlists ?? []);
        foreach (var card in interactions.Where(i => i.Action is DiscoverAction.Play or DiscoverAction.Download or DiscoverAction.Queue).GroupBy(i => i.Key))
        {
            var outcome = Outcome(card.ToList(), heard[card.Key], marked.Contains(card.Key));
            if (outcome == DiscoverOutcome.None) continue;
            learning.Outcomes[card.Key] = outcome;
            var artist = card.First().Artist;
            var (good, bad) = learning.ArtistOutcomes.GetValueOrDefault(artist);
            learning.ArtistOutcomes[artist] = outcome >= DiscoverOutcome.Enjoyed ? (good + 1, bad) : outcome == DiscoverOutcome.Skipped ? (good, bad + 1) : (good, bad);
        }
        return learning;
    }

    /// <summary>Plays of a recommended card count from its first Play or save onwards (two minutes of clock slack).</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

    /// <summary>
    /// What became of one card, from the listener's own records after they first acted on it:
    /// Kept (a favourite, a rating of 4+, or in one of their own playlists; or two good plays on separate days),
    /// Saved (downloaded or queued), Enjoyed (one finish or qualified play), Sampled (a partial listen),
    /// Skipped (an audible early skip and nothing better). A quick skip, a failed start or no play at all is None.
    /// </summary>
    internal static DiscoverOutcome Outcome(IReadOnlyList<DiscoverInteraction> card, IEnumerable<TastePlay> plays, bool marked)
    {
        var from = card.Min(i => i.At).UtcDateTime - Slack;
        var after = plays.Where(p => p.At >= from).ToList();
        if (marked && card.Any(i => i.Action is DiscoverAction.Download or DiscoverAction.Queue)) return DiscoverOutcome.Kept;
        if (after.Where(p => p.Weight >= 1).Select(p => p.At.Date).Distinct().Count() >= 2) return DiscoverOutcome.Kept;
        if (card.Any(i => i.Action is DiscoverAction.Download or DiscoverAction.Queue)) return DiscoverOutcome.Saved;
        if (after.Any(p => p.Weight >= 1)) return DiscoverOutcome.Enjoyed;
        if (after.Any(p => p.Weight > 0)) return DiscoverOutcome.Sampled;
        if (after.Any(p => p.Weight < 0)) return DiscoverOutcome.Skipped;
        return DiscoverOutcome.None;
    }

    /// <summary>Recording keys the listener marked as theirs: favourite, rated 4 or 5, or in a playlist they made (not an import).</summary>
    private static HashSet<string> Marked(IReadOnlyList<LocalSong> library, IReadOnlyList<TastePlaylist> playlists)
    {
        var chosen = playlists.Where(p => !p.Imported).SelectMany(p => p.Tracks).ToHashSet();
        return library.Where(s => s.Favorite || s.Rating >= 4 || chosen.Contains(s.Id))
            .Select(s => DiscoverIdentity.Key(s.Title, s.Artist.Length > 0 ? s.Artist : s.AlbumArtist)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A plain count of what the shelf did, for the local replay report. No titles or artists.</summary>
    public string Summary(DiscoverLearning learning)
    {
        lock (_lock)
        {
            int cards = _batches.Sum(b => b.Shown.Count), seen = _batches.Sum(b => b.Seen?.Count ?? Math.Min(b.Shown.Count, FrontOfShelf));
            var acted = _interactions.GroupBy(i => i.Action).ToDictionary(g => g.Key, g => g.Select(i => i.Key).Distinct().Count());
            var outcomes = learning.Outcomes.Values.GroupBy(o => o).ToDictionary(g => g.Key, g => g.Count());
            string Count(DiscoverAction a) => acted.GetValueOrDefault(a).ToString();
            string Of(DiscoverOutcome o) => outcomes.GetValueOrDefault(o).ToString();
            return $"{_batches.Count} pages, {cards} cards shown, {seen} in view · played {Count(DiscoverAction.Play)}, saved {acted.GetValueOrDefault(DiscoverAction.Download) + acted.GetValueOrDefault(DiscoverAction.Queue)}, "
                + $"dismissed {Count(DiscoverAction.Dismiss)} · outcomes: kept {Of(DiscoverOutcome.Kept)}, saved {Of(DiscoverOutcome.Saved)}, enjoyed {Of(DiscoverOutcome.Enjoyed)}, "
                + $"sampled {Of(DiscoverOutcome.Sampled)}, skipped {Of(DiscoverOutcome.Skipped)}";
        }
    }

    private void Prune()
    {
        var cutoff = _now() - Keep;
        _batches.RemoveAll(b => b.At < cutoff);
        if (_batches.Count > MaxBatches) _batches.RemoveRange(0, _batches.Count - MaxBatches);
        _interactions.RemoveAll(i => i.At < cutoff);
        if (_interactions.Count > MaxInteractions) _interactions.RemoveRange(0, _interactions.Count - MaxInteractions);
    }

    private void Save()
    {
        var snapshot = new Saved(1, _batches.ToList(), _interactions.ToList());
        var version = _version;
        _write = _write.ContinueWith(_ =>
        {
            try
            {
                for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(_path))!); dir is not null; dir = dir.Parent)
                    if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return;
                lock (_lock) if (version != _version) return; // reset since: nothing old is written back
                AtomicJson.Write(_path, snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // kept for this session at least
        }, TaskScheduler.Default);
    }
}
