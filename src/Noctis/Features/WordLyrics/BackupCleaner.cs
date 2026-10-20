using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Noctis.Features.WordLyrics;

/// <summary>
/// Owner #96: WordLyrics copies every song before it works on it, again on every run, retries included (2.5 GB of copies
/// here). Once a run is over, a copy whose song is still there byte for byte is only a duplicate and goes. A copy whose song
/// is missing or differs is kept, whatever the reason, so nothing that exists only in a backup is ever removed.
/// Reads only what WordLyrics wrote about the run (summary.txt: "Backup copy: …"; request.json: the music folder and songs).
/// </summary>
internal static class BackupCleaner
{
    private static readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>After a run: its copies, in the background, at low priority.</summary>
    internal static void CleanLater(string runFolder, Action<string> log) => _ = Task.Run(async () =>
    {
        await _one.WaitAsync().ConfigureAwait(false);
        var priority = Thread.CurrentThread.Priority; Thread.CurrentThread.Priority = ThreadPriority.Lowest;
        try { Report(CleanRun(runFolder), log); }
        catch (Exception ex) { log("backup clean-up skipped: " + ex.Message); }
        finally { Thread.CurrentThread.Priority = priority; _one.Release(); }
    });

    /// <summary>Once per start: the copies earlier runs left (every run folder of this WordLyrics install).</summary>
    internal static void CleanEarlierLater(string toolFolder, Action<string> log, TimeSpan delay) => _ = Task.Run(async () =>
    {
        await Task.Delay(delay).ConfigureAwait(false);
        await _one.WaitAsync().ConfigureAwait(false);
        var priority = Thread.CurrentThread.Priority; Thread.CurrentThread.Priority = ThreadPriority.Lowest;
        try
        {
            var runs = Path.Combine(toolFolder, "runs");
            if (!Directory.Exists(runs)) return;
            var total = new Result(0, 0, 0);
            foreach (var summary in Directory.EnumerateFiles(runs, "summary.txt", SearchOption.AllDirectories))
            {
                var r = CleanRun(Path.GetDirectoryName(summary)!);
                total = new(total.Removed + r.Removed, total.Kept + r.Kept, total.Bytes + r.Bytes);
            }
            Report(total, log);
        }
        catch (Exception ex) { log("backup clean-up skipped: " + ex.Message); }
        finally { Thread.CurrentThread.Priority = priority; _one.Release(); }
    });

    internal sealed record Result(int Removed, int Kept, long Bytes);

    private static void Report(Result r, Action<string> log)
    {
        if (r.Removed > 0 || r.Kept > 0)
            log($"backups: removed {r.Removed} duplicate copies ({r.Bytes / 1048576.0:0} MB); kept {r.Kept} whose song is missing or changed");
    }

    /// <summary>The copies one run made: each one identical to its song is deleted; empty backup folders go too.</summary>
    internal static Result CleanRun(string runFolder)
    {
        var summary = Path.Combine(runFolder, "summary.txt");
        if (!File.Exists(summary)) return new(0, 0, 0);
        var backup = File.ReadLines(summary).Select(l => Regex.Match(l, @"^\s*Backup copy:\s*(.+?)\s*$")).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        if (backup is null || !Path.IsPathFullyQualified(backup)) return new(0, 0, 0);
        // WordLyrics keeps a small note beside each backup folder ("….wordlyrics.json"); with the folder gone it describes nothing.
        if (!Directory.Exists(backup)) { DropNote(backup); return new(0, 0, 0); }
        if (Redirected(backup)) return new(0, 0, 0);
        var (source, songs) = Request(Path.Combine(runFolder, "request.json"));
        if (source is null) return new(0, 0, 0);
        var root = Path.GetFullPath(backup).TrimEnd('\\', '/');
        // Never inside the music itself, and never a drive root.
        if (Path.GetPathRoot(root)?.TrimEnd('\\', '/') == root || Inside(root, source) || Inside(source, root)) return new(0, 0, 0);
        var byName = songs.GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        int removed = 0, kept = 0; long bytes = 0;
        foreach (var copy in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }))
        {
            var rel = Path.GetRelativePath(root, copy);
            // A whole-folder copy keeps the music folder's layout; a single-songs copy is flat, named after the song.
            var candidates = new List<string> { Path.Combine(source, rel) };
            if (byName.TryGetValue(Path.GetFileName(copy), out var named)) candidates.AddRange(named);
            var size = new FileInfo(copy).Length;
            if (candidates.Any(original => Same(original, copy, size)))
            {
                try { File.SetAttributes(copy, FileAttributes.Normal); File.Delete(copy); removed++; bytes += size; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { kept++; }
            }
            else kept++;
        }
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(root))
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any() && !Redirected(dir)) Directory.Delete(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!Directory.Exists(root)) DropNote(root);
        return new(removed, kept, bytes);
    }

    private static void DropNote(string backupFolder)
    {
        var note = Path.GetFullPath(backupFolder).TrimEnd('\\', '/') + ".wordlyrics.json";
        try { if (File.Exists(note) && !Redirected(note)) File.Delete(note); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static (string? Source, List<string> Songs) Request(string file)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 8 * 1024 * 1024) return (null, []);
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var source = doc.RootElement.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var songs = doc.RootElement.TryGetProperty("songs", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(Path.IsPathFullyQualified).ToList() : [];
            return source is { Length: > 0 } && Path.IsPathFullyQualified(source) ? (Path.GetFullPath(source).TrimEnd('\\', '/'), songs) : (null, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return (null, []); }
    }

    /// <summary>The song exists and is the very same bytes (length, then SHA-256).</summary>
    private static bool Same(string original, string copy, long size)
    {
        try
        {
            if (!File.Exists(original) || string.Equals(Path.GetFullPath(original), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase)) return false;
            if (new FileInfo(original).Length != size) return false;
            return Hash(original).AsSpan().SequenceEqual(Hash(copy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }

    private static bool Inside(string path, string folder)
    {
        var a = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var b = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Redirected(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
}
