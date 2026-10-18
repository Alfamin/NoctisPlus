using TL;
using WTelegram;

namespace FreeMusicFinder;

internal sealed class AudioTransferStalledException(long received, long total) : TimeoutException(
    $"AUDIO_TRANSFER_STALLED: no audio bytes arrived for three minutes ({received:N0} / {total:N0} bytes). Check your VPN or system proxy. The request is kept for retry.");

/// <summary>Bounded chunk requests, one writer. An abandoned RPC can only return bytes;
/// it never retains the output stream. Cancellation drains every writer before returning.</summary>
internal static class AudioFileTransfer
{
    internal const int ChunkSize = 128 * 1024, Parallelism = 4;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    internal static async Task CopyAsync(Stream output, long size,
        Func<long, int, CancellationToken, Task<byte[]>> read, IProgress<double>? progress, CancellationToken ct,
        TimeSpan? idleTimeout = null)
    {
        if (size <= 0 || size > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("INVALID_AUDIO_SIZE: the provider supplied an empty or oversized file.");
        if (!output.CanSeek || !output.CanWrite) throw new ArgumentException("Audio transfers need a seekable writable output.", nameof(output));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = idleTimeout ?? IdleTimeout;
        stop.CancelAfter(timeout);
        var token = stop.Token;
        var pending = new List<Task<(long Offset, byte[] Bytes)>>(Parallelism);
        long next = 0, received = 0, origin = output.Position;
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
        if (output is Noctis.Features.ProgressivePlayback.IProgressiveAudioWriter publisher)
            publisher.BeginAudio(document.size, TelegramBotSource.Extension(document) ?? "");
        Client? media = null;
        var initialMedia = new Lazy<Task<Client>>(() => document.dc_id == 0
            ? Task.FromResult(client) : client.GetClientForDC(-document.dc_id, true));
        var location = document.ToFileLocation((PhotoSizeBase?)null);
        async Task<byte[]> Read(long offset, int count, CancellationToken token)
        {
            var dc = media ?? await initialMedia.Value.WaitAsync(token).ConfigureAwait(false);
            Interlocked.CompareExchange(ref media, dc, null);
            Upload_FileBase file;
            try { file = await dc.Upload_GetFile(location, offset, count).WaitAsync(token).ConfigureAwait(false); }
            catch (RpcException ex) when (ex.Code == 303 && ex.Message == "FILE_MIGRATE_X")
            {
                dc = await client.GetClientForDC(-ex.X, true).WaitAsync(token).ConfigureAwait(false);
                Interlocked.Exchange(ref media, dc);
                file = await dc.Upload_GetFile(location, offset, count).WaitAsync(token).ConfigureAwait(false);
            }
            return file is Upload_File bytes ? bytes.bytes : throw new TrackUnavailableException("Telegram returned unsupported audio storage. Retry the recording from the other bot.");
        }
        try { await CopyAsync(output, document.size, Read, progress, ct).ConfigureAwait(false); }
        catch (AudioTransferStalledException)
        { await account.RetireConnectionAsync(client).ConfigureAwait(false); throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await account.RetireConnectionAsync(client).ConfigureAwait(false); throw; }
        catch (InvalidDataException ex) { throw new TrackUnavailableException(ex.Message); }
    }
}
