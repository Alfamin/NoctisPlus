using System.Security.Cryptography;
using System.Text.Json;

namespace Noctis.Features.WordLyrics;

/// <summary>Durable song-level decisions, separate from the transient processing queue.
/// Cache/library copies share a key; unrelated recordings with the same filename do not.
/// Only two small samples are read, once per unchanged file, never the whole audio.</summary>
internal sealed class SongRules(string directory, Action<string> log)
{
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromDays(10);
    internal sealed record Rule(bool Ignored = false, DateTime RetryUtc = default);
    private readonly Dictionary<string, Rule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Saved(Dictionary<string, Rule> Rules, Dictionary<string, string> Paths);
    private readonly Dictionary<string, (long Length, DateTime Stamp, string Key)> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private string FileName => Path.Combine(directory, "song-rules.json");

    internal void Load()
    {
        try
        {
            if (!File.Exists(FileName)) return;
            if (new FileInfo(FileName).Length > 16 * 1024 * 1024 || (File.GetAttributes(FileName) & FileAttributes.ReparsePoint) != 0)
                throw new JsonException("Song rules are too large or redirected.");
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FileName));
            if (saved is { Rules: null } or { Paths: null }) throw new JsonException("Missing song rules.");
            lock (_gate) if (saved is not null)
            {
                foreach (var (key, value) in saved.Rules) if (value is not null) _rules[key] = value;
                foreach (var (path, key) in saved.Paths) _paths[path] = key;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { log("WordLyrics song rules could not be read: " + ex.Message); }
    }

    private string Key(string path)
    {
        path = Path.GetFullPath(path);
        if (_paths.TryGetValue(path, out var identity)) return identity;
        var file = new FileInfo(path);
        // Remember a known identity while a drive is disconnected.
        if (!file.Exists) return _keys.TryGetValue(path, out var known) ? known.Key : path;
        if (_keys.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Stamp == file.LastWriteTimeUtc) return cached.Key;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[Math.Min(8192L, file.Length)];
        var first = Math.Min(4096, bytes.Length);
        input.ReadExactly(bytes.AsSpan(0, first));
        if (bytes.Length > first)
        {
            input.Position = file.Length - (bytes.Length - first);
            input.ReadExactly(bytes.AsSpan(first));
        }
        var key = Path.GetFileName(path).ToUpperInvariant() + ":" + file.Length + ":" + Convert.ToHexString(SHA256.HashData(bytes));
        _keys[path] = (file.Length, file.LastWriteTimeUtc, key);
        return key;
    }

    internal Rule Get(string path)
    {
        lock (_gate)
        {
            if (_rules.Count == 0) return new();
            try { return _rules.GetValueOrDefault(Key(path)) ?? new(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { return _keys.TryGetValue(path, out var cached) ? _rules.GetValueOrDefault(cached.Key) ?? new() : new(); }
        }
    }
    internal void Set(string path, Rule rule)
    {
        lock (_gate)
        {
            if (rule == new Rule() && _rules.Count == 0) return;
            var key = Key(path);
            _paths[Path.GetFullPath(path)] = key; // a tag edit or finishing stream must not undo an explicit choice
            if (!rule.Ignored && rule.RetryUtc == default) _rules.Remove(key);
            else _rules[key] = rule;
            Save();
        }
    }
    internal void Share(string source, string target)
    {
        lock (_gate)
        {
            var key = Key(source);
            var other = Key(target);
            var a = _rules.GetValueOrDefault(key) ?? new();
            var b = _rules.GetValueOrDefault(other) ?? new();
            _paths[Path.GetFullPath(source)] = key;
            _paths[Path.GetFullPath(target)] = key;
            if (a == new Rule() && b == new Rule()) return;
            _rules[key] = new(a.Ignored || b.Ignored, a.RetryUtc > b.RetryUtc ? a.RetryUtc : b.RetryUtc);
            Save();
        }
    }
    private void Save()
    {
        var temporary = FileName + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Saved(_rules, _paths)));
        File.Move(temporary, FileName, overwrite: true);
    }
}
