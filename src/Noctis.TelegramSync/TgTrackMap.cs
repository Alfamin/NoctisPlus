using System.Text.Json;

namespace Noctis.TelegramSync;

/// <summary>A local file the map may link an incoming song to.</summary>
public sealed record TgLocalFile(string TrackId, string Path, long Size, string RecordingKey);

/// <summary>
/// Persisted map Telegram documentId &lt;-&gt; local track id (small JSON file, atomic write:
/// temp file + replace). <see cref="LinkOrNull"/> links an incoming song to a file already in the
/// local library (same size + recording key) so it is not downloaded again.
/// </summary>
public sealed class TgTrackMap
{
    private readonly Dictionary<long, string> _byDoc = new();
    private readonly Dictionary<string, long> _byTrack = new(StringComparer.Ordinal);
    private readonly string _path;

    public TgTrackMap(string path) { _path = path; Load(); }

    public int Count => _byDoc.Count;

    public string? TrackFor(long documentId) => _byDoc.TryGetValue(documentId, out var t) ? t : null;
    public long? DocumentFor(string trackId) => _byTrack.TryGetValue(trackId, out var d) ? d : null;

    public void Set(long documentId, string trackId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        if (_byDoc.TryGetValue(documentId, out var oldTrack) && oldTrack != trackId) Remove(documentId);
        _byDoc[documentId] = trackId;
        _byTrack.TryAdd(trackId, documentId); // aliases keep the first usable home as the primary one
    }

    public bool Remove(long documentId)
    {
        if (!_byDoc.Remove(documentId, out var t)) return false;
        if (_byTrack.GetValueOrDefault(t) == documentId)
        {
            _byTrack.Remove(t);
            foreach (var other in _byDoc)
                if (other.Value == t) { _byTrack[t] = other.Key; break; }
        }
        return true;
    }

    /// <summary>Already mapped, or a local file with the same size and recording key: map + return its track id. Null = download.</summary>
    public string? LinkOrNull(TgDocument doc, string recordingKey, IEnumerable<TgLocalFile> local)
    {
        var files = local.ToList();
        if (TrackFor(doc.DocumentId) is { } known)
        {
            if (files.Any(f => f.TrackId == known && File.Exists(f.Path))) return known;
            Remove(doc.DocumentId); // A stale link must not prevent downloading the file again.
        }
        if (string.IsNullOrWhiteSpace(recordingKey) || doc.Size <= 0) return null;
        foreach (var f in files)
        {
            if (f.Size != doc.Size || !string.Equals(f.RecordingKey, recordingKey, StringComparison.Ordinal)) continue;
            if (!File.Exists(f.Path)) continue;
            // Another Telegram document may contain this exact local recording.
            // LinkOrNull returns it without replacing its primary document mapping.
            Set(doc.DocumentId, f.TrackId);
            return f.TrackId;
        }
        return null;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(dir);
        var data = _byDoc.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
        TgStateFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(new MapFile(1, data)));
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var f = JsonSerializer.Deserialize<MapFile>(File.ReadAllText(_path));
            if (f is not { V: 1, Map: not null }) return;
            foreach (var (k, v) in f.Map)
                if (!string.IsNullOrWhiteSpace(v) && long.TryParse(k, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
                    Set(id, v);
        }
        catch (JsonException) { /* corrupt file: start empty, files re-link by size + recording key */ }
    }

    private sealed record MapFile(int V, Dictionary<string, string> Map);
}

/// <summary>Snapshot cadence: a snapshot every <see cref="Every"/> posts; posts older than the last snapshot may be deleted.</summary>
public sealed record TgSnapshotRule(int Every = 200)
{
    public bool SnapshotDue(long postsSinceSnapshot) => postsSinceSnapshot >= Every;

    /// <summary>Only delete posts the snapshot actually contains, not posts merely older than its upload.</summary>
    public bool CanDelete(long postMessageId, long? snapshotUpToMessageId) =>
        postMessageId > 0 && snapshotUpToMessageId is { } covered && postMessageId <= covered;
}
