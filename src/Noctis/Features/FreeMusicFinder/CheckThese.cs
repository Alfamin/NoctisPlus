using System.Collections.Concurrent;
using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>
/// Owner #93: songs that only a close match supplied (the second-chance pass, after both bots found nothing exact). They are
/// saved to Songs like any other; this list marks them until the user says the song is right (or removes it). A few songs in a
/// thousand, so a plain list and a path set for the Songs mark.
/// </summary>
internal static class CheckThese
{
    internal sealed record Entry(string Path, string Title, string Artist, DateTimeOffset At);

    private static readonly ConcurrentDictionary<string, byte> _fetched = new();
    private static readonly object _gate = new();
    private static List<Entry> _entries = [];
    private static HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private static string? _file;

    /// <summary>Raised (any thread) when the list changes.</summary>
    internal static event Action? Changed;

    internal static void Fetched(MusicRequest request) => _fetched[request.Identity] = 0;
    internal static bool TakeFetched(MusicRequest request) => _fetched.TryRemove(request.Identity, out _);

    internal static IReadOnlyList<Entry> Entries { get { lock (_gate) return _entries.ToArray(); } }
    internal static int Count { get { lock (_gate) return _entries.Count; } }
    internal static bool Contains(string? path) { if (path is null) return false; lock (_gate) return _paths.Contains(path); }

    internal static void Load(string file)
    {
        List<Entry> loaded = [];
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length < 1024 * 1024)
                loaded = (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file)) ?? [])
                    .Where(e => e is { Path.Length: > 0 and < 1000 } && File.Exists(e.Path)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        lock (_gate) { _file = file; _entries = loaded; _paths = new(loaded.Select(e => e.Path), StringComparer.OrdinalIgnoreCase); }
        Raise();
    }

    internal static void Add(string path, MusicRequest wanted)
    {
        lock (_gate)
        {
            if (!_paths.Add(path)) return;
            _entries.Insert(0, new(path, wanted.Title, wanted.Artist, DateTimeOffset.UtcNow));
            Save();
        }
        Raise();
    }

    /// <summary>The user listened and kept it, or removed the song: the mark goes.</summary>
    internal static void Remove(string path)
    {
        lock (_gate)
        {
            if (!_paths.Remove(path)) return;
            _entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            Save();
        }
        Raise();
    }

    private static void Save()
    {
        if (_file is null) return;
        try { AtomicJson.Write(_file, _entries); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // the marks stay for this session
    }

    private static void Raise() { try { Changed?.Invoke(); } catch (Exception) { } }
}
