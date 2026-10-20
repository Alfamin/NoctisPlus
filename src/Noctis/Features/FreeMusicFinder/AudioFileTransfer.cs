using TL;
using WTelegram;

namespace FreeMusicFinder;

internal sealed class AudioTransferStalledException(long received, long total) : TimeoutException(
    $"AUDIO_TRANSFER_STALLED: no audio bytes arrived for three minutes ({received:N0} / {total:N0} bytes). Check your VPN or system proxy. The request is kept for retry.");

internal sealed class AudioNetworkInterruptedException(Exception cause) : IOException("Audio connection interrupted.", cause);

/// <summary>Owner #100: the output of a fetch that only asks the bot (the next songs of the queue are made ready in the chat,
/// only the next two are downloaded). Nothing may be written to it.</summary>
internal sealed class AskOnlyStream : Stream
{
    public Document? Found { get; internal set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Only asked; nothing is transferred.");
}

/// <summary>Bounded chunk requests, one writer. An abandoned RPC can only return bytes;
/// it never retains the output stream. Cancellation drains every writer before returning.</summary>
internal static class AudioFileTransfer
{
    // Owner #100: 512 KB pieces (the largest Telegram serves at aligned offsets; 1 MB is a multiple). With 128 KB a FLAC
    // took ~16 s over the VPN (live, 2026-10-20: ~2 MB/s, bound by round trips); four times the bytes per trip.
    internal const int ChunkSize = 512 * 1024, Parallelism = 4;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    internal static async Task CopyRecoveringAsync(Stream output, long size,
        Func<long, int, CancellationToken, Task<byte[]>> read, IProgress<double>? progress, CancellationToken ct,
        PartialAudio.Slot kept, Func<Task> reconnect, Noctis.Services.OnlineAccess access, TimeSpan? retryDelay = null)
    {
        var origin = output.Position - kept.Prefix;
        while (true)
        {
            await access.WaitAsync(ct).ConfigureAwait(false);
            output.Position = origin + kept.Prefix;
            try
            {
                await CopyAsync(output, size, read, progress, ct, start: kept.Prefix, kept: kept).ConfigureAwait(false);
                access.Succeeded();
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is AudioNetworkInterruptedException or AudioTransferStalledException)
            {
                access.Failed();
                TransferProgress.Stage(progress, "Waiting for connection…", Noctis.Services.Search.OnlineDownloadStep.Receiving);
                await reconnect().ConfigureAwait(false);
                await access.RetryAsync(ct, retryDelay).ConfigureAwait(false);
            }
        }
    }

    internal static async Task CopyAsync(Stream output, long size,
        Func<long, int, CancellationToken, Task<byte[]>> read, IProgress<double>? progress, CancellationToken ct,
        TimeSpan? idleTimeout = null, long start = 0, PartialAudio.Slot? kept = null)
    {
        if (size <= 0 || size > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("INVALID_AUDIO_SIZE: the provider supplied an empty or oversized file.");
        if (!output.CanSeek || !output.CanWrite) throw new ArgumentException("Audio transfers need a seekable writable output.", nameof(output));
        if (start < 0 || start > size || start % ChunkSize != 0 && start != size) throw new ArgumentOutOfRangeException(nameof(start));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = idleTimeout ?? IdleTimeout;
        stop.CancelAfter(timeout);
        var token = stop.Token;
        var pending = new List<Task<(long Offset, byte[] Bytes)>>(Parallelism);
        // Owner #100: a transfer stopped part way resumes from the bytes kept (start); the unbroken prefix written so far is
        // what can be kept if this one is stopped too (chunks arrive out of order).
        long next = start, received = start, origin = output.Position - start, prefix = start;
        var done = new HashSet<long>();
        double lastShare = 0;
        async Task<(long, byte[])> Part(long offset)
        {
            // This delegate never gets the file. Late results after cancellation are safe.
            var request = read(offset, ChunkSize, token);
            _ = request.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var bytes = await request.WaitAsync(token).ConfigureAwait(false);
            if (bytes.Length != Math.Min(ChunkSize, size - offset)) throw new InvalidDataException("INCOMPLETE_AUDIO: a Telegram chunk did not match the recording's declared size.");
            return (offset, bytes);
        }
        try
        {
            while (next < size || pending.Count > 0)
            {
                while (next < size && pending.Count < Parallelism)
                { token.ThrowIfCancellationRequested(); pending.Add(Part(next)); next += ChunkSize; }
                var ready = await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false);
                pending.Remove(ready);
                var (offset, bytes) = await ready.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                // Only this loop mutates the output. No WaitAsync is used around file I/O:
                // a write must finish/cancel before the caller can close, rename or retry it.
                output.Position = checked(origin + offset);
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                received += bytes.Length; stop.CancelAfter(timeout);
                done.Add(offset);
                while (done.Remove(prefix)) prefix = Math.Min(size, prefix + ChunkSize);
                if (kept is not null) kept.Prefix = prefix;
                var share = (double)received / size;
                if (share - lastShare >= .02 || received == size) progress?.Report(lastShare = share);
            }
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Position = checked(origin + size);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AudioTransferStalledException(received, size); }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { /* all bounded readers are observed */ }
        }
    }

    internal static async Task DownloadAsync(TelegramAccount account, Client client, Document document,
        Stream output, IProgress<double>? progress, CancellationToken ct)
    {
        // Owner #100: asked ahead. The bot has sent the file into the chat; nothing is transferred. A later download finds
        // it there and starts at once, without asking the bot again.
        if (output is AskOnlyStream asked) { asked.Found = document; return; }
        if (output is Noctis.Features.ProgressivePlayback.IProgressiveAudioWriter publisher)
            publisher.BeginAudio(document.size, TelegramBotSource.Extension(document) ?? "");
        Client? media = null;
        var connection = client;
        Lazy<Task<Client>> MediaConnection() => new(async () =>
        {
            connection = await account.ConnectAsync(ct).ConfigureAwait(false);
            return document.dc_id == 0 ? connection : await connection.GetClientForDC(-document.dc_id, true).ConfigureAwait(false);
        });
        var initialMedia = MediaConnection();
        var location = document.ToFileLocation((PhotoSizeBase?)null);
        async Task<byte[]> Read(long offset, int count, CancellationToken token)
        {
            await Noctis.Services.OnlineAccess.Shared.WaitAsync(token).ConfigureAwait(false);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, Noctis.Services.OnlineAccess.Shared.RequestToken);
            try
            {
            var dc = media ?? await initialMedia.Value.WaitAsync(token).ConfigureAwait(false);
            Interlocked.CompareExchange(ref media, dc, null);
            Upload_FileBase file;
            async Task<Upload_FileBase> Fetch(Client from)
            {
                var rpc = from.Upload_GetFile(location, offset, count);
                _ = rpc.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return await rpc.WaitAsync(TimeSpan.FromSeconds(12), attempt.Token).ConfigureAwait(false);
            }
            try { file = await Fetch(dc).ConfigureAwait(false); }
            catch (RpcException ex) when (ex.Code == 303 && ex.Message == "FILE_MIGRATE_X")
            {
                dc = await connection.GetClientForDC(-ex.X, true).WaitAsync(token).ConfigureAwait(false);
                Interlocked.Exchange(ref media, dc);
                file = await Fetch(dc).ConfigureAwait(false);
            }
            Noctis.Services.OnlineAccess.Shared.Succeeded();
            return file is Upload_File bytes ? bytes.bytes : throw new TrackUnavailableException("Telegram returned unsupported audio storage. Retry the recording from the other bot.");
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested && Noctis.Services.OnlineAccess.Shared.IsOffline)
            { throw new AudioNetworkInterruptedException(ex); }
            catch (Exception ex) when (ex is TimeoutException or System.Net.Sockets.SocketException or IOException)
            { throw new AudioNetworkInterruptedException(ex); }
        }
        // Owner #100: the bytes of this very file kept from a transfer stopped earlier go back in first.
        var kept = PartialAudio.Current.Value ?? new PartialAudio.Slot();
        long start = 0;
        if (kept is not null)
        {
            kept.DocumentId = document.id; kept.Size = document.size; kept.Prefix = 0;
            start = await PartialAudio.RestoreAsync(document.id, document.size, output, ct).ConfigureAwait(false);
            kept.Prefix = start;
            if (start > 0) Noctis.Services.DebugLog.Write("Finder", $"resuming a stopped transfer at {start * 100 / document.size}%");
        }
        try
        {
            await CopyRecoveringAsync(output, document.size, Read, progress, ct, kept, async () =>
                {
                    await account.RetireConnectionAsync(connection).ConfigureAwait(false);
                    media = null; initialMedia = MediaConnection();
                }, Noctis.Services.OnlineAccess.Shared).ConfigureAwait(false);
        }
        // A cancelled transfer (removed, or held back for a newer Play, owner #83/#84) keeps the connection: the other
        // transfers and bot conversations share it, and its late chunk answers are simply dropped. Retiring it here
        // disposed the client under them (ObjectDisposedException, a linked reply missed in the TEST log).
        catch (InvalidDataException ex) { throw new TrackUnavailableException(ex.Message); }
    }
}
