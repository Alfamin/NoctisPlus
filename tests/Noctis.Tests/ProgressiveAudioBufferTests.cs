using Noctis.Features.ProgressivePlayback;
using Xunit;

namespace Noctis.Tests;

public class ProgressiveAudioBufferTests
{
    [Fact]
    public async Task NativeInputCannotRewindOrReopenAfterAnInterruptedRead()
    {
        using var rig = new Rig(); rig.Buffer.Begin(8);
        rig.Buffer.Writer.Write("abcd"u8); rig.Buffer.Finish(new IOException("VPN unavailable"));
        using var input = new ProgressiveMediaInput(rig.Buffer);
        Assert.True(input.Open(out _));
        var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
        try
        {
            Assert.Equal(4, input.Read(memory, 8));
            var tail = Task.Run(() => input.Read(memory, 8));
            for (int i = 0; i < 100 && input.Error is null; i++) await Task.Delay(10);
            Assert.NotNull(input.Error);
            Assert.False(input.Seek(0)); Assert.False(input.Open(out _));
            Assert.False(tail.IsCompleted);
            input.CancelRead();
            Assert.Equal(-1, await tail.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(-1, input.Read(memory, 8));
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
    }
    [Fact]
    public void BufferedAudioDrainsBeforeAnInterruptedProducerIsReported()
    {
        using var rig = new Rig(); var b = rig.Buffer; b.Begin(8);
        b.Writer.Write("abcd"u8); b.Finish(new IOException("VPN unavailable"));
        using var reader = b.OpenReader(); var data = new byte[8];
        Assert.Equal(4, reader.Read(data));
        Assert.Equal("abcd", System.Text.Encoding.ASCII.GetString(data, 0, 4));
        Assert.Throws<IOException>(() => reader.Read(data));
        Assert.False(b.Complete);
    }
    private sealed class Rig : IDisposable
    {
        internal readonly TestPersistenceService Data = new();
        internal readonly ProgressiveAudioBuffer Buffer;
        internal Rig() => Buffer = new(new FileStream(Path.Combine(Data.DataDirectory, "synthetic.part"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1));
        public void Dispose() { Buffer.Dispose(); Data.Dispose(); }
    }
    [Fact]
    public async Task OutOfOrderChunksNeverPublishHoles_AndTemporaryTailIsNotEof()
    {
        using var rig = new Rig(); var b = rig.Buffer; b.Begin(12);
        using var reader = b.OpenReader();
        b.Writer.Position = 4; b.Writer.Write("efgh"u8); Assert.Equal(0, b.Prefix);
        var data = new byte[12]; var first = Task.Run(() => reader.Read(data));
        await Task.Delay(50); Assert.False(first.IsCompleted);
        b.Writer.Position = 0; b.Writer.Write("abcd"u8);
        Assert.Equal(8, await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("abcdefgh", System.Text.Encoding.ASCII.GetString(data, 0, 8));
        var tail = Task.Run(() => reader.Read(data)); await Task.Delay(50); Assert.False(tail.IsCompleted);
        b.Writer.Position = 8; b.Writer.Write("ijkl"u8);
        Assert.Equal(4, await tail.WaitAsync(TimeSpan.FromSeconds(2))); b.Finish();
        Assert.Equal(0, reader.Read(data)); Assert.True(b.Complete);
    }
    [Fact]
    public async Task CancellingPlaybackReleasesItsBlockedReadWithoutCancellingDownload()
    {
        using var rig = new Rig(); var b = rig.Buffer; b.Begin(4);
        using var reader = b.OpenReader();
        var pending = Task.Run(() => reader.Read(new byte[10])); await Task.Delay(50);
        reader.Dispose(); reader.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        b.Writer.Write("done"u8); b.Finish();
        using var replacement = b.OpenReader(); var data = new byte[4];
        Assert.Equal(4, replacement.Read(data)); Assert.Equal("done", System.Text.Encoding.ASCII.GetString(data));
    }
    [Fact]
    public async Task InterruptedTransferIsAnError_AndReplacementCannotStitchDifferentAudio()
    {
        using var rig = new Rig(); var b = rig.Buffer; b.Begin(8);
        using var old = b.OpenReader(); b.Writer.Write("old!"u8);
        Assert.Equal(4, old.Read(new byte[8]));
        b.Writer.SetLength(0); b.Begin(4); b.Writer.Write("new!"u8);
        Assert.Throws<IOException>(() => old.Read(new byte[8]));
        using var reader = b.OpenReader(); Assert.Equal(4, reader.Read(new byte[8]));
        var pending = Task.Run(() => reader.Read(new byte[8])); await Task.Delay(50);
        b.Finish(new IOException("synthetic disconnect"));
        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(b.Complete);
    }
    [Fact]
    public void TruncatedOrSparseCompletionIsNeverSuccessful()
    {
        using var rig = new Rig(); var b = rig.Buffer; b.Begin(8);
        b.Writer.Position = 4; b.Writer.Write("hole"u8); b.Finish();
        Assert.False(b.Complete); using var reader = b.OpenReader();
        Assert.Throws<IOException>(() => reader.Read(new byte[8]));
    }
    [Fact]
    public void SeekingCannotReadBeyondTheContiguousPrefix_AndNoReaderImportsAFile()
    {
        using var rig = new Rig(); var b = rig.Buffer; b.Begin(12);
        b.Writer.Write("abcd"u8); b.Writer.Position = 8; b.Writer.Write("ijkl"u8);
        using var reader = b.OpenReader(); Assert.False(reader.Seek(8)); Assert.False(reader.Seek(-1));
        Assert.True(reader.Seek(2)); var data = new byte[8]; Assert.Equal(2, reader.Read(data));
        Assert.Equal("cd", System.Text.Encoding.ASCII.GetString(data, 0, 2));
        Assert.Single(Directory.GetFiles(rig.Data.DataDirectory));
    }
    [Fact]
    public async Task DisposalWakesBlockedReaders_AndVlcCallbacksNeverUnwindExceptions()
    {
        using var rig = new Rig(); using var input = new ProgressiveMediaInput(rig.Buffer);
        Assert.True(input.CanSeek); Assert.True(input.Open(out var size)); Assert.Equal(ulong.MaxValue, size);
        Assert.True(input.Seek(0)); // VLC needs the callback for native pause support.
        Assert.False(input.Seek(ulong.MaxValue)); // unknown tail is never read or waited for.
        var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(1024);
        try
        {
            var pending = Task.Run(() => input.Read(memory, 1024)); await Task.Delay(50);
            rig.Buffer.Dispose(); Assert.Equal(-1, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsType<ObjectDisposedException>(input.Error); Assert.False(input.Seek(0));
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
    }
}
