using System.Text.Json;
using System.Text.Encodings.Web;

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
    public sealed record Job(string SourcePath, string FileName, IReadOnlyDictionary<string, string> Meta)
    {
        internal string Identity { get; init; } = "";
    }

    private readonly LinkedList<Job> _queue = new();
    private readonly Func<bool> _mayTransfer = mayTransfer ?? (() => true);
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _gate = new();
    private readonly HashSet<string> _known = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions CaptionJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public TimeSpan MinGap { get; init; } = TimeSpan.FromSeconds(2);

    public DateTime WaitUntilUtc { get; private set; } = DateTime.MinValue;
    public int Count { get { lock (_gate) return _queue.Count; } }
    public TgMessage? LastPosted { get; private set; }
    public string? LastError { get; private set; }

    public void Enqueue(string sourcePath, IReadOnlyDictionary<string, string> meta, string? fileName = null)
    {
        var path = Path.GetFullPath(sourcePath);
        var file = new FileInfo(path);
        var identity = path + "\u001f" + (file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing");
        lock (_gate)
            if (_known.Add(identity)) _queue.AddLast(new Job(path, fileName ?? Path.GetFileName(path), new Dictionary<string, string>(meta)) { Identity = identity });
    }

    public static string Caption(IReadOnlyDictionary<string, string> meta) => JsonSerializer.Serialize(meta, CaptionJson);

    /// <summary>Runs the head job. Done and Missing take it off the queue; every other result keeps it.</summary>
    public async Task<TgTransferResult> RunNextAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return TgTransferResult.Cancelled;
        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false)) return TgTransferResult.Paused;
        try { return await RunCoreAsync(ct).ConfigureAwait(false); }
        finally { _runGate.Release(); }
    }

    private void Remove(LinkedListNode<Job> node, bool keepKnown = false)
    {
        lock (_gate) { _queue.Remove(node); if (!keepKnown) _known.Remove(node.Value.Identity); }
    }

    private async Task<TgTransferResult> RunCoreAsync(CancellationToken ct)
    {
        LinkedListNode<Job>? node;
        lock (_gate) node = _queue.First;
        if (node is null) return TgTransferResult.Done;
        if (_now() < WaitUntilUtc) return TgTransferResult.Waiting;
        if (!_mayTransfer()) return TgTransferResult.Paused;
        var job = node.Value;
        LastError = null;
        if (!File.Exists(job.SourcePath)) { Remove(node); return TgTransferResult.Missing; }
        var caption = Caption(job.Meta);
        if (caption.Length > 1024)
        {
            LastError = "Song details are too long to upload";
            Remove(node);
            return TgTransferResult.Failed;
        }
        // Pace attempts as well as successes; a failing route must never become a hot loop.
        WaitUntilUtc = _now() + MinGap;
        try
        {
            await using var file = new FileStream(job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            LastPosted = await drive.PostDocumentAsync(libraryChannel, job.FileName, file, caption, null, ct).ConfigureAwait(false);
            Remove(node, keepKnown: true);
            return TgTransferResult.Done;
        }
        catch (TelegramFloodWaitException e)
        {
            WaitUntilUtc = _now() + (e.RetryAfter > MinGap ? e.RetryAfter : MinGap);
            return TgTransferResult.Waiting;
        }
        catch (OperationCanceledException)
        {
            return TgTransferResult.Cancelled;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { LastError = "File is no longer available"; Remove(node); return TgTransferResult.Missing; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LastError = "Upload paused. Check the connection and file access"; return TgTransferResult.Paused; }
    }
}

/// <summary>Server-side forwards paced to at most one per <see cref="MinGap"/> (default 2 s); honours flood waits.</summary>
public sealed class TgForwardPacer(ITelegramDrive drive, Func<DateTime>? now = null, TimeSpan? minGap = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    public TimeSpan MinGap { get; } = minGap ?? TimeSpan.FromSeconds(2);
    public DateTime NextAllowedUtc { get; private set; } = DateTime.MinValue;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>Forwards when allowed; returns null (and does nothing) while paced or waiting.</summary>
    public async Task<TgMessage?> TryForwardAsync(long fromPeer, long id, long toChannel, CancellationToken ct = default)
    {
        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        try {
        if (_now() < NextAllowedUtc) return null;
        NextAllowedUtc = _now() + MinGap;
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
        } finally { _runGate.Release(); }
    }
}
