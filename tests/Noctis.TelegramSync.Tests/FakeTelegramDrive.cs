using Noctis.TelegramSync;

namespace Noctis.TelegramSync.Tests;

/// <summary>
/// One in-memory Telegram account shared by every fake device. Deterministic; can inject flood
/// waits, duplicated history pages, deleted messages and expired file references.
/// </summary>
public sealed class FakeTelegramDrive : ITelegramDrive
{
    private sealed record Stored(TgMessage Message, byte[] Data);
    private sealed record Channel(TgChannel Info, List<Stored> Messages) { public long? Pinned; }

    private readonly Dictionary<long, Channel> _channels = new();
    private readonly object _gate = new();
    private long _nextChannel = -1001, _nextMessage = 1, _nextDocument = 5_000_000_000;

    public long SelfUserId { get; set; } = 42;
    /// <summary>The next N posts fail with FLOOD_WAIT of <see cref="FloodWait"/>.</summary>
    public int FailNextPosts { get; set; }
    public TimeSpan FloodWait { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>History pages repeat their first message (a read seen twice).</summary>
    public bool DuplicateReads { get; set; }
    /// <summary>Messages whose next download fails with FILE_REFERENCE_EXPIRED (cleared by a re-read).</summary>
    public HashSet<long> ExpiredReferences { get; } = new();
    /// <summary>The next N downloads fail with FLOOD_PREMIUM_WAIT of <see cref="FloodWait"/>.</summary>
    public int FailNextDownloads { get; set; }
    public int Posts { get; private set; }
    public int HistoryCalls { get; private set; }

    public bool Revoked { get; set; }
    private Channel Ch(long id) { if (Revoked) throw new TelegramSessionRevokedException(); return _channels.TryGetValue(id, out var c) ? c : throw new TelegramChannelGoneException(id); }
    public Task<long> GetSelfUserIdAsync(CancellationToken ct = default) => Revoked ? throw new TelegramSessionRevokedException() : Task.FromResult(SelfUserId);

    public Task<TgChannel> FindOrCreateChannelAsync(string role, string title, string markerIfNew, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var found = _channels.Values.FirstOrDefault(c => TgMarkers.IsOurs(c.Info.Marker, role));
            if (found is not null) return Task.FromResult(found.Info);
            var info = new TgChannel(_nextChannel--, title, markerIfNew);
            _channels[info.Id] = new Channel(info, new List<Stored>());
            return Task.FromResult(info);
        }
    }

    public void DeleteChannel(long id) { lock (_gate) _channels.Remove(id); }

    public async Task<TgMessage> PostDocumentAsync(long channel, string fileName, Stream content, string caption, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailNextPosts > 0) { FailNextPosts--; throw new TelegramFloodWaitException(FloodWait); }
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        lock (_gate)
        {
            var c = Ch(channel);
            var msg = new TgMessage(channel, _nextMessage++, caption, new TgDocument(_nextDocument++, fileName, ms.Length), DateTime.UtcNow);
            c.Messages.Add(new Stored(msg, ms.ToArray()));
            Posts++;
            progress?.Report(ms.Length);
            return msg;
        }
    }

    public Task<IReadOnlyList<TgMessage>> ReadHistoryAsync(long channel, long afterId, int limit = 100, CancellationToken ct = default)
    {
        lock (_gate)
        {
            HistoryCalls++;
            var page = Ch(channel).Messages.Where(m => m.Message.Id > afterId).Take(limit).Select(m => m.Message).ToList();
            if (DuplicateReads && page.Count > 0 && page.Count < limit) page.Insert(page.Count, page[0]);
            return Task.FromResult<IReadOnlyList<TgMessage>>(page);
        }
    }

    public Task<TgMessage?> GetMessageAsync(long peer, long id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ExpiredReferences.Remove(id);
            return Task.FromResult(Find(peer, id)?.Message);
        }
    }

    public Task<TgMessage?> GetPinnedAsync(long channel, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var c = Ch(channel);
            return Task.FromResult(c.Pinned is { } p ? Find(channel, p)?.Message : null);
        }
    }

    public Task PinAsync(long channel, long id, CancellationToken ct = default)
    {
        lock (_gate) Ch(channel).Pinned = id;
        return Task.CompletedTask;
    }

    public Task DeleteMessagesAsync(long channel, IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        lock (_gate) Ch(channel).Messages.RemoveAll(m => ids.Contains(m.Message.Id));
        return Task.CompletedTask;
    }

    /// <summary>Delete every message older than <paramref name="id"/> (old posts gone after a snapshot).</summary>
    public void DeleteBefore(long channel, long id) { lock (_gate) Ch(channel).Messages.RemoveAll(m => m.Message.Id < id); }

    /// <summary>Called after each chunk is written; a test can cancel mid-transfer.</summary>
    public Action<long>? OnChunk { get; set; }
    public int ChunkSize { get; set; } = 64 * 1024;

    public async Task DownloadAsync(TgMessage message, Stream destination, long offset = 0, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        byte[] data;
        lock (_gate)
        {
            if (FailNextDownloads > 0) { FailNextDownloads--; throw new TelegramFloodWaitException(FloodWait, premium: true); }
            if (ExpiredReferences.Contains(message.Id)) throw new TelegramFileReferenceExpiredException(message.Peer, message.Id);
            data = Find(message.Peer, message.Id)?.Data ?? throw new InvalidOperationException("MESSAGE_ID_INVALID");
        }
        for (var at = offset; at < data.Length; at += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var n = (int)Math.Min(ChunkSize, data.Length - at);
            await destination.WriteAsync(data.AsMemory((int)at, n), ct);
            progress?.Report(at + n);
            OnChunk?.Invoke(at + n);
        }
    }

    public Task<TgMessage> ForwardAsync(long fromPeer, long id, long toChannel, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var src = Find(fromPeer, id) ?? throw new InvalidOperationException("MESSAGE_ID_INVALID");
            var msg = src.Message with { Peer = toChannel, Id = _nextMessage++ };
            _channels[toChannel].Messages.Add(new Stored(msg, src.Data));
            return Task.FromResult(msg);
        }
    }

    /// <summary>Put a song file into a channel as if a bot had sent it.</summary>
    public TgMessage AddFile(long channel, string fileName, byte[] data)
    {
        lock (_gate)
        {
            var msg = new TgMessage(channel, _nextMessage++, string.Empty, new TgDocument(_nextDocument++, fileName, data.Length), DateTime.UtcNow);
            Ch(channel).Messages.Add(new Stored(msg, data));
            return msg;
        }
    }

    private Stored? Find(long peer, long id) =>
        _channels.TryGetValue(peer, out var c) ? c.Messages.FirstOrDefault(m => m.Message.Id == id) : null;
}
