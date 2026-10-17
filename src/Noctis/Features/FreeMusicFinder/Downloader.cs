using System.Text.Json;

namespace FreeMusicFinder;

internal enum DownloadOutcome { Saved, AlreadyThere }

/// <summary>Saves tracks into the download folder.</summary>
internal static class Downloader
{
    /// <summary>
    /// The only kinds of file that are ever written. The bot is a third party: whatever else
    /// it sends is refused. A track only learns its extension while it is downloaded, so all
    /// of these are also checked for an earlier copy.
    /// </summary>
    public static readonly IReadOnlyList<string> AudioExtensions = new[] { "flac", "mp3", "m4a", "ogg", "opus", "wav", "aac", "wma", "aiff", "aif", "wv", "ape" };

    // A download in progress. The name is the plugin's own, so that tidying up never touches another program's files.
    private const string PartSuffix = ".fmf.part";
    private static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(10);

    /// <summary>The folder tracks are saved into: the configured one, or <see cref="DefaultFolder"/>.</summary>
    public static string Folder(string? configured, string pluginDataDirectory)
    {
        configured = configured?.Trim();
        return string.IsNullOrEmpty(configured) ? DefaultFolder(pluginDataDirectory) : Environment.ExpandEnvironmentVariables(configured);
    }

    /// <summary>
    /// Where tracks go when no download folder is configured: inside the first Noctis library
    /// folder, where the app's folder watcher adds a file to the library as soon as it is
    /// saved. Without a library folder, under the user's Music folder.
    /// </summary>
    public static string DefaultFolder(string pluginDataDirectory)
    {
        var parent = FirstLibraryFolder(pluginDataDirectory);
        if (parent is null)
        {
            parent = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(parent)) parent = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        return Path.Combine(parent, "Noctis Free Music");
    }

    /// <summary>
    /// The plugin API does not say where the library folders are, so this reads them from
    /// Noctis's own settings file, which sits two levels above the plugin's data folder.
    /// </summary>
    private static string? FirstLibraryFolder(string pluginDataDirectory)
    {
        try
        {
            var settings = Path.Combine(pluginDataDirectory, "..", "..", "settings.json");
            if (!File.Exists(settings)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(settings));
            if (!document.RootElement.TryGetProperty("musicFolders", out var folders) || folders.ValueKind != JsonValueKind.Array) return null;
            return folders.EnumerateArray()
                .Where(f => f.ValueKind == JsonValueKind.String)
                .Select(f => f.GetString())
                .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file names (without extension) for one result list, in its order: "Artist - Title".
    /// Where the list has the same artist and title with different lengths (an album and a live
    /// version), each of those carries its length, "Artist - Title (4.18)", so that one does not
    /// stand in for the other.
    /// </summary>
    public static IReadOnlyList<string> FileNames(IReadOnlyList<BotText.Line> lines)
    {
        var names = lines.Select(l => Sanitize($"{l.Artist} - {l.Title}")).ToArray();
        foreach (var same in Enumerable.Range(0, names.Length).GroupBy(i => names[i], StringComparer.OrdinalIgnoreCase))
        {
            if (same.Select(i => lines[i].Duration).Distinct().Count() < 2) continue;
            foreach (var i in same)
                if (lines[i].Duration is { } length)
                    names[i] += $" ({(length.TotalHours >= 1 ? length.ToString(@"h\.mm\.ss") : length.ToString(@"m\.ss"))})";
        }
        return names;
    }

    /// <summary>The file a track was saved as, or null when it is not in the folder.</summary>
    public static string? FindExisting(string folder, string fileName)
        => AudioExtensions.Select(e => Path.Combine(folder, fileName + "." + e)).FirstOrDefault(File.Exists);

    public static async Task<DownloadOutcome> DownloadAsync(BotTrack track, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(track.FileName) || Path.IsPathRooted(track.FileName) || track.FileName.IndexOfAny(new[]{'/', '\\',':','\0'})>=0 || track.FileName is "." or "..")
            throw new InvalidDataException("UNSAFE_FILE_NAME: the provider did not supply a usable name.");
        Directory.CreateDirectory(folder);
        if (FindExisting(folder, track.FileName) is not null) return DownloadOutcome.AlreadyThere;

        var partial = Path.Combine(folder, track.FileName + PartSuffix);
        try
        {
            string extension;
            long length;
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                extension = await track.Fetch(output, progress, ct).ConfigureAwait(false);
                length = output.Length;
            }
            if (!AudioExtensions.Contains(extension)) throw new InvalidDataException($"The bot sent a \".{extension}\" file, which is not a song.");
            if (length == 0) throw new InvalidDataException("The bot sent an empty file.");
            if(track.Request is not null && extension=="flac")
            {
                using var audio=File.OpenRead(partial);var header=new byte[10];
                var read=audio.Read(header,0,header.Length);
                if(read>=10 && header[0]=='I' && header[1]=='D' && header[2]=='3')
                {
                    if(header.Skip(6).Take(4).Any(b=>(b&128)!=0))throw new InvalidDataException("A malformed audio header was received.");
                    var size=(header[6]<<21)|(header[7]<<14)|(header[8]<<7)|header[9];
                    if(size>16*1024*1024)throw new InvalidDataException("The audio tag is too large.");
                    audio.Position=10+size+((header[5]&16)!=0?10:0);read=audio.Read(header,0,4);
                }
                if(read<4 || header[0]!='f' || header[1]!='L' || header[2]!='a' || header[3]!='C')
                    throw new InvalidDataException("INVALID_AUDIO: the file is labelled FLAC but does not have a FLAC header.");
            }

            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(folder, track.FileName + "." + extension);
            if (File.Exists(target))
            {
                File.Delete(partial);
                return DownloadOutcome.AlreadyThere;
            }
            File.Move(partial, target);
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return DownloadOutcome.Saved;
    }

    /// <summary>
    /// Removes what a download left behind when Noctis was closed or crashed in the middle of
    /// it: this plugin's own part files that nothing has written to for ten minutes.
    /// Returns how many were removed.
    /// </summary>
    public static int CleanLeftovers(string folder)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(folder)) return 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*" + PartSuffix))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < Abandoned) continue;
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return removed;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        // The Windows set is used everywhere so a library moved between systems keeps working.
        var cleaned = new string(name.Select(c => invalid.Contains(c) || "<>:\"/\\|?*".Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        if (cleaned.Length > 150) cleaned = cleaned[..150].TrimEnd().TrimEnd('.');
        return cleaned.Length == 0 ? "track" : cleaned;
    }
}
