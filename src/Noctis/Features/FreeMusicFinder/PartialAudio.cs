namespace FreeMusicFinder;

/// <summary>
/// Owner #100: a download stopped part way (skipped past in the queue, held back for a newer Play) keeps the bytes it had,
/// like a streaming app's cache, and a later download of the same file goes on from there. Kept per Telegram file (id and
/// size), next to the stream cache; old ones are removed.
/// </summary>
internal static class PartialAudio
{
    /// <summary>What a running download knows about its transfer (set by <see cref="Downloader"/>, filled by the transfer).</summary>
    internal sealed class Slot
    {
        public long DocumentId { get; set; }
        public long Size { get; set; }
        /// <summary>Bytes from the start written without a gap.</summary>
        public long Prefix { get; set; }
    }

    internal static readonly AsyncLocal<Slot?> Current = new();
    private const long MinimumKept = AudioFileTransfer.ChunkSize; // every fully validated chunk is reusable

    /// <summary>Null: nothing is kept (no stream cache).</summary>
    public static string? Folder { get; set; }

    private static string? PathOf(long id, long size) => Folder is { } folder ? Path.Combine(folder, $"{id}-{size}.part") : null;

    /// <summary>Writes the kept bytes of this file into <paramref name="output"/> (from its current position) and returns how
    /// many; the kept copy is removed (a new one is made if this transfer is stopped too).</summary>
    public static async Task<long> RestoreAsync(long id, long size, Stream output, CancellationToken ct)
    {
        if (PathOf(id, size) is not { } path || !File.Exists(path)) return 0;
        var at = output.Position;
        try
        {
            long length;
            await using (var kept = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                length = Math.Min(kept.Length, size) / AudioFileTransfer.ChunkSize * AudioFileTransfer.ChunkSize;
                if (length <= 0) return 0;
                var buffer = new byte[81920]; long copied = 0;
                while (copied < length)
                {
                    var read = await kept.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;
                }
                if (copied != length) throw new IOException("The kept part of the song was shorter than expected.");
            }
            Delete(path);
            return length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A kept part that cannot be read is simply downloaded again, from the start (every byte is written again).
            Delete(path);
            Noctis.Services.DebugLog.Write("Finder", "a kept part of a song could not be used: " + ex.Message);
            output.Position = at;
            return 0;
        }
    }

    /// <summary>The download was stopped: the unbroken start of <paramref name="partialFile"/> is kept for later.</summary>
    public static void Keep(string partialFile, Slot? slot)
    {
        if (slot is not { DocumentId: not 0, Prefix: >= MinimumKept } || slot.Prefix >= slot.Size || PathOf(slot.DocumentId, slot.Size) is not { } path) return;
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temp = path + ".tmp";
            using (var source = new FileStream(partialFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (source.Length < slot.Prefix) return;
                var buffer = new byte[81920]; long left = slot.Prefix;
                while (left > 0)
                {
                    var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read == 0) return;
                    target.Write(buffer, 0, read); left -= read;
                }
            }
            File.Move(temp, path, overwrite: true); temp = null;
            Noctis.Services.DebugLog.Write("Finder", $"kept {slot.Prefix * 100 / slot.Size}% of a stopped download to go on later");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { if (temp is not null) Delete(temp); }
    }

    /// <summary>Partial files obey the same age and space limits as completed stream files.</summary>
    public static void Clean(int? days = 30, long? maxBytes = null)
    {
        try
        {
            if (Folder is not { } folder || !Directory.Exists(folder)) return;
            StreamCache.Purge(folder, days, DateTime.UtcNow, maxBytes: maxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Delete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}
