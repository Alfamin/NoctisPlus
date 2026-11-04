using System.IO.Compression;
using System.Text;
using Noctis.TelegramSync;
using Xunit;

namespace Noctis.TelegramSync.Tests;

public sealed class RecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisSyncRecovery", Guid.NewGuid().ToString("N"));
    public RecoveryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string PathOf(string name) => Path.Combine(_root, name);
    private static async Task<TgChannel> Channel(FakeTelegramDrive drive) => await drive.FindOrCreateChannelAsync(
        TgMarkers.Sync, "Test", TgCreatorMarker.For(TgMarkers.Sync, Guid.NewGuid(), drive.SelfUserId));
    private static TgSyncLog Log(FakeTelegramDrive drive, long channel, string device, string? path = null) =>
        new(drive, channel, device, new TgLedger(), cursorPath: path) { MinPostInterval = TimeSpan.Zero };

    [Fact]
    public async Task AmbiguousPostRetriesTheExactReservedBatch_AndKeepsNewEdits()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive);
        var sender = Log(drive, channel.Id, "pc", PathOf("state.json"));
        sender.Record("track", "one", "{}");
        drive.AfterPost = () => { drive.AfterPost = null; throw new TimeoutException("reply lost after acceptance"); };
        await Assert.ThrowsAsync<TimeoutException>(() => sender.FlushAsync());
        sender.Record("track", "two", "{}");
        Assert.True(await sender.FlushAsync());
        Assert.Equal(1, sender.PendingCount);
        Assert.True(await sender.FlushAsync());
        var reader = Log(drive, channel.Id, "phone");
        Assert.Equal(2, await reader.PullAsync());
        Assert.Equal(2, reader.Cursor.Contiguous["pc"]);
        Assert.False(reader.Cursor.Above.ContainsKey("pc"));
        Assert.Equal(2, reader.Ledger.Count);
    }

    [Fact]
    public async Task RestartRestoresTheLedgerCursorAndInterruptedBatchTogether()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive); var path = PathOf("state.json");
        var sender = Log(drive, channel.Id, "pc", path);
        sender.Record("track", "one", "{}");
        drive.AfterPost = () => { drive.AfterPost = null; throw new OperationCanceledException(); };
        await Assert.ThrowsAsync<OperationCanceledException>(() => sender.FlushAsync());
        var restarted = Log(drive, channel.Id, "pc", path);
        Assert.Equal(1, restarted.Ledger.Count);
        Assert.Equal(1, restarted.PendingCount);
        Assert.True(await restarted.FlushAsync());
        var readerPath = PathOf("reader.json");
        var reader = Log(drive, channel.Id, "phone", readerPath);
        await reader.PullAsync();
        var reopened = Log(drive, channel.Id, "phone", readerPath);
        Assert.Equal(reader.Cursor.LastMessageId, reopened.Cursor.LastMessageId);
        Assert.Equal(reader.Ledger.Fingerprint(), reopened.Ledger.Fingerprint());
        Assert.Equal(0, await reopened.PullAsync());
        Assert.Equal(1, reopened.Ledger.Count);
    }

    [Fact]
    public async Task PullWaitsForFlush_WhileRecordingDoesNotLoseTheNewEdit()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive);
        var sender = Log(drive, channel.Id, "pc", PathOf("state.json"));
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        drive.BeforePostAsync = () => { entered.SetResult(); return release.Task; };
        sender.Record("track", "one", "{}");
        var flush = sender.FlushAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pull = sender.PullAsync(); Assert.False(pull.IsCompleted);
        sender.Record("track", "two", "{}"); release.SetResult();
        await flush; await pull;
        Assert.Equal(1, sender.PendingCount);
        drive.BeforePostAsync = null;
        Assert.True(await sender.FlushAsync());
        Assert.Equal(2, sender.Ledger.Count);
    }

    [Fact]
    public async Task SnapshotPreservesHighestSeenSequenceAboveAnOldGap()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive);
        var sender = Log(drive, channel.Id, "pc");
        sender.Cursor.Mark("pc", 5);
        sender.Record("track", "one", "{}");
        await sender.PostSnapshotAsync();
        var reinstall = Log(drive, channel.Id, "pc");
        Assert.True(await reinstall.LoadSnapshotAsync());
        Assert.Equal(5, reinstall.Cursor.OwnSeq);
        reinstall.Record("track", "two", "{}");
        await reinstall.FlushAsync();
        Assert.Equal(6, reinstall.Cursor.OwnSeq);
    }

    [Fact]
    public async Task SameTickLocalEditsKeepTheLatestValueOnBothDevices()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive);
        var sender = Log(drive, channel.Id, "pc"); var at = DateTime.UtcNow;
        sender.Record("track", "one", "{\"liked\":true}", at);
        sender.Record("track", "one", "{\"liked\":false}", at);
        await sender.FlushAsync();
        var reader = Log(drive, channel.Id, "phone"); await reader.PullAsync();
        Assert.Equal("{\"liked\":false}", reader.Ledger.Get("track", "one")!.Payload);
        Assert.Equal(sender.Ledger.Fingerprint(), reader.Ledger.Fingerprint());
    }

    [Fact]
    public async Task AccountGuardRejectsMissingCreator_AndRechecksTheReplacement()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive); var guard = new TgSyncGuard(drive);
        Assert.Equal(TgGuardStatus.WrongAccount, await guard.CheckAccountAsync(channel with { Marker = TgMarkers.For(TgMarkers.Sync, Guid.NewGuid()) }));
        var replacement = channel with { Id = channel.Id - 1, Marker = TgCreatorMarker.For(TgMarkers.Sync, Guid.NewGuid(), 99) };
        var calls = 0;
        var result = await guard.RunAsync(channel, c => { calls++; throw new TelegramChannelGoneException(c.Id); }, () => Task.FromResult(replacement));
        Assert.Equal(TgGuardStatus.WrongAccount, result.Status);
        Assert.Equal(replacement, result.Channel); Assert.Equal(1, calls);
        await Assert.ThrowsAsync<TelegramChannelGoneException>(() => guard.RunAsync(channel,
            _ => throw new TelegramChannelGoneException(12345), () => throw new Exception("must not recreate another channel")));
    }

    [Fact]
    public async Task GuardPassesCancellationIntoWork()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive); using var stop = new CancellationTokenSource();
        await new TgSyncGuard(drive).RunAsync(channel, (_, ct) => { Assert.Equal(stop.Token, ct); return Task.CompletedTask; },
            _ => throw new Exception("not needed"), stop.Token);
    }

    [Fact]
    public void AliasMapsPersist_DeletedLocalFilesAndEmptyKeysDoNotBlockDownloads()
    {
        var path = PathOf("map.json"); var song = PathOf("song.mp3"); File.WriteAllBytes(song, new byte[10]);
        var local = new[] { new TgLocalFile("local", song, 10, "artist/title") };
        var map = new TgTrackMap(path);
        Assert.Equal("local", map.LinkOrNull(new(1, "song.mp3", 10), "artist/title", local));
        Assert.Equal("local", map.LinkOrNull(new(2, "song.mp3", 10), "artist/title", local));
        map.Save(); map = new TgTrackMap(path);
        Assert.Equal("local", map.TrackFor(1)); Assert.Equal("local", map.TrackFor(2));
        File.Delete(song);
        Assert.Null(map.LinkOrNull(new(2, "song.mp3", 10), "artist/title", local));
        Assert.Null(map.TrackFor(2));
        Assert.Null(map.LinkOrNull(new(3, "unknown.mp3", 10), "", local));
        File.WriteAllText(path, "{\"V\":1,\"Map\":{\"1\":null,\"-2\":\"bad\"}}");
        Assert.Equal(0, new TgTrackMap(path).Count);
    }

    [Fact]
    public void CodecDropsMalformedItems_AndBoundsCompressedExpansion()
    {
        var json = "{\"v\":1,\"device\":\"pc\",\"seq\":1,\"items\":[null,{\"kind\":\"track\",\"id\":\"one\",\"payload\":null}]}";
        Assert.Empty(BatchCodec.Decode(Encoding.UTF8.GetBytes(json))!.Items);
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, true))
            gzip.Write(new byte[BatchCodec.MaxJsonBytes + 1]);
        Assert.Null(BatchCodec.Decode(bytes.ToArray()));
    }

    [Fact]
    public async Task CaptionSupportsPersian_AndOversizedDetailsDoNotJamUploads()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive);
        var song = PathOf("song.mp3"); File.WriteAllBytes(song, new byte[10]);
        var persian = string.Concat(Enumerable.Repeat("آهنگ فارسی ", 25));
        var caption = TgUploadQueue.Caption(new Dictionary<string, string> { ["title"] = persian });
        Assert.Contains("آهنگ", caption); Assert.True(caption.Length < 1024);
        var queue = new TgUploadQueue(drive, channel.Id) { MinGap = TimeSpan.Zero };
        queue.Enqueue(song, new Dictionary<string, string> { ["title"] = new string('x', 1100) });
        Assert.Equal(TgTransferResult.Failed, await queue.RunNextAsync());
        Assert.Equal(0, queue.Count); Assert.NotNull(queue.LastError);
        queue.Enqueue(song, new Dictionary<string, string> { ["title"] = persian });
        Assert.Equal(TgTransferResult.Done, await queue.RunNextAsync()); Assert.Equal(1, drive.Posts);
    }

    [Fact]
    public async Task ConcurrentUploadCallsAndDuplicateEnqueuesPostOnlyOnce()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive);
        var song = PathOf("song.mp3"); File.WriteAllBytes(song, new byte[10]);
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        drive.BeforePostAsync = () => { entered.SetResult(); return release.Task; };
        var queue = new TgUploadQueue(drive, channel.Id);
        queue.Enqueue(song, new Dictionary<string, string>()); queue.Enqueue(song, new Dictionary<string, string>());
        var upload = queue.RunNextAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(TgTransferResult.Paused, await queue.RunNextAsync());
        release.SetResult(); Assert.Equal(TgTransferResult.Done, await upload);
        queue.Enqueue(song, new Dictionary<string, string>());
        Assert.Equal(0, queue.Count); Assert.Equal(1, drive.Posts);
    }

    [Fact]
    public async Task PartialFilesResumeAtAlignedOffsets_AndReturnCollisionPaths()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive);
        var data = Enumerable.Range(0, 12000).Select(i => (byte)i).ToArray();
        var msg = drive.AddFile(channel.Id, "song.mp3", data); var target = PathOf("song.mp3");
        File.WriteAllBytes(target + ".part", data[..4100]); File.WriteAllBytes(target, [7]);
        drive.OnDownloadOffset = offset => Assert.Equal(4096, offset);
        var queue = new TgTransferQueue(drive); queue.Enqueue(msg, target);
        Assert.Equal(TgTransferResult.Done, await queue.RunNextAsync());
        Assert.Equal(PathOf("song (2).mp3"), queue.LastSavedPath);
        Assert.Equal(data, File.ReadAllBytes(queue.LastSavedPath!)); Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task ReplacingAnUploadedFileAtTheSamePathIsNotSuppressedForever()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive);
        var song = PathOf("updated.mp3"); File.WriteAllBytes(song, new byte[10]);
        var queue = new TgUploadQueue(drive, channel.Id) { MinGap = TimeSpan.Zero };
        queue.Enqueue(song, new Dictionary<string, string>()); await queue.RunNextAsync();
        queue.Enqueue(song, new Dictionary<string, string>()); Assert.Equal(0, queue.Count);
        File.WriteAllBytes(song, new byte[20]);
        queue.Enqueue(song, new Dictionary<string, string>()); Assert.Equal(1, queue.Count);
        Assert.Equal(TgTransferResult.Done, await queue.RunNextAsync()); Assert.Equal(2, drive.Posts);
    }

    [Fact]
    public async Task RepeatedSizeMismatchFailsThatJob_AndAllowsTheNextSong()
    {
        var drive = new FakeTelegramDrive(); var channel = await Channel(drive);
        var msg = drive.AddFile(channel.Id, "song.mp3", [1, 2, 3]);
        var wrong = msg with { Document = msg.Document! with { Size = 20 } };
        var queue = new TgTransferQueue(drive); queue.Enqueue(wrong, PathOf("wrong.mp3")); queue.Enqueue(msg, PathOf("good.mp3"));
        Assert.Equal(TgTransferResult.Paused, await queue.RunNextAsync());
        Assert.Equal(TgTransferResult.Failed, await queue.RunNextAsync());
        Assert.Equal(TgTransferResult.Done, await queue.RunNextAsync()); Assert.Equal(0, queue.Count);
    }
}
