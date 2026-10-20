using System.Net;
using FreeMusicFinder;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class NetworkRecoveryTests
{
    [Fact]
    public async Task OfflineCancelsOnlyItsNetworkEpoch_AndWaitsForExplicitOnline()
    {
        var access = new OnlineAccess();
        var old = access.RequestToken;
        access.SetOffline(true);
        Assert.True(old.IsCancellationRequested);
        var wait = access.WaitAsync(CancellationToken.None);
        access.Hint(); access.Succeeded();
        Assert.False(wait.IsCompleted);
        Assert.True(access.IsOffline);
        access.SetOffline(false);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(access.RequestToken.IsCancellationRequested);
        Assert.NotEqual(old, access.RequestToken);
    }

    private sealed class Http : HttpMessageHandler
    {
        internal int Calls;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; Started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); }
    }

    [Fact]
    public async Task OfflineStopsInflightHttp_AndDoesNotSendNewCalls()
    {
        var access = new OnlineAccess(); var inner = new Http();
        using var http = new HttpClient(new OnlineAccessHandler(inner, access));
        var request = http.GetAsync("https://synthetic.invalid/");
        await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        access.SetOffline(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync("https://synthetic.invalid/"));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void OnlyActualSuccessfulTrafficSignalsRecovery_AndNeverChangesOfflineChoice()
    {
        var access = new OnlineAccess(); int recovered = 0;
        access.Recovered += () => recovered++;
        access.Failed(); access.Hint(); Assert.Equal(0, recovered);
        access.Succeeded(); access.Succeeded(); Assert.Equal(1, recovered);
        access.Failed(); access.SetOffline(true); access.Succeeded(); Assert.Equal(1, recovered);
        access.SetOffline(false); access.Succeeded(); Assert.Equal(2, recovered);
    }

    [Fact]
    public async Task InterruptedAudioRetriesOnlyTheMissingPrefix_WithoutClosingItsReader()
    {
        const int chunk = AudioFileTransfer.ChunkSize;
        var song = new byte[chunk * 8]; new Random(12).NextBytes(song);
        using var output = new MemoryStream();
        var slot = new PartialAudio.Slot(); var access = new OnlineAccess();
        int resets = 0; long resumedAt = -1;
        async Task<byte[]> Read(long offset, int size, CancellationToken ct)
        {
            if (resets == 0 && offset >= chunk * 4)
            {
                while (slot.Prefix < chunk * 4) await Task.Delay(1, ct);
                throw new AudioNetworkInterruptedException(new IOException("VPN unavailable"));
            }
            if (resets > 0) Interlocked.CompareExchange(ref resumedAt, offset, -1);
            return song.AsSpan((int)offset, size).ToArray();
        }
        await AudioFileTransfer.CopyRecoveringAsync(output, song.Length, Read, null, CancellationToken.None,
            slot, () => { resets++; return Task.CompletedTask; }, access, TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resets); Assert.Equal(chunk * 4, resumedAt);
        Assert.Equal(song, output.ToArray()); Assert.Equal(song.Length, slot.Prefix);
    }

    [Fact]
    public async Task CancelledPlaybackCannotBeResurrectedByNetworkRecovery()
    {
        using var output = new MemoryStream(); using var stop = new CancellationTokenSource();
        var access = new OnlineAccess(); var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copy = AudioFileTransfer.CopyRecoveringAsync(output, AudioFileTransfer.ChunkSize,
            (_, _, _) => Task.FromException<byte[]>(new AudioNetworkInterruptedException(new IOException())),
            null, stop.Token, new(), () => { reset.TrySetResult(); access.SetOffline(true); return Task.CompletedTask; }, access, TimeSpan.Zero);
        await reset.Task.WaitAsync(TimeSpan.FromSeconds(1));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
        access.SetOffline(false); Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task CorruptRemoteAudioIsNotRetriedAsAConnectivityProblem()
    {
        using var output = new MemoryStream(); int retries = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => AudioFileTransfer.CopyRecoveringAsync(output, 9,
            (_, _, _) => Task.FromResult(new byte[2]), null, CancellationToken.None, new(),
            () => { retries++; return Task.CompletedTask; }, new(), TimeSpan.Zero));
        Assert.Equal(0, retries);
    }
}
