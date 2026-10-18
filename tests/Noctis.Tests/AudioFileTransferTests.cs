using FreeMusicFinder;
using Xunit;

namespace Noctis.Tests;

public class AudioFileTransferTests
{
    [Fact]
    public async Task ParallelOutOfOrderChunksWriteTheExactRecording_AndRespectTheStartingPosition()
    {
        var bytes = new byte[AudioFileTransfer.ChunkSize * 3 + 71]; new Random(17).NextBytes(bytes);
        using var output = new MemoryStream(); output.Write("prefix"u8);
        int active = 0, maximum = 0;
        async Task<byte[]> Read(long offset, int count, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximum, current);
            try
            {
                await Task.Delay(offset == 0 ? 40 : 5, ct);
                return bytes.AsSpan((int)offset, Math.Min(count, bytes.Length - (int)offset)).ToArray();
            }
            finally { Interlocked.Decrement(ref active); }
        }
        await AudioFileTransfer.CopyAsync(output, bytes.Length, Read, null, default);
        Assert.Equal(4, maximum); Assert.Equal(0, active); Assert.Equal(bytes.Length + 6, output.Position);
        Assert.Equal("prefix"u8.ToArray(), output.ToArray()[..6]); Assert.Equal(bytes, output.ToArray()[6..]);
    }

    [Fact]
    public async Task SilentTransferTimesOut_AndLateRpcResultsCannotWriteIntoTheNextAttempt()
    {
        using var output = new MemoryStream();
        var late = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = await Assert.ThrowsAsync<AudioTransferStalledException>(() => AudioFileTransfer.CopyAsync(output,
            AudioFileTransfer.ChunkSize, (_, _, _) => late.Task, null, default, TimeSpan.FromMilliseconds(60)));
        Assert.Contains("AUDIO_TRANSFER_STALLED", stalled.Message); Assert.Equal(0, output.Length);
        output.Write("replacement"u8); late.SetResult(new byte[AudioFileTransfer.ChunkSize]);
        await Task.Delay(30); Assert.Equal("replacement"u8.ToArray(), output.ToArray());
    }

    [Fact]
    public async Task CancellingASilentTransferReleasesTheQueueWithoutWaitingForItsRpc()
    {
        using var output = new MemoryStream(); using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copy = AudioFileTransfer.CopyAsync(output, AudioFileTransfer.ChunkSize,
            (_, _, _) => { entered.TrySetResult(); return late.Task; }, null, stop.Token);
        await entered.Task; stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.WaitAsync(TimeSpan.FromSeconds(2)));
        late.SetException(new IOException("late transport failure"));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task ProgressResetsTheIdleDeadline_AndAWholeTransferHasNoFixedTimeLimit()
    {
        using var output = new MemoryStream(); var bytes = new byte[AudioFileTransfer.ChunkSize * 24];
        using var release = new SemaphoreSlim(0);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        int arrivals = 0;
        async Task<byte[]> Read(long offset, int count, CancellationToken ct)
        {
            // First completion opens the next chunk: each delay is below the idle deadline,
            // while the whole recording takes substantially longer than that deadline.
            await release.WaitAsync(ct); await Task.Delay(100, ct);
            if (Interlocked.Increment(ref arrivals) < 24) release.Release();
            return new byte[count];
        }
        release.Release();
        await AudioFileTransfer.CopyAsync(output, bytes.Length, Read, null, default, TimeSpan.FromSeconds(2));
        Assert.Equal(bytes.Length, output.Length); Assert.Equal(24, arrivals);
        Assert.True(elapsed.Elapsed > TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(131073)]
    public async Task ShortOrOversizedChunksAreRejectedBeforePublishingAFile(int supplied)
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => AudioFileTransfer.CopyAsync(output, AudioFileTransfer.ChunkSize,
            (_, _, _) => Task.FromResult(new byte[supplied]), null, default));
        Assert.Equal(0, output.Length);
    }

    private sealed class Source(string id, Func<Stream, CancellationToken, Task<string>> fetch) : IMusicSource
    {
        public string Id => id;
        public Task<IReadOnlyList<MusicRequest>> SearchAsync(string query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MusicRequest>>([]);
        public Task<string> FetchAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
            => fetch(output, ct);
    }

    [Fact]
    public async Task RealPartFile_StalledPrimaryUsesBackup_AndLateBytesCannotChangeSavedAudio()
    {
        using var data = new TestPersistenceService();
        var late = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new Source("DeezLoad", async (stream, ct) =>
        {
            await AudioFileTransfer.CopyAsync(stream, AudioFileTransfer.ChunkSize,
                (_, _, _) => late.Task, null, ct, TimeSpan.FromMilliseconds(80));
            return "flac";
        });
        var bytes = "fLaCsynthetic backup"u8.ToArray();
        var backup = new Source("Music Hunters", async (stream, ct) => { await stream.WriteAsync(bytes, ct); return "flac"; });
        var sources = new MusicSources(primary, backup, _ => { });
        var track = sources.Track(new("Silent primary", "Synthetic artist"));
        Assert.Equal(DownloadOutcome.Saved, await Downloader.DownloadAsync(track, data.DataDirectory, null, default));
        var saved = Downloader.FindExisting(data.DataDirectory, track.FileName)!;
        Assert.Equal(bytes, await File.ReadAllBytesAsync(saved));
        late.SetResult(new byte[AudioFileTransfer.ChunkSize]); await Task.Delay(30);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(saved));
        Assert.Empty(Directory.GetFiles(data.DataDirectory, "*.fmf.part"));
    }

    [Fact]
    public async Task BothBotsStall_DurableQueueWaitsWithReasonAndRetryTime_AndSurvivesReopening()
    {
        using var data = new TestPersistenceService();
        var state = Path.Combine(data.DataDirectory, "queue.json");
        var sources = new MusicSources(new Source("DeezLoad", (_, _) => throw new AudioTransferStalledException(0, 1024)),
            new Source("Music Hunters", (_, _) => throw new AudioTransferStalledException(0, 1024)), _ => { });
        using (var downloads = new Downloads(_ => { }, state, sources.Track))
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            downloads.Changed += d => { if (d.State == DownloadState.Waiting) waiting.TrySetResult(); };
            var request = downloads.Start(sources.Track(new("Interrupted", "Synthetic artist")), data.DataDirectory);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DownloadState.Waiting, request.State);
            Assert.Contains("AUDIO_TRANSFER_STALLED", request.Error);
            Assert.True(request.RetryAt > DateTimeOffset.UtcNow);
            Assert.Single(downloads.Active);
            downloads.SetPaused(true);
        }
        using var reopened = new Downloads(_ => { }, state, sources.Track);
        Assert.True(reopened.Paused); Assert.Single(reopened.Active);
        Assert.Equal(DownloadState.Waiting, reopened.Active[0].State);
        Assert.Contains("AUDIO_TRANSFER_STALLED", reopened.Active[0].Error);
        Assert.Empty(Directory.GetFiles(data.DataDirectory, "*.fmf.part"));
    }
}

internal static class InterlockedExtensions
{
    internal static void Max(ref int target, int value)
    {
        int previous;
        do { previous = Volatile.Read(ref target); if (previous >= value) return; }
        while (Interlocked.CompareExchange(ref target, value, previous) != previous);
    }
}
