using Noctis.Features.ProgressivePlayback;
using Xunit;

namespace Noctis.Tests;

/// <summary>Cloud #261: the portable reader under the phone's Media3 data source, over a real growing transfer file
/// (synthetic bytes with an MP3 or FLAC header; nothing is decoded here: decoding is a device check).</summary>
public class ProgressiveReadSessionTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(60);

    private sealed class Rig : IDisposable
    {
        public required ProgressiveTransfer Transfer { get; init; }
        public required Stream Writer { get; init; }
        public required string Path { get; init; }
        public required byte[] Audio { get; init; }
        public required TestPersistenceService Data { get; init; }
        public int Written;
        public void Write(int count) { Writer.Write(Audio.AsSpan(Written, count)); Written += count; }
        public void Dispose() { Transfer.End(null, new IOException("test over")); Data.Dispose(); }
    }

    private static async Task<Rig> StartAsync(string extension, int size = 1024 * 1024, int first = 300 * 1024)
    {
        var data = new TestPersistenceService();
        var audio = new byte[size];
        new Random(7).NextBytes(audio);
        if (extension == "flac") "fLaC"u8.CopyTo(audio);
        else { audio[0] = 0xFF; audio[1] = 0xFB; audio[2] = 0x90; audio[3] = 0x64; }
        var transfer = new ProgressiveTransfer(data.DataDirectory, "song", TimeSpan.FromSeconds(100));
        var file = new FileStream(System.IO.Path.Combine(data.DataDirectory, "song.part"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1);
        var writer = transfer.Attach(file);
        ((IProgressiveAudioWriter)writer).SetFormatPolicy(_ => true);
        ((IProgressiveAudioWriter)writer).BeginAudio(size, extension);
        var rig = new Rig { Transfer = transfer, Writer = writer, Path = "", Audio = audio, Data = data };
        rig.Write(first);
        string? path = null;
        for (var i = 0; i < 200 && path is null; i++) { path = transfer.ReadyPath; if (path is null) await Task.Delay(20); }
        Assert.NotNull(path);
        return new Rig { Transfer = transfer, Writer = writer, Path = path!, Audio = audio, Data = data, Written = rig.Written };
    }

    private static byte[] ReadAvailable(ProgressiveReadSession s, int max)
    {
        var all = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (all.Length < max && s.TryRead(buffer, 0, buffer.Length, Short, out var n) && n > 0) all.Write(buffer, 0, n);
        return all.ToArray();
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    public async Task ReadsWhatArrived_WaitsAtTheTail_ResumesOnNewBytes_AndEndsOnlyAtTheRealEnd(string extension)
    {
        using var rig = await StartAsync(extension);
        Assert.False(File.Exists(rig.Path)); // playable before the song is saved
        using var s = ProgressiveReadSession.TryOpen(rig.Path)!;
        Assert.Equal(rig.Audio.Length, s.Length);

        var first = ReadAvailable(s, int.MaxValue);
        Assert.Equal(rig.Audio.AsSpan(0, rig.Written).ToArray(), first);
        Assert.False(s.TryRead(new byte[16], 0, 16, Short, out _)); // starved: not the end

        var waiting = Task.Run(() => { var b = new byte[1000]; while (!s.TryRead(b, 0, b.Length, Short, out var n)) { } return b; });
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        rig.Write(rig.Audio.Length - rig.Written);   // the rest arrives
        var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(rig.Audio.AsSpan(first.Length, 1000).ToArray(), resumed);

        rig.Transfer.End(null);                      // complete
        var rest = ReadAvailable(s, int.MaxValue);
        Assert.Equal(rig.Audio.Length, first.Length + 1000 + rest.Length);
        Assert.True(s.TryRead(new byte[16], 0, 16, Short, out var end));
        Assert.Equal(0, end);                        // the genuine end
    }

    [Fact]
    public async Task AFailedTransfer_IsAnError_NotTheEnd()
    {
        using var rig = await StartAsync("flac");
        using var s = ProgressiveReadSession.TryOpen(rig.Path)!;
        ReadAvailable(s, int.MaxValue);
        rig.Transfer.End(null, new IOException("network lost"));
        Assert.Throws<IOException>(() => s.TryRead(new byte[16], 0, 16, Short, out _));
    }

    [Fact]
    public async Task CloseFromAnotherThread_UnblocksAWaitingRead_AndReleasesTheLease()
    {
        using var rig = await StartAsync("mp3");
        var s = ProgressiveReadSession.TryOpen(rig.Path)!;
        ReadAvailable(s, int.MaxValue);
        var blocked = Task.Run(() => s.TryRead(new byte[16], 0, 16, TimeSpan.FromSeconds(30), out _));
        await Task.Delay(100);
        Assert.True(rig.Transfer.HasReaders);
        s.Dispose(); s.Dispose();
        await Assert.ThrowsAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(rig.Transfer.HasReaders);
        Assert.Equal(0, ReadyStill(rig) ? 0 : 1); // the download itself is untouched by a closed reader
    }

    private static bool ReadyStill(Rig rig) => !rig.Transfer.Finished && ProgressiveTransfer.IsAvailable(rig.Path);

    [Fact]
    public async Task TwoReaders_AreIndependent_AndTheBufferIsReleasedOnlyAfterTheLast()
    {
        using var rig = await StartAsync("flac");
        var a = ProgressiveReadSession.TryOpen(rig.Path)!;
        var b = ProgressiveReadSession.TryOpen(rig.Path)!;
        Assert.True(b.TrySeek(1000, Short));
        a.Dispose();
        var buffer = new byte[10];
        Assert.True(b.TryRead(buffer, 0, 10, Short, out var n));
        Assert.Equal(rig.Audio.AsSpan(1000, n).ToArray(), buffer.AsSpan(0, n).ToArray());
        rig.Write(rig.Audio.Length - rig.Written); rig.Transfer.End(null);
        Assert.True(rig.Transfer.HasReaders);   // b still reads the finished buffer
        Assert.NotNull(ProgressiveTransfer.Acquire(rig.Path) is null ? "" : null); // finished: no new progressive leases
        b.Dispose();
        Assert.False(rig.Transfer.HasReaders);
        Assert.False(ProgressiveTransfer.IsAvailable(rig.Path)); // released and unregistered
    }

    [Fact]
    public async Task Seek_WithinTheBytes_WaitsBeyondThem_AndRefusesPastTheEnd()
    {
        using var rig = await StartAsync("mp3");
        using var s = ProgressiveReadSession.TryOpen(rig.Path)!;
        Assert.True(s.TrySeek(rig.Written - 10, Short));
        Assert.Null(s.TrySeek(rig.Written + 50_000, Short));  // not received yet: wait, never a fake EOF
        Assert.True(s.TrySeek(0, Short));                     // probe back to the start still works
        var target = rig.Written + 50_000L;
        var ahead = Task.Run(() => { bool? r; while ((r = s.TrySeek(target, Short)) is null) { } return r; });
        rig.Write(100_000);
        Assert.True(await ahead.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(target, s.Position);
        rig.Write(rig.Audio.Length - rig.Written); rig.Transfer.End(null);
        Assert.False(s.TrySeek(rig.Audio.Length + 1, Short)); // past the end of the complete file
        Assert.True(s.TrySeek(rig.Audio.Length, Short));
        Assert.True(s.TryRead(new byte[4], 0, 4, Short, out var end)); Assert.Equal(0, end);
    }

    [Fact]
    public void NothingBeingWritten_MeansAnOrdinaryFile()
        => Assert.Null(ProgressiveReadSession.TryOpen(Path.Combine(Path.GetTempPath(), "not-a-transfer.flac")));
}
