namespace Noctis.TelegramSync;

public enum TgTransferResult { Done, Paused, Waiting, Missing, Cancelled, Failed }

/// <summary>
/// Paced background downloads, one at a time: streams into <c>&lt;target&gt;.part</c> (resumed
/// from its length after a drop or a cancel), checks the size, then renames atomically, so a
/// half file never lands in the music folder. Honours FLOOD_WAIT (the job stays queued until
/// the retry-after passes), re-reads the message on an expired file reference, and asks the
/// injected policy (Wi-Fi / charging) before each start. Uploads are stage 2 (same shape).
/// </summary>
public sealed class TgTransferQueue(ITelegramDrive drive, Func<bool>? mayTransfer = null, Func<DateTime>? now = null)
{
    public sealed record Job(TgMessage Message, string TargetPath);

    private readonly LinkedList<Job> _queue = new();
    private readonly Func<bool> _mayTransfer = mayTransfer ?? (() => true);
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<Job, int> _badSizes = new();

    public DateTime WaitUntilUtc { get; private set; } = DateTime.MinValue;
    public int Count { get { lock (_gate) return _queue.Count; } }
    public string? LastSavedPath { get; private set; }
    public string? LastError { get; private set; }

    public void Enqueue(TgMessage message, string targetPath)
    {
        var job = new Job(message, Path.GetFullPath(targetPath));
        lock (_gate)
            if (!_queue.Contains(job)) _queue.AddLast(job);
    }

    /// <summary>Runs the head job. Done and Missing take it off the queue; every other result keeps it.</summary>
    public async Task<TgTransferResult> RunNextAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return TgTransferResult.Cancelled;
        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false)) return TgTransferResult.Paused;
        try { return await RunCoreAsync(ct).ConfigureAwait(false); }
        finally { _runGate.Release(); }
    }

    private void Remove(LinkedListNode<Job> node)
    { lock (_gate) { _queue.Remove(node); _badSizes.Remove(node.Value); } }

    private async Task<TgTransferResult> RunCoreAsync(CancellationToken ct)
    {
        LinkedListNode<Job>? node;
        lock (_gate) node = _queue.First;
        if (node is null) return TgTransferResult.Done;
        if (_now() < WaitUntilUtc) return TgTransferResult.Waiting;
        if (!_mayTransfer()) return TgTransferResult.Paused;
        var job = node.Value;
        LastSavedPath = null; LastError = null;
        var part = job.TargetPath + ".part";
        try
        {
            var msg = job.Message;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using (var file = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                    {
                        // Telegram offsets must be 4 KiB aligned. Preserve completed chunks only.
                        file.SetLength(file.Length / 4096 * 4096);
                        file.Position = file.Length;
                        await drive.DownloadAsync(msg, file, file.Length, null, ct).ConfigureAwait(false);
                    }
                    break;
                }
                catch (TelegramFileReferenceExpiredException) when (attempt == 0)
                {
                    var fresh = await drive.GetMessageAsync(msg.Peer, msg.Id, ct).ConfigureAwait(false);
                    if (fresh?.Document is null) { Remove(node); return TgTransferResult.Missing; }
                    msg = fresh;
                }
            }
            if (msg.Document is { Size: > 0 } doc && new FileInfo(part).Length != doc.Size)
            {
                File.Delete(part); // wrong size: start over next time, never keep a corrupt file
                var attempts = _badSizes.GetValueOrDefault(job) + 1;
                _badSizes[job] = attempts;
                LastError = "Downloaded size does not match";
                if (attempts < 2) return TgTransferResult.Paused;
                Remove(node);
                return TgTransferResult.Failed;
            }
            if (File.Exists(job.TargetPath))
            {
                var expected = msg.Document?.Size ?? 0;
                if (expected > 0 && new FileInfo(job.TargetPath).Length == expected)
                {
                    File.Delete(part); // already downloaded (or queued twice): keep the existing file
                    LastSavedPath = job.TargetPath;
                    Remove(node);
                    return TgTransferResult.Done;
                }
                var unique = UniquePath(job.TargetPath);
                File.Move(part, unique, overwrite: false); // a different file holds the name
                LastSavedPath = unique;
            }
            else { File.Move(part, job.TargetPath, overwrite: false); LastSavedPath = job.TargetPath; }
            Remove(node);
            return TgTransferResult.Done;
        }
        catch (TelegramFloodWaitException e)
        {
            WaitUntilUtc = _now() + e.RetryAfter;
            return TgTransferResult.Waiting;
        }
        catch (OperationCanceledException)
        {
            return TgTransferResult.Cancelled; // the .part stays; the next run resumes from it
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = "Download paused. Check storage and the connection";
            return TgTransferResult.Paused;
        }
    }

    private static string UniquePath(string target)
    {
        var dir = Path.GetDirectoryName(target) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
