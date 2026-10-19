namespace FreeMusicFinder;

/// <summary>
/// Owner #87: a song that is only played (streamed) is kept here, outside every library folder, so it never joins Songs
/// by itself. Replaying it is instant; "Add to songs" copies it into the download folder; songs not played for the chosen
/// number of days are deleted.
/// </summary>
internal static class StreamCache
{
    internal const string SettingKey = "streamCacheDays", DefaultChoice = "30 days";
    internal static readonly string[] Choices = ["7 days", "30 days", "90 days", "Forever"];

    /// <summary>Beside the feature's data, except that a profile in the roaming AppData folder keeps its cache in the local
    /// one: gigabytes of audio must never travel with a roaming Windows profile.</summary>
    public static string Folder(string pluginDataDirectory)
    {
        var data = Path.GetFullPath(pluginDataDirectory);
        var roaming = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (roaming.Length > 0 && local.Length > 0 && data.StartsWith(roaming + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(local, data[(roaming.Length + 1)..], "stream-cache");
        return Path.Combine(data, "stream-cache");
    }

    /// <summary>Days a streamed song is kept after it was last played; null = forever.</summary>
    public static int? Days(string? choice) => choice?.Trim() switch
    {
        "7 days" => 7,
        "90 days" => 90,
        "Forever" => null,
        _ => 30,
    };

    public static bool Contains(string cacheFolder, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheFolder)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Played again: its days start over.</summary>
    public static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal const long NormalCap = 20L * 1024 * 1024 * 1024, LowDiskCap = 1L * 1024 * 1024 * 1024, LowDisk = 3L * 1024 * 1024 * 1024;

    /// <summary>Most the cache may hold: 20 GB with a day limit, no size limit for Forever, but never more than 1 GB once
    /// the drive has under 3 GB free (streaming must never fill the disk).</summary>
    public static long? Cap(int? days, long freeBytes) => freeBytes < LowDisk ? LowDiskCap : days is null ? null : NormalCap;

    public static long FreeSpace(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return long.MaxValue; }
    }

    /// <summary>Deletes cached songs not played for <paramref name="days"/> days, then the least recently played ones while
    /// the cache is over <paramref name="maxBytes"/>. A file in use (playing, still being written) or named by
    /// <paramref name="keep"/> stays. Returns how many files were deleted.</summary>
    public static int Purge(string cacheFolder, int? days, DateTime nowUtc, Func<string, bool>? keep = null, long? maxBytes = null)
    {
        if (!Directory.Exists(cacheFolder)) return 0;
        var files = new List<(FileInfo Info, DateTime Last)>();
        foreach (var file in Directory.EnumerateFiles(cacheFolder))
        {
            try
            {
                var info = new FileInfo(file);
                files.Add((info, info.LastWriteTimeUtc > info.LastAccessTimeUtc ? info.LastWriteTimeUtc : info.LastAccessTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var removed = 0;
        var left = new List<(FileInfo Info, DateTime Last)>();
        foreach (var file in files)
        {
            if (days is { } limit && nowUtc - file.Last >= TimeSpan.FromDays(limit) && TryDelete(file.Info, keep)) removed++;
            else left.Add(file);
        }
        if (maxBytes is { } cap)
        {
            var total = left.Sum(f => SafeLength(f.Info));
            foreach (var file in left.OrderBy(f => f.Last))
            {
                if (total <= cap) break;
                var length = SafeLength(file.Info);
                if (TryDelete(file.Info, keep)) { removed++; total -= length; }
            }
        }
        return removed;
    }

    private static long SafeLength(FileInfo info)
    {
        try { info.Refresh(); return info.Exists ? info.Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool TryDelete(FileInfo info, Func<string, bool>? keep)
    {
        if (keep?.Invoke(info.FullName) == true) return false;
        try
        {
            // Open alone first: a file the player or a download still holds is left for next time.
            using (new FileStream(info.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            info.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Copies a cached song (and lyrics saved beside it under the same name) into the library folder.
    /// An existing file of that name is kept, not overwritten. Returns the library path.</summary>
    public static string CopyToLibrary(string cached, string libraryFolder)
    {
        Directory.CreateDirectory(libraryFolder);
        var target = Path.Combine(libraryFolder, Path.GetFileName(cached));
        var name = Path.GetFileNameWithoutExtension(cached);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(cached)!, name + ".*"))
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase)) continue;
            var copy = Path.Combine(libraryFolder, Path.GetFileName(file));
            if (File.Exists(copy)) continue;
            var temp = copy + ".adding";
            // The song may be playing: read while the player also reads it, and appear under its name only when whole.
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            File.Move(temp, copy, overwrite: false);
        }
        return target;
    }
}
