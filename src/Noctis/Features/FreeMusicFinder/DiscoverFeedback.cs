using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeMusicFinder;

/// <summary>A card that was put on the shelf: which recording, where, and why it was picked.</summary>
internal sealed record DiscoverShown(string Key, string SongId, string Artist, string Seed, bool Own, int Position);
/// <summary>One page of picks the listener was shown, recorded once however often it is shown again.</summary>
internal sealed record DiscoverBatch(string Id, DateTimeOffset At, List<DiscoverShown> Shown);

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

    /// <summary>How much of its score a song keeps after being shown and passed over; 0 rests it for now.</summary>
    public double Rest(string key, DateTimeOffset now)
    {
        if (!Ignored.TryGetValue(key, out var seen)) return 1;
        var age = now - seen.Last;
        if (age > TimeSpan.FromDays(30)) return 1;
        return seen.Pages >= 3 ? 0 : seen.Pages >= 2 ? 0.5 : seen.Pages >= 1 ? 0.8 : 1;
    }

    /// <summary>One dismissal changes nothing beyond that song; each further one lowers the artist's other songs a little.</summary>
    public double ArtistFactor(string artistKey) => Dismissed.TryGetValue(artistKey, out var n) && n > 1 ? Math.Max(0.4, 1 / (1 + 0.3 * (n - 1))) : 1;
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

    public DiscoverLearning Learn()
    {
        var learning = new DiscoverLearning();
        lock (_lock)
        {
            var now = _now();
            var used = _interactions.Where(i => i.Action != DiscoverAction.Dismiss).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var batch in _batches)
                foreach (var card in batch.Shown.Where(c => c.Position < FrontOfShelf && !used.Contains(c.Key)).DistinctBy(c => c.Key))
                {
                    var seen = learning.Ignored.GetValueOrDefault(card.Key);
                    learning.Ignored[card.Key] = (seen.Pages + 1, seen.Last > batch.At ? seen.Last : batch.At);
                }
            foreach (var dismissal in _interactions.Where(i => i.Action == DiscoverAction.Dismiss && now - i.At < TimeSpan.FromDays(90)).DistinctBy(i => i.Key))
                learning.Dismissed[dismissal.Artist] = learning.Dismissed.GetValueOrDefault(dismissal.Artist) + 1;
            foreach (var save in _interactions.Where(i => i.Action is DiscoverAction.Download or DiscoverAction.Queue).DistinctBy(i => i.Key))
                learning.Saves.Add(new(save.Artist, save.At.UtcDateTime, 0.5, save.Title));
        }
        return learning;
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
