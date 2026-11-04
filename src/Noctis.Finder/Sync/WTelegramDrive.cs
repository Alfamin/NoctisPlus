using System.Security.Cryptography;
using Noctis.TelegramSync;
using TL;
using WTelegram;

namespace FreeMusicFinder;

/// <summary>
/// Cross-device sync's <see cref="ITelegramDrive"/> over the host's ONE signed-in account
/// (<see cref="TelegramAccount.ConnectAsync"/>: the same connection Music Finder uses; never a second
/// session). Written against the pinned WTelegramClient 4.4.9 API. NOT checked against real Telegram:
/// history paging, pinning, forwarding and DC migration need the owner's live channel check.
/// Limits: a stable <c>random_id</c> lets Telegram refuse a second copy of a retried post, but only for
/// as long as Telegram remembers that id, so this is not crash-proof exactly-once; an upload that is
/// interrupted starts again from the first byte (only downloads resume).
/// </summary>
internal sealed class WTelegramDrive(Func<CancellationToken, Task<Client>> connect) : ITelegramDrive
{
    /// <summary>upload.getFile: a chunk is a multiple of 4 KiB that divides 1 MiB; offsets are multiples of the chunk.</summary>
    internal const int Chunk = 512 * 1024;
    private readonly Dictionary<long, Channel> _channels = new();
    private readonly Dictionary<(long Peer, long Id), Document> _documents = new();
    private readonly object _gate = new();

    public WTelegramDrive(TelegramAccount account) : this(account.ConnectAsync) { }

    public async Task<long> GetSelfUserIdAsync(CancellationToken ct = default)
        => (await connect(ct).ConfigureAwait(false)).UserId;

    public Task<TgChannel> FindOrCreateChannelAsync(string role, string title, string markerIfNew, CancellationToken ct = default)
        => Call(async client =>
        {
            var dialogs = await client.Messages_GetAllDialogs().ConfigureAwait(false);
            foreach (var channel in dialogs.chats.Values.OfType<Channel>().Where(c => c.flags.HasFlag(Channel.Flags.creator) && !c.IsGroup))
            {
                Remember(channel);
                var full = await client.Channels_GetFullChannel(channel).ConfigureAwait(false);
                if (full.full_chat is ChannelFull info && TgMarkers.IsOurs(info.about, role))
                    return new TgChannel(channel.id, channel.title, info.about);
            }
            var created = await client.Channels_CreateChannel(title, markerIfNew, broadcast: true).ConfigureAwait(false);
            var made = created.Chats.Values.OfType<Channel>().FirstOrDefault()
                ?? throw new IOException("Telegram did not return the new channel.");
            Remember(made);
            return new TgChannel(made.id, made.title, markerIfNew);
        }, ct);

    public Task<TgMessage> PostDocumentAsync(long channel, string fileName, Stream content, string caption,
        IProgress<long>? progress = null, CancellationToken ct = default)
        => PostDocumentAsync(channel, fileName, content, caption, RandomId(), progress, ct);

    public Task<TgMessage> PostDocumentAsync(long channel, string fileName, Stream content, string caption, long requestId,
        IProgress<long>? progress = null, CancellationToken ct = default)
        => Call(async client =>
        {
            var peer = await PeerAsync(client, channel).ConfigureAwait(false);
            var start = content.CanSeek ? content.Position : 0;
            var file = await client.UploadFileAsync(content, fileName,
                progress is null ? null : (sent, _) => progress.Report(sent)).ConfigureAwait(false);
            var media = new InputMediaUploadedDocument(file, MimeOf(fileName),
                new DocumentAttributeFilename { file_name = fileName }) { flags = InputMediaUploadedDocument.Flags.force_file };
            try
            {
                var updates = await client.Messages_SendMedia(peer, media, caption, requestId).ConfigureAwait(false);
                return Posted(updates, channel) ?? throw new IOException("Telegram did not return the posted message.");
            }
            catch (RpcException ex) when (ex.Message == "RANDOM_ID_DUPLICATE")
            {
                // Sent before (a lost reply, a restart): the earlier copy is the post.
                var size = content.CanSeek ? content.Length - start : -1;
                var recent = await ReadHistoryAsync(channel, 0, 100, ct).ConfigureAwait(false);
                return recent.Where(m => m.Caption == caption && m.Document is { } d && d.FileName == fileName && (size < 0 || d.Size == size))
                    .OrderByDescending(m => m.Id).FirstOrDefault()
                    ?? throw new IOException("This post was already sent; Telegram did not show it yet. Sync will check again.");
            }
        }, ct);

    public Task<IReadOnlyList<TgMessage>> ReadHistoryAsync(long channel, long afterId, int limit = 100, CancellationToken ct = default)
        => Call(async client =>
        {
            var peer = await PeerAsync(client, channel).ConfigureAwait(false);
            var (offsetId, addOffset, minId) = HistoryWindow(afterId, limit);
            var page = await client.Messages_GetHistory(peer, offsetId, default, addOffset, limit, 0, minId).ConfigureAwait(false);
            return (IReadOnlyList<TgMessage>)page.Messages.OfType<Message>().Where(m => m.id > afterId)
                .Select(m => Convert(m, channel)).OrderBy(m => m.Id).ToList();
        }, ct);

    public Task<TgMessage?> GetMessageAsync(long peer, long id, CancellationToken ct = default)
        => Call(async client =>
        {
            var channel = await ChannelAsync(client, peer).ConfigureAwait(false);
            var found = await client.Channels_GetMessages(channel, new InputMessageID { id = checked((int)id) }).ConfigureAwait(false);
            return found.Messages.OfType<Message>().FirstOrDefault() is { } m ? Convert(m, peer) : null;
        }, ct);

    public Task<TgMessage?> GetPinnedAsync(long channel, CancellationToken ct = default)
        => Call(async client =>
        {
            var full = await client.Channels_GetFullChannel(await ChannelAsync(client, channel).ConfigureAwait(false)).ConfigureAwait(false);
            return full.full_chat is ChannelFull { pinned_msg_id: > 0 } info ? await GetMessageAsync(channel, info.pinned_msg_id, ct).ConfigureAwait(false) : null;
        }, ct);

    public Task PinAsync(long channel, long id, CancellationToken ct = default)
        => Call(async client =>
        {
            await client.Messages_UpdatePinnedMessage(await PeerAsync(client, channel).ConfigureAwait(false), checked((int)id), silent: true).ConfigureAwait(false);
            return true;
        }, ct);

    public Task DeleteMessagesAsync(long channel, IReadOnlyList<long> ids, CancellationToken ct = default)
        => Call(async client =>
        {
            if (ids.Count == 0) return true;
            var target = await ChannelAsync(client, channel).ConfigureAwait(false);
            foreach (var part in ids.Select(i => checked((int)i)).Chunk(100))
                await client.Channels_DeleteMessages(target, part).ConfigureAwait(false);
            return true;
        }, ct);

    public Task DownloadAsync(TgMessage message, Stream destination, long offset = 0, IProgress<long>? progress = null, CancellationToken ct = default)
        => Call(async client =>
        {
            var document = await DocumentAsync(message, ct).ConfigureAwait(false);
            var location = document.ToFileLocation((PhotoSizeBase?)null);
            var media = document.dc_id == 0 ? client : await client.GetClientForDC(-document.dc_id, true).ConfigureAwait(false);
            var (aligned, skip) = Resume(offset);
            var buffer = new byte[Chunk];
            for (var at = aligned; at < document.size;)
            {
                ct.ThrowIfCancellationRequested();
                Upload_FileBase part;
                try { part = await media.Upload_GetFile(location, at, Chunk).ConfigureAwait(false); }
                catch (RpcException ex) when (ex.Code == 303 && ex.Message == "FILE_MIGRATE_X")
                {
                    media = await client.GetClientForDC(-ex.X, true).ConfigureAwait(false);
                    continue;
                }
                if (part is not Upload_File { bytes: { Length: > 0 } bytes })
                    throw new IOException("Telegram returned this file from storage the app cannot read.");
                var from = (int)Math.Min(skip, bytes.Length);
                await destination.WriteAsync(bytes.AsMemory(from), ct).ConfigureAwait(false);
                skip -= from; at += bytes.Length;
                progress?.Report(at);
                if (bytes.Length < Chunk) break; // the last part
            }
            return true;
        }, ct, message);

    public Task<TgMessage> ForwardAsync(long fromPeer, long id, long toChannel, CancellationToken ct = default)
        => Call(async client =>
        {
            var from = await PeerAsync(client, fromPeer).ConfigureAwait(false);
            var to = await PeerAsync(client, toChannel).ConfigureAwait(false);
            var updates = await client.Messages_ForwardMessages(from, [checked((int)id)], [RandomId()], to).ConfigureAwait(false);
            return Posted(updates, toChannel) ?? throw new IOException("Telegram did not return the forwarded message.");
        }, ct);

    // ── Pure helpers (tested without Telegram) ──

    private static long RandomId() => BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8));

    /// <summary>Where to ask from and how many bytes of the first answer were already saved.</summary>
    internal static (long Aligned, long Skip) Resume(long offset)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var aligned = offset / Chunk * Chunk;
        return (aligned, offset - aligned);
    }

    /// <summary>messages.getHistory arguments for up to <paramref name="limit"/> posts just after <paramref name="afterId"/>.</summary>
    internal static (int OffsetId, int AddOffset, int MinId) HistoryWindow(long afterId, int limit)
    {
        var after = checked((int)afterId);
        return (after + 1, -Math.Clamp(limit, 1, 100), after);
    }

    /// <summary>Telegram's errors as the sync engine's own (FLOOD_WAIT and an expired file reference).</summary>
    internal static Exception? Map(RpcException ex, TgMessage? message)
    {
        if (ex.Code == 420 || ex.Message.StartsWith("FLOOD_WAIT", StringComparison.Ordinal) || ex.Message.StartsWith("FLOOD_PREMIUM_WAIT", StringComparison.Ordinal))
            return new TelegramFloodWaitException(TimeSpan.FromSeconds(Math.Max(ex.X, 1)), ex.Message.StartsWith("FLOOD_PREMIUM", StringComparison.Ordinal));
        if (message is not null && ex.Message.StartsWith("FILE_REFERENCE_", StringComparison.Ordinal))
            return new TelegramFileReferenceExpiredException(message.Peer, message.Id);
        return null;
    }

    internal static TgMessage Convert(Message m, long peer)
    {
        TgDocument? doc = m.media is MessageMediaDocument { document: Document d }
            ? new TgDocument(d.id, d.Filename ?? "", d.size) : null;
        return new TgMessage(peer, m.id, m.message ?? "", doc, DateTime.SpecifyKind(m.date, DateTimeKind.Utc));
    }

    private static string MimeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".flac" => "audio/flac",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".ogg" or ".opus" => "audio/ogg",
        _ => "application/octet-stream",
    };

    // ── Connection-side helpers ──

    private async Task<T> Call<T>(Func<Client, Task<T>> work, CancellationToken ct, TgMessage? about = null)
    {
        var client = await connect(ct).ConfigureAwait(false);
        try { return await work(client).WaitAsync(ct).ConfigureAwait(false); }
        catch (RpcException ex) when (Map(ex, about) is { } mapped)
        {
            if (mapped is TelegramFileReferenceExpiredException && about is not null) lock (_gate) _documents.Remove((about.Peer, about.Id));
            throw mapped;
        }
    }

    private void Remember(Channel channel) { lock (_gate) _channels[channel.id] = channel; }

    private async Task<Channel> ChannelAsync(Client client, long id)
    {
        lock (_gate) if (_channels.TryGetValue(id, out var known)) return known;
        var dialogs = await client.Messages_GetAllDialogs().ConfigureAwait(false);
        foreach (var c in dialogs.chats.Values.OfType<Channel>()) Remember(c);
        lock (_gate) return _channels.TryGetValue(id, out var found) ? found : throw new IOException("The sync channel is not in this account's chats.");
    }

    private async Task<InputPeer> PeerAsync(Client client, long id)
    {
        lock (_gate) if (_channels.TryGetValue(id, out var known)) return known;
        try { return await ChannelAsync(client, id).ConfigureAwait(false); }
        catch (IOException)
        {
            // A forward source can be a bot or user chat (Music Finder's bots) rather than a channel.
            var dialogs = await client.Messages_GetAllDialogs().ConfigureAwait(false);
            if (dialogs.users.TryGetValue(id, out var user)) return user;
            throw;
        }
    }

    private async Task<Document> DocumentAsync(TgMessage message, CancellationToken ct)
    {
        lock (_gate) if (_documents.TryGetValue((message.Peer, message.Id), out var known)) return known;
        var client = await connect(ct).ConfigureAwait(false);
        var channel = await ChannelAsync(client, message.Peer).ConfigureAwait(false);
        var found = await client.Channels_GetMessages(channel, new InputMessageID { id = checked((int)message.Id) }).ConfigureAwait(false);
        var document = found.Messages.OfType<Message>().Select(m => (m.media as MessageMediaDocument)?.document as Document).FirstOrDefault(d => d is not null)
            ?? throw new IOException("This sync post has no file.");
        lock (_gate) _documents[(message.Peer, message.Id)] = document;
        return document;
    }

    private TgMessage? Posted(UpdatesBase updates, long channel)
    {
        foreach (var update in updates.UpdateList)
            if (update is UpdateNewChannelMessage { message: Message m })
            {
                if (m.media is MessageMediaDocument { document: Document d }) lock (_gate) _documents[(channel, m.id)] = d;
                return Convert(m, channel);
            }
        return null;
    }
}
