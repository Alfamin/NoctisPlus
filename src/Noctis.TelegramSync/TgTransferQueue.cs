namespace Noctis.TelegramSync;

public enum TgTransferResult { Done, Paused, Waiting, Missing, Cancelled }

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

    public DateTime WaitUntilUtc { get; private set; } = DateTime.MinValue;
    public int Count => _queue.Count;

    public void Enqueue(TgMessage message, string targetPath) => _queue.AddLast(new Job(message, targetPath));

    /// <summary>Runs the head job. Done and Missing take it off the queue; every other result keeps it.</summary>
    public async Task<TgTransferResult> RunNextAsync(CancellationToken ct = default)
    {
        if (_queue.First is not { } node) return TgTransferResult.Done;
        if (_now() < WaitUntilUtc) return TgTransferResult.Waiting;
        if (!_mayTransfer()) return TgTransferResult.Paused;
        var job = node.Value;
        var part = job.TargetPath + ".part";
        try
        {
            var msg = job.Message;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using (var file = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None))
                        await drive.DownloadAsync(msg, file, file.Length, null, ct).ConfigureAwait(false);
                    break;
                }
                catch (TelegramFileReferenceExpiredException) when (attempt == 0)
                {
                    var fresh = await drive.GetMessageAsync(msg.Peer, msg.Id, ct).ConfigureAwait(false);
                    if (fresh?.Document is null) { _queue.Remove(node); return TgTransferResult.Missing; }
                    msg = fresh;
                }
            }
            if (job.Message.Document is { Size: > 0 } doc && new FileInfo(part).Length != doc.Size)
            {
                File.Delete(part); // wrong size: start over next time, never keep a corrupt file
                return TgTransferResult.Paused;
            }
            if (File.Exists(job.TargetPath))
            {
                var expected = job.Message.Document?.Size ?? 0;
                if (expected <= 0 || new FileInfo(job.TargetPath).Length == expected)
                {
                    File.Delete(part); // already downloaded (or queued twice): keep the existing file
                    _queue.Remove(node);
                    return TgTransferResult.Done;
                }
                File.Move(part, UniquePath(job.TargetPath), overwrite: false); // a different file holds the name
            }
            else File.Move(part, job.TargetPath, overwrite: false);
            _queue.Remove(node);
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
