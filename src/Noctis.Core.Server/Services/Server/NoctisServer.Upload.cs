using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Noctis.Models;

namespace Noctis.Services.Server;

/// <summary>
/// Songs sent from a signed-in phone to this computer ("Send phone songs to computer"):
/// <list type="bullet">
/// <item><c>noctisHasTracks</c> (form POST, repeated <c>title</c>/<c>artist</c>/<c>duration</c>
/// paired by position, at most <see cref="MaxHasTracksItems"/>): which of those songs the library
/// already has, so the phone only sends the rest. Answer <c>noctisHasTracks: { have: [bool…] }</c>.</item>
/// <item><c>noctisUploadTrack?name=…</c> (POST, the file as the raw body, at most
/// <see cref="MaxUploadBytes"/>): the body is streamed to a temp file beside its destination while
/// it is hashed, then moved under the library's upload root (the desktop's "Noctis Imports") in a
/// folder named after the device, and added to the library like a dropped file. Answer
/// <c>noctisUpload: { status: "added" | "exists", name }</c>: "exists" when a file with the same
/// bytes is already there or in the library (nothing is written).</item>
/// </list>
/// Both need the phone's own device key and Sync on (the same gate as the sync endpoints). The
/// request names nothing but a file name, which is reduced to a plain allow-listed audio name:
/// no folder, no reserved device name, never an existing file overwritten.
/// </summary>
public sealed partial class NoctisServer
{
    /// <summary>Largest song a phone may send. A 24-bit/192 kHz FLAC album track is ~300 MB.</summary>
    public const long MaxUploadBytes = 500L * 1024 * 1024;

    /// <summary>The cap in force (<see cref="MaxUploadBytes"/>; tests lower it).</summary>
    internal long UploadLimitBytes { get; set; } = MaxUploadBytes;

    /// <summary>Most songs one noctisHasTracks call may ask about: three form values each, under
    /// the form reader's 1024-value limit.</summary>
    public const int MaxHasTracksItems = 300;

    /// <summary>Two songs whose title and artist match are the same song when their lengths differ
    /// by at most this (a FLAC on the PC and an MP3 of it on the phone differ by a frame or two).</summary>
    private const int SameSongSeconds = 2;

    private const string UploadTempPrefix = ".noctis-upload-";

    /// <summary>One dedupe-and-move at a time, so two copies of one song sent at once cannot both
    /// find nothing and both land.</summary>
    private readonly SemaphoreSlim _uploadGate = new(1, 1);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Sync on, and the caller is a signed-in device (its folder is named after it).</summary>
    private ServerDevice RequireSyncDevice(HttpContext ctx)
    {
        RequireSync();
        return ctx.Items["device"] as ServerDevice
            ?? throw new SubsonicException(SubsonicResponse.ErrAuthMechanismNotSupported, "Send songs with the key this device signed in with.");
    }

    private async Task<JsonObject> HasTracksAsync(Params p, HttpContext ctx)
    {
        RequireSyncDevice(ctx);
        var titles = p.All("title").ToList();
        var artists = p.All("artist").ToList();
        var durations = p.All("duration").ToList();
        if (titles.Count > MaxHasTracksItems)
            throw new SubsonicException(SubsonicResponse.ErrGeneric, $"At most {MaxHasTracksItems} songs per call");

        var snap = Playable(await _library.SnapshotAsync().ConfigureAwait(false));
        var byName = snap.Tracks.ToLookup(t => SongKey(t.Title, t.Artist), t => (int)t.Duration.TotalSeconds, StringComparer.OrdinalIgnoreCase);
        var have = new JsonArray();
        for (var i = 0; i < titles.Count; i++)
        {
            var artist = i < artists.Count ? artists[i] : string.Empty;
            var seconds = i < durations.Count && int.TryParse(durations[i], out var d) ? d : 0;
            var key = SongKey(titles[i], artist);
            // An unknown length on either side matches on title and artist alone.
            have.Add(key.Length > 1 && byName[key].Any(s => seconds <= 0 || s <= 0 || Math.Abs(s - seconds) <= SameSongSeconds));
        }
        return new JsonObject { ["noctisHasTracks"] = new JsonObject { ["have"] = have } };
    }

    private static string SongKey(string? title, string? artist) => $"{title?.Trim()}\n{artist?.Trim()}";

    private async Task<JsonObject> UploadTrackAsync(Params p, HttpContext ctx)
    {
        var device = RequireSyncDevice(ctx);
        var name = UploadFileName(p.Get("name") ?? throw Missing("name"))
            ?? throw new SubsonicException(SubsonicResponse.ErrMissingParameter, "Parameter 'name' must be the file name of a song this computer can play");
        if (!HttpMethods.IsPost(ctx.Request.Method) || ctx.Request.HasFormContentType)
            throw new SubsonicException(SubsonicResponse.ErrGeneric, "Send the song as the body of a POST.");
        if (ctx.Request.ContentLength > UploadLimitBytes) throw UploadTooLarge();
        var ct = ctx.RequestAborted;

        var root = await _library.UploadRootAsync(ct).ConfigureAwait(false)
            ?? throw new SubsonicException(SubsonicResponse.ErrGeneric, "This computer doesn't take songs from phones.");
        var folder = Path.GetFullPath(Path.Combine(root, UploadFolderName(device.DeviceName)));
        if (!string.Equals(Path.GetDirectoryName(folder), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase))
            throw new SubsonicException(SubsonicResponse.ErrGeneric, "This computer doesn't take songs from phones.");
        Directory.CreateDirectory(folder);

        // The server-wide 1 MB body limit is for form posts; this body is a song. Kestrel's limit is
        // only the backstop, with a margin: it counts a chunked body's framing too (a chunked body
        // of exactly the cap was refused in a test). The cap itself is the song's bytes, counted below.
        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = UploadLimitBytes + UploadLimitBytes / 256 + 64 * 1024;

        // Beside the destination (same volume: the move is a rename) under a name no scan or
        // watcher takes for a song (not an audio extension).
        var temp = Path.Combine(folder, UploadTempPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long length = 0;
            byte[] digest;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    while (true)
                    {
                        int read;
                        try { read = await ctx.Request.Body.ReadAsync(buffer, ct).ConfigureAwait(false); }
                        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { throw UploadTooLarge(); }
                        if (read == 0) break;
                        length += read;
                        if (length > UploadLimitBytes) throw UploadTooLarge();
                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                }
                digest = hash.GetHashAndReset();
            }
            if (length == 0) throw new SubsonicException(SubsonicResponse.ErrMissingParameter, "The song arrived empty.");

            string destination;
            await _uploadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var snap = await _library.SnapshotAsync().ConfigureAwait(false);
                if (FindSameFile(folder, snap, length, digest) is { } existing)
                {
                    DebugLogger.Info(DebugLogger.Category.State, "Server", $"upload from {device.DeviceName}: already here");
                    return UploadAnswer("exists", Path.GetFileName(existing));
                }
                destination = MoveUnique(temp, folder, name);
            }
            finally { _uploadGate.Release(); }

            await _library.ImportUploadedAsync(destination, ct).ConfigureAwait(false);
            DebugLogger.Info(DebugLogger.Category.State, "Server", $"upload from {device.DeviceName}: added ({length / 1024} KB)");
            return UploadAnswer("added", Path.GetFileName(destination));
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort: a leftover .tmp is never scanned */ }
        }
    }

    private static JsonObject UploadAnswer(string status, string name)
        => new() { ["noctisUpload"] = new JsonObject { ["status"] = status, ["name"] = name } };

    private SubsonicException UploadTooLarge()
        => new(SubsonicResponse.ErrGeneric, $"Songs up to {UploadLimitBytes / (1024 * 1024)} MB can be sent.");

    /// <summary>
    /// A file already on this computer with exactly these bytes: one in the device's folder, or a
    /// library song. Only files of the same length are hashed, so this costs nothing unless a
    /// same-size file exists (then one read of it).
    /// </summary>
    private static string? FindSameFile(string folder, LibrarySnapshot snap, long length, byte[] digest)
    {
        var candidates = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
                if (!Path.GetFileName(file).StartsWith(UploadTempPrefix, StringComparison.Ordinal)) candidates.Add(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        candidates.AddRange(snap.Tracks.Where(t => t.FileSize == length && !string.IsNullOrEmpty(t.FilePath)).Select(t => t.FilePath));

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != length) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), digest)) return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Moves the temp file to <paramref name="name"/> in the folder, or "name (2)",
    /// "name (3)"… when taken: never over an existing file (the move itself refuses to).</summary>
    private static string MoveUnique(string temp, string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 1; ; n++)
        {
            var destination = Path.Combine(folder, n == 1 ? name : $"{stem} ({n}){extension}");
            if (File.Exists(destination) || Directory.Exists(destination)) continue;
            try
            {
                File.Move(temp, destination, overwrite: false);
                return destination;
            }
            catch (IOException) when (n < 1000 && (File.Exists(destination) || Directory.Exists(destination)))
            {
                // Taken between the check and the move: the next number.
            }
        }
    }

    /// <summary>
    /// The phone's file name as a plain file name for this computer, or null when it is not an
    /// audio file the desktop plays (<see cref="MetadataService.SupportedExtensions"/>). Only the
    /// last path segment counts (so "../x.mp3" is "x.mp3"); characters a file name cannot hold
    /// on Windows become '_'; leading dots (hidden files, our temp names), trailing dots and
    /// spaces go; a reserved device name ("CON.mp3") gets a leading '_'.
    /// </summary>
    internal static string? UploadFileName(string? raw)
    {
        var last = (raw ?? string.Empty).Split('/', '\\').Last();
        var extension = Path.GetExtension(last).ToLowerInvariant();
        if (extension.Length < 2 || !MetadataService.SupportedExtensions.Contains(extension)) return null;
        var stem = SafeName(last[..^extension.Length], 120) ?? "Song";
        return stem + extension;
    }

    /// <summary>The device's folder under the upload root: its name made safe, else "Phone".</summary>
    internal static string UploadFolderName(string? deviceName) => SafeName(deviceName, 64) ?? "Phone";

    private static readonly char[] WindowsInvalidNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static string? SafeName(string? raw, int maxLength)
    {
        var chars = (raw ?? string.Empty)
            .Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)
            .Select(c => char.IsControl(c) || WindowsInvalidNameChars.Contains(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray();
        var name = new string(chars).Trim().TrimStart('.').TrimEnd('.', ' ').Trim();
        if (name.Length > maxLength) name = name[..maxLength].TrimEnd('.', ' ');
        if (name.Length == 0) return null;
        // "CON", "con.backup", "COM1 " are all the console device on Windows.
        var device = name.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(device) ? "_" + name : name;
    }
}
