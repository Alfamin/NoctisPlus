namespace Noctis.TelegramSync;

/// <summary>A private channel the app owns, found again by the marker in its description (never by title).</summary>
public sealed record TgChannel(long Id, string Title, string Marker);

/// <summary>The stored file behind a message. <see cref="DocumentId"/> is the cross-device song id (<c>tg:&lt;id&gt;</c>).</summary>
public sealed record TgDocument(long DocumentId, string FileName, long Size);

/// <summary>One message as the sync core sees it: where it is, its caption and its document (if any).</summary>
public sealed record TgMessage(long Peer, long Id, string Caption, TgDocument? Document, DateTime DateUtc);

/// <summary>Channel markers: <c>noctis-plus:&lt;library|sync&gt;:v1:&lt;guid&gt;</c>.</summary>
/// <summary>Request ids for posts: the same post retried (after a lost reply or a restart) keeps one id, so
/// Telegram can refuse a second copy while it remembers that id. Not a guarantee of exactly-once.</summary>
public static class TgRequestIds
{
    public static long For(string scope, string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope + "\u001f" + key));
        var id = BitConverter.ToInt64(hash, 0);
        return id == 0 ? 1 : id;
    }
}

public static class TgMarkers
{
    public const string Library = "library", Sync = "sync";
    public static string For(string role, Guid id) => $"noctis-plus:{role}:v1:{id:N}";
    public static bool IsOurs(string? description, string role) =>
        description is not null && description.StartsWith($"noctis-plus:{role}:v1:", StringComparison.Ordinal);
}

/// <summary>
/// The handful of Telegram operations the sync core needs, so the core is testable with an
/// in-memory fake and the real WTelegramClient wrapper (stage 2) stays thin. Implementations
/// report FLOOD_WAIT / FLOOD_PREMIUM_WAIT as <see cref="TelegramFloodWaitException"/> and an
/// expired file reference as <see cref="TelegramFileReferenceExpiredException"/>.
/// </summary>
public interface ITelegramDrive
{
    /// <summary>The signed-in account's user id (two devices on different accounts must refuse to sync).</summary>
    Task<long> GetSelfUserIdAsync(CancellationToken ct = default);

    /// <summary>Find our channel by description marker for <paramref name="role"/>, or create it with <paramref name="markerIfNew"/>.</summary>
    Task<TgChannel> FindOrCreateChannelAsync(string role, string title, string markerIfNew, CancellationToken ct = default);

    /// <summary>Post <paramref name="content"/> as a document with a caption. Streams; never needs the whole file in memory.</summary>
    Task<TgMessage> PostDocumentAsync(long channel, string fileName, Stream content, string caption, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>The same post with a request id that stays the same when this exact post is retried (a lost
    /// reply, a restart), so the service can refuse a second copy. Default: no such protection.</summary>
    Task<TgMessage> PostDocumentAsync(long channel, string fileName, Stream content, string caption, long requestId,
        IProgress<long>? progress = null, CancellationToken ct = default)
        => PostDocumentAsync(channel, fileName, content, caption, progress, ct);

    /// <summary>Messages with id greater than <paramref name="afterId"/>, oldest first, at most <paramref name="limit"/> (one history call).</summary>
    Task<IReadOnlyList<TgMessage>> ReadHistoryAsync(long channel, long afterId, int limit = 100, CancellationToken ct = default);

    /// <summary>Re-read one message (refreshes the file reference). Null when it was deleted.</summary>
    Task<TgMessage?> GetMessageAsync(long peer, long id, CancellationToken ct = default);

    Task<TgMessage?> GetPinnedAsync(long channel, CancellationToken ct = default);
    Task PinAsync(long channel, long id, CancellationToken ct = default);
    Task DeleteMessagesAsync(long channel, IReadOnlyList<long> ids, CancellationToken ct = default);

    /// <summary>Download a message's document into <paramref name="destination"/> starting at byte <paramref name="offset"/> (resume).</summary>
    Task DownloadAsync(TgMessage message, Stream destination, long offset = 0, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>Server-side forward (no re-upload; the document id stays the same).</summary>
    Task<TgMessage> ForwardAsync(long fromPeer, long id, long toChannel, CancellationToken ct = default);
}

/// <summary>FLOOD_WAIT_X / FLOOD_PREMIUM_WAIT_X: wait <see cref="RetryAfter"/>, keep the queue, never spin.</summary>
public sealed class TelegramFloodWaitException(TimeSpan retryAfter, bool premium = false)
    : Exception($"{(premium ? "FLOOD_PREMIUM_WAIT" : "FLOOD_WAIT")}_{(int)retryAfter.TotalSeconds}")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
    public bool Premium { get; } = premium;
}

/// <summary>FILE_REFERENCE_EXPIRED: re-read the message (<see cref="ITelegramDrive.GetMessageAsync"/>) and try again.</summary>
public sealed class TelegramFileReferenceExpiredException(long peer, long id)
    : Exception($"FILE_REFERENCE_EXPIRED {peer}/{id}")
{
    public long Peer { get; } = peer;
    public long MessageId { get; } = id;
}
