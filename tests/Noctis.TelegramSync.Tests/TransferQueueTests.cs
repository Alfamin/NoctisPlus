using Noctis.TelegramSync;
using Xunit;

namespace Noctis.TelegramSync.Tests;

public sealed class TransferQueueTests
{
    private static (FakeTelegramDrive Drive, TgMessage Msg, byte[] Data, string Dir) Setup()
    {
        var drive = new FakeTelegramDrive { ChunkSize = 1000 };
        var ch = drive.FindOrCreateChannelAsync(TgMarkers.Library, "Noctis Plus Library", TgMarkers.For(TgMarkers.Library, Guid.NewGuid())).Result.Id;
        var data = Enumerable.Range(0, 10_500).Select(i => (byte)i).ToArray();
        var msg = drive.AddFile(ch, "song.mp3", data);
        var dir = Path.Combine(Path.GetTempPath(), $"tgq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return (drive, msg, data, dir);
    }

    [Fact]
    public async Task Cancelled_download_resumes_from_the_part_file_and_lands_whole()
    {
        var (drive, msg, data, dir) = Setup();
        try
        {
            var target = Path.Combine(dir, "song.mp3");
            using var cts = new CancellationTokenSource();
            drive.OnChunk = at => { if (at >= 4000) cts.Cancel(); };
            var q = new TgTransferQueue(drive);
            q.Enqueue(msg, target);
            Assert.Equal(TgTransferResult.Cancelled, await q.RunNextAsync(cts.Token));
            Assert.False(File.Exists(target));
            Assert.Equal(4000, new FileInfo(target + ".part").Length);

            drive.OnChunk = null;
            Assert.Equal(TgTransferResult.Done, await q.RunNextAsync());
            Assert.Equal(data, File.ReadAllBytes(target));
            Assert.False(File.Exists(target + ".part"));
            Assert.Equal(0, q.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Policy_pauses_and_expired_reference_is_re_read()
    {
        var (drive, msg, data, dir) = Setup();
        try
        {
            var onWifi = false;
            var q = new TgTransferQueue(drive, () => onWifi);
            var target = Path.Combine(dir, "song.mp3");
            q.Enqueue(msg, target);
            Assert.Equal(TgTransferResult.Paused, await q.RunNextAsync());
            onWifi = true;
            drive.ExpiredReferences.Add(msg.Id);
            Assert.Equal(TgTransferResult.Done, await q.RunNextAsync());
            Assert.Equal(data, File.ReadAllBytes(target));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Flood_wait_during_download_keeps_the_job_and_waits_without_calling()
    {
        var (drive, msg, data, dir) = Setup();
        try
        {
            var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
            drive.FailNextDownloads = 1;
            drive.FloodWait = TimeSpan.FromSeconds(60);
            var q = new TgTransferQueue(drive, now: () => now);
            var target = Path.Combine(dir, "song.mp3");
            q.Enqueue(msg, target);
            Assert.Equal(TgTransferResult.Waiting, await q.RunNextAsync());
            now = now.AddSeconds(30);
            Assert.Equal(TgTransferResult.Waiting, await q.RunNextAsync());
            Assert.Equal(1, q.Count);
            now = now.AddSeconds(31);
            Assert.Equal(TgTransferResult.Done, await q.RunNextAsync());
            Assert.Equal(data, File.ReadAllBytes(target));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Deleted_song_message_is_reported_missing_without_touching_files()
    {
        var (drive, msg, _, dir) = Setup();
        try
        {
            await drive.DeleteMessagesAsync(msg.Peer, [msg.Id]);
            drive.ExpiredReferences.Add(msg.Id);
            var q = new TgTransferQueue(drive);
            q.Enqueue(msg, Path.Combine(dir, "song.mp3"));
            Assert.Equal(TgTransferResult.Missing, await q.RunNextAsync());
            Assert.Equal(0, q.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Existing_target_does_not_block_the_queue()
    {
        var (drive, msg, data, dir) = Setup();
        try
        {
            var target = Path.Combine(dir, "song.mp3");
            File.WriteAllBytes(target, data); // already downloaded
            var other = drive.AddFile(msg.Peer, "other.mp3", data);
            var otherTarget = Path.Combine(dir, "other.mp3");
            File.WriteAllBytes(otherTarget, new byte[] { 1, 2, 3 }); // a different file holds the name
            var q = new TgTransferQueue(drive);
            q.Enqueue(msg, target);
            q.Enqueue(other, otherTarget);
            Assert.Equal(TgTransferResult.Done, await q.RunNextAsync());
            Assert.Equal(TgTransferResult.Done, await q.RunNextAsync());
            Assert.Equal(0, q.Count);
            Assert.Equal(data, File.ReadAllBytes(target));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(otherTarget));
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(dir, "other (2).mp3")));
            Assert.Empty(Directory.GetFiles(dir, "*.part"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
