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
        if (_byDoc.TryGetValue(documentId, out var oldTrack)) _byTrack.Remove(oldTrack);
        if (_byTrack.TryGetValue(trackId, out var oldDoc)) _byDoc.Remove(oldDoc);
        _byDoc[documentId] = trackId;
        _byTrack[trackId] = documentId;
    }

    public bool Remove(long documentId)
    {
        if (!_byDoc.Remove(documentId, out var t)) return false;
        _byTrack.Remove(t);
        return true;
    }

    /// <summary>Already mapped, or a local file with the same size and recording key: map + return its track id. Null = download.</summary>
    public string? LinkOrNull(TgDocument doc, string recordingKey, IEnumerable<TgLocalFile> local)
    {
        if (TrackFor(doc.DocumentId) is { } known) return known;
        foreach (var f in local)
        {
            if (f.Size != doc.Size || !string.Equals(f.RecordingKey, recordingKey, StringComparison.Ordinal)) continue;
            if (_byTrack.ContainsKey(f.TrackId)) continue;
            Set(doc.DocumentId, f.TrackId);
            return f.TrackId;
        }
        return null;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        var data = _byDoc.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
        File.WriteAllText(tmp, JsonSerializer.Serialize(new MapFile(1, data)));
        File.Move(tmp, _path, overwrite: true);
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var f = JsonSerializer.Deserialize<MapFile>(File.ReadAllText(_path));
            if (f?.Map is null) return;
            foreach (var (k, v) in f.Map)
                if (long.TryParse(k, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
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

    /// <summary>A post may be deleted once a later snapshot covers it (its id is below the snapshot's message id).</summary>
    public bool CanDelete(long postMessageId, long? lastSnapshotMessageId) =>
        lastSnapshotMessageId is { } s && postMessageId < s;
}
