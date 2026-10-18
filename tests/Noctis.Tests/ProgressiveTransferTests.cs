using Noctis.Features.ProgressivePlayback;
using Xunit;

namespace Noctis.Tests;

public class ProgressiveTransferTests
{
    private sealed class Rig(TimeSpan? duration = null) : IDisposable
    {
        internal readonly TestPersistenceService Data = new();
        internal ProgressiveTransfer Transfer = null!;
        internal Stream Writer = null!;
        internal string Part => Path.Combine(Data.DataDirectory, "scratch.fmf.part");
        internal string Target => Path.Combine(Data.DataDirectory, "song.flac");
        internal Rig Open(bool allow = true)
        {
            Transfer = new(Data.DataDirectory, "song", duration);
            Writer = Transfer.Attach(new FileStream(Part, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1));
            ((IProgressiveAudioWriter)Writer).SetFormatPolicy(_ => allow);
            ((IProgressiveAudioWriter)Writer).BeginAudio(1024 * 1024, "flac");
            return this;
        }
        public void Dispose() { Transfer.End(null, new IOException("test ended")); Data.Dispose(); }
    }
    private static byte[] Audio(int size = 512 * 1024)
    {
        var bytes = new byte[size]; "fLaC"u8.CopyTo(bytes); return bytes;
    }
    private static async Task<string> Ready(ProgressiveTransfer transfer)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (transfer.ReadyPath is not { } path)
        {
            Assert.True(DateTime.UtcNow < until, "partial audio never became ready");
            await Task.Delay(10);
        }
        return transfer.ReadyPath!;
    }

    [Fact]
    public async Task ProducerFinishAndRenameDoNotCloseAReader_AndLastLeaseReleasesTheFile()
    {
        using var rig = new Rig(TimeSpan.FromSeconds(120)).Open();
        rig.Writer.Write(Audio());
        Assert.Equal(rig.Target, await Ready(rig.Transfer)); Assert.False(File.Exists(rig.Target));
        using var lease = ProgressiveTransfer.Acquire(rig.Target)!;
        Assert.NotNull(lease); using var reader = lease.Buffer.OpenReader();
        Assert.Equal(512 * 1024, reader.Read(new byte[1024 * 1024]));
        rig.Writer.Write(new byte[512 * 1024]); File.Move(rig.Part, rig.Target);
        rig.Transfer.End(rig.Target);
        Assert.True(lease.Buffer.Complete);
        // This is TagLib's ordinary read/share-read pattern, which must work while
        // VLC still owns its lease. Holding the original write handle fails here.
        using (var metadata = File.Open(rig.Target, FileMode.Open, FileAccess.Read, FileShare.Read)) Assert.Equal(1024 * 1024, metadata.Length);
        Assert.Equal(512 * 1024, reader.Read(new byte[1024 * 1024])); Assert.Equal(0, reader.Read(new byte[1]));
        // A queued Media retains its already-acquired preparation lease even when the
        // producer finished between Play and the playback worker reaching the lock.
        using var retained = lease.Retain(); lease.Dispose();
        Assert.Throws<IOException>(() => File.Open(rig.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        retained.Dispose();
        using var exclusive = File.Open(rig.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(1024 * 1024, exclusive.Length);
        Assert.Null(ProgressiveTransfer.Acquire(rig.Target));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ProvisionalFormatOrUnknownHeaderNeverStartsEarly(bool allowed, bool validHeader)
    {
        using var rig = new Rig(TimeSpan.FromSeconds(120)).Open(allowed);
        rig.Writer.Write(validHeader ? Audio() : new byte[512 * 1024]); await Task.Delay(550);
        Assert.Null(rig.Transfer.ReadyPath); Assert.Null(ProgressiveTransfer.Acquire(rig.Target));
    }

    [Fact]
    public async Task UnknownDurationWaitsForTheValidatedCompleteFile()
    {
        using var rig = new Rig().Open(); rig.Writer.Write(Audio()); await Task.Delay(550);
        Assert.Null(rig.Transfer.ReadyPath);
        rig.Writer.Write(new byte[512 * 1024]); File.Move(rig.Part, rig.Target); rig.Transfer.End(rig.Target);
        Assert.Equal(rig.Target, rig.Transfer.ReadyPath); Assert.Null(ProgressiveTransfer.Acquire(rig.Target));
    }

    [Fact]
    public async Task BackupResetUnpublishesOldBytes_AndInvalidatesItsReader()
    {
        using var rig = new Rig(TimeSpan.FromSeconds(120)).Open(); rig.Writer.Write(Audio());
        await Ready(rig.Transfer); using var old = ProgressiveTransfer.Acquire(rig.Target)!;
        using var reader = old.Buffer.OpenReader();
        rig.Writer.SetLength(0);
        Assert.Null(rig.Transfer.ReadyPath); Assert.Null(ProgressiveTransfer.Acquire(rig.Target));
        Assert.Throws<IOException>(() => reader.Read(new byte[1]));
        ((IProgressiveAudioWriter)rig.Writer).BeginAudio(1024 * 1024, "flac");
        rig.Writer.Write(Audio()); Assert.Equal(rig.Target, await Ready(rig.Transfer));
        Assert.Throws<IOException>(() => reader.Read(new byte[1]));
    }

    [Fact]
    public async Task TransferFailureWakesTheDecoder_AndDoesNotPublishAPartialFile()
    {
        using var rig = new Rig(TimeSpan.FromSeconds(120)).Open(); rig.Writer.Write(Audio());
        await Ready(rig.Transfer); using var lease = ProgressiveTransfer.Acquire(rig.Target)!;
        using var reader = lease.Buffer.OpenReader(); reader.Read(new byte[512 * 1024]);
        var read = Task.Run(() => reader.Read(new byte[1]));
        rig.Transfer.End(null, new IOException("synthetic offline"));
        await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(rig.Transfer.ReadyPath); Assert.False(File.Exists(rig.Target));
        lease.Dispose(); File.Delete(rig.Part); Assert.False(File.Exists(rig.Part));
    }
}
