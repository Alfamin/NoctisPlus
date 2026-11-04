using System.Text.Json;

namespace Noctis.TelegramSync;

/// <summary>
/// Paced background uploads to the Library channel, one at a time. Each song is posted as a
/// document whose caption is the song's JSON metadata. Resumable: a cancel, a drop or a flood
/// wait keeps the job at the head of the queue, so the next run starts it again (the real
/// drive resumes the parts it already sent; the fake re-sends). Honours typed retry-after
/// (FLOOD_WAIT / FLOOD_PREMIUM_WAIT) by waiting, never spinning, and asks the injected policy
/// (Wi-Fi / charging) before each start.
/// </summary>
public sealed class TgUploadQueue(ITelegramDrive drive, long libraryChannel, Func<bool>? mayTransfer = null, Func<DateTime>? now = null)
{
    public sealed record Job(string SourcePath, string FileName, IReadOnlyDictionary<string, string> Meta);

    private readonly LinkedList<Job> _queue = new();
    private readonly Func<bool> _mayTransfer = mayTransfer ?? (() => true);
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    public DateTime WaitUntilUtc { get; private set; } = DateTime.MinValue;
    public int Count => _queue.Count;
    public TgMessage? LastPosted { get; private set; }

    public void Enqueue(string sourcePath, IReadOnlyDictionary<string, string> meta, string? fileName = null) =>
        _queue.AddLast(new Job(sourcePath, fileName ?? Path.GetFileName(sourcePath), meta));

    public static string Caption(IReadOnlyDictionary<string, string> meta) => JsonSerializer.Serialize(meta);

    /// <summary>Runs the head job. Done and Missing take it off the queue; every other result keeps it.</summary>
    public async Task<TgTransferResult> RunNextAsync(CancellationToken ct = default)
    {
        if (_queue.First is not { } node) return TgTransferResult.Done;
        if (_now() < WaitUntilUtc) return TgTransferResult.Waiting;
        if (!_mayTransfer()) return TgTransferResult.Paused;
        var job = node.Value;
        if (!File.Exists(job.SourcePath)) { _queue.Remove(node); return TgTransferResult.Missing; }
        try
        {
            await using var file = new FileStream(job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            LastPosted = await drive.PostDocumentAsync(libraryChannel, job.FileName, file, Caption(job.Meta), null, ct).ConfigureAwait(false);
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
            return TgTransferResult.Cancelled;
        }
    }
}

/// <summary>Server-side forwards paced to at most one per <see cref="MinGap"/> (default 2 s); honours flood waits.</summary>
public sealed class TgForwardPacer(ITelegramDrive drive, Func<DateTime>? now = null, TimeSpan? minGap = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    public TimeSpan MinGap { get; } = minGap ?? TimeSpan.FromSeconds(2);
    public DateTime NextAllowedUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Forwards when allowed; returns null (and does nothing) while paced or waiting.</summary>
    public async Task<TgMessage?> TryForwardAsync(long fromPeer, long id, long toChannel, CancellationToken ct = default)
    {
        if (_now() < NextAllowedUtc) return null;
        try
        {
            var msg = await drive.ForwardAsync(fromPeer, id, toChannel, ct).ConfigureAwait(false);
            NextAllowedUtc = _now() + MinGap;
            return msg;
        }
        catch (TelegramFloodWaitException e)
        {
            NextAllowedUtc = _now() + (e.RetryAfter > MinGap ? e.RetryAfter : MinGap);
            return null;
        }
    }
}
