namespace Noctis.Services;

/// <summary>
/// Folders the library never takes songs from, whatever the library folders are (owner #87: the stream cache of
/// songs that were only played). Someone whose library folder is the whole user folder would otherwise have every
/// played song scanned or picked up by the folder watcher. Registered by the feature that owns the folder.
/// </summary>
public static class LibraryExclusions
{
    private static readonly object Gate = new();
    private static string[] _folders = [];

    public static IReadOnlyList<string> Folders { get { lock (Gate) return _folders; } }

    public static void Register(string folder)
    {
        var full = Normalize(folder);
        if (full is null) return;
        lock (Gate) if (!_folders.Contains(full, StringComparer.OrdinalIgnoreCase)) _folders = [.. _folders, full];
    }

    public static void Unregister(string folder)
    {
        var full = Normalize(folder);
        if (full is null) return;
        lock (Gate) _folders = _folders.Where(f => !string.Equals(f, full, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static bool IsExcluded(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string[] folders;
        lock (Gate) folders = _folders;
        if (folders.Length == 0) return false;
        try
        {
            var full = Path.GetFullPath(path);
            return folders.Any(f => full.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, f, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string? Normalize(string folder)
    {
        try { return string.IsNullOrWhiteSpace(folder) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
