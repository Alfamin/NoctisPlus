using System.Text.Json;
using Noctis.TelegramSync;
using Xunit;

namespace Noctis.TelegramSync.Tests;

public sealed class UploadQueueTests
{
    private static (FakeTelegramDrive Drive, long Ch, string Dir) Setup()
    {
        var drive = new FakeTelegramDrive();
        var ch = drive.FindOrCreateChannelAsync(TgMarkers.Library, "Noctis Plus Library", TgMarkers.For(TgMarkers.Library, Guid.NewGuid())).Result.Id;
        var dir = Path.Combine(Path.GetTempPath(), $"tgu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return (drive, ch, dir);
    }

    private static string Song(string dir, string name, int size = 5000)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, Enumerable.Range(0, size).Select(i => (byte)i).ToArray());
        return p;
    }

    private static Dictionary<string, string> Meta(string title) => new() { ["title"] = title, ["artist"] = "A" };

    [Fact]
    public async Task Uploads_one_at_a_time_with_json_caption()
    {
        var (drive, ch, dir) = Setup();
        try
        {
            var q = new TgUploadQueue(drive, ch);
            q.Enqueue(Song(dir, "a.mp3"), Meta("One"));
            q.Enqueue(Song(dir, "b.mp3"), Meta("Two"));
            Assert.Equal(TgTransferResult.Done, await q.RunNextAsync());
            Assert.Equal(1, drive.Posts);
            Assert.Equal(1, q.Count);
            var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(q.LastPosted!.Caption)!;
            Assert.Equal("One", meta["title"]);
            Assert.Equal(5000, q.LastPosted.Document!.Size);
            Assert.Equal(TgTransferResult.Done, await q.RunNextAsync());
            Assert.Equal(2, drive.Posts);
            Assert.Equal(2, (await drive.ReadHistoryAsync(ch, 0)).Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Flood_wait_keeps_job_and_waits_for_retry_after()
    {
        var (drive, ch, dir) = Setup();
        try
        {
            var t = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
            drive.FailNextPosts = 1; drive.FloodWait = TimeSpan.FromSeconds(30);
            var q = new TgUploadQueue(drive, ch, now: () => t);
            q.Enqueue(Song(dir, "a.mp3"), Meta("One"));
            Assert.Equal(TgTransferResult.Waiting, await q.RunNextAsync());
            Assert.Equal(1, q.Count);
            t = t.AddSeconds(29);
            Assert.Equal(TgTransferResult.Waiting, await q.RunNextAsync());
            Assert.Equal(0, drive.Posts);
            t = t.AddSeconds(2);
            Assert.Equal(TgTransferResult.Done, await q.RunNextAsync());
            Assert.Equal(1, drive.Posts);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Policy_pauses_and_cancel_keeps_job_for_resume()
    {
        var (drive, ch, dir) = Setup();
        try
        {
            var allowed = false;
            var q = new TgUploadQueue(drive, ch, () => allowed);
            q.Enqueue(Song(dir, "a.mp3"), Meta("One"));
            Assert.Equal(TgTransferResult.Paused, await q.RunNextAsync());
            allowed = true;
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Equal(TgTransferResult.Cancelled, await q.RunNextAsync(cts.Token));
            Assert.Equal(1, q.Count);
            Assert.Equal(0, drive.Posts);
            Assert.Equal(TgTransferResult.Done, await q.RunNextAsync());
            Assert.Equal(1, drive.Posts);
            Assert.Equal(0, q.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Missing_source_is_dropped()
    {
        var (drive, ch, dir) = Setup();
        try
        {
            var q = new TgUploadQueue(drive, ch);
            q.Enqueue(Path.Combine(dir, "gone.mp3"), Meta("Gone"));
            Assert.Equal(TgTransferResult.Missing, await q.RunNextAsync());
            Assert.Equal(0, q.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Forwards_are_paced_to_one_per_two_seconds()
    {
        var (drive, ch, _) = Setup();
        var src = drive.AddFile(ch, "x.mp3", new byte[10]);
        var dst = (await drive.FindOrCreateChannelAsync(TgMarkers.Sync, "Other", TgMarkers.For(TgMarkers.Sync, Guid.NewGuid()))).Id;
        var t = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var p = new TgForwardPacer(drive, () => t);
        Assert.NotNull(await p.TryForwardAsync(ch, src.Id, dst));
        Assert.Null(await p.TryForwardAsync(ch, src.Id, dst));
        t = t.AddSeconds(1.9);
        Assert.Null(await p.TryForwardAsync(ch, src.Id, dst));
        t = t.AddSeconds(0.2);
        var f = await p.TryForwardAsync(ch, src.Id, dst);
        Assert.NotNull(f);
        Assert.Equal(src.Document!.DocumentId, f!.Document!.DocumentId);
    }
}
