using Noctis.Services.Sync;
using Noctis.TelegramSync;
using Xunit;

namespace Noctis.TelegramSync.Tests;

/// <summary>
/// Cloud #260, review M2 and M7. Devices with different clocks that read the same posts at different times
/// must store the same state (fingerprints equal), and a device id's play counter must never go backwards.
/// </summary>
public sealed class ConvergenceTests
{
    private static readonly DateTime T0 = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    private sealed class World
    {
        public DateTime Now = T0; // the real (server) time
        public readonly FakeTelegramDrive Drive = new();
        public readonly long Channel;
        public World()
        {
            Drive.Clock = () => Now;
            Channel = Drive.FindOrCreateChannelAsync(TgMarkers.Sync, "Noctis Plus Sync", TgMarkers.For(TgMarkers.Sync, Guid.NewGuid())).Result.Id;
        }
        public void Advance(TimeSpan by) => Now += by;
        public TgSyncLog Device(string name, TimeSpan skew = default, string? path = null)
            => new(Drive, Channel, name, new TgLedger(() => Now + skew), () => Now + skew, path) { MinPostInterval = TimeSpan.FromSeconds(5) };
    }

    private static TrackSyncState Rating(int stars) => new(false, stars, false, 0, null, null);
    private static int RatingOf(TgSyncLog d, string id) => SyncJson.Deserialize<TrackSyncState>(d.Ledger.Get(TgKinds.Track, id)!.Payload)!.Rating;

    [Fact]
    public async Task OnePost_ReadAtDifferentTimes_ByDifferentClocks_IsStoredTheSameEverywhere()
    {
        var w = new World();
        var fast = w.Device("fast", TimeSpan.FromHours(2));
        var a = w.Device("a");
        var slow = w.Device("slow", -TimeSpan.FromHours(1));
        var ahead = w.Device("ahead", TimeSpan.FromMinutes(30));
        fast.RecordJson(TgKinds.Track, "tg:1", Rating(3));
        Assert.True(await fast.FlushAsync());

        await a.PullAsync();                       // right away
        w.Advance(TimeSpan.FromMinutes(40));
        await ahead.PullAsync();                   // 40 min later, its clock 30 min fast
        w.Advance(TimeSpan.FromHours(3));
        await slow.PullAsync();                    // hours later, its clock an hour slow
        await fast.PullAsync();                    // the writer reads its own post back

        var stamp = a.Ledger.Get(TgKinds.Track, "tg:1")!.UpdatedUtc;
        Assert.Equal(T0 + SyncStore.MaxClockSkew, stamp); // bounded by the post's server date, not by any reader
        foreach (var d in new[] { ahead, slow, fast }) Assert.Equal(a.Ledger.Fingerprint(), d.Ledger.Fingerprint());
    }

    [Fact]
    public async Task AFastWritersOldEdit_DoesNotBeatACorrectClocksLaterEdit_OnAnyDevice()
    {
        var w = new World();
        var fast = w.Device("zz-fast", TimeSpan.FromDays(1)); // highest id: it would also win every tie
        var ok = w.Device("ok");
        var late = w.Device("late");
        fast.RecordJson(TgKinds.Track, "tg:1", Rating(1));
        await fast.FlushAsync();
        w.Advance(TimeSpan.FromMinutes(5));
        await ok.PullAsync();
        ok.RecordJson(TgKinds.Track, "tg:1", Rating(5));
        await ok.FlushAsync();
        w.Advance(TimeSpan.FromMinutes(10));
        await fast.PullAsync(); await late.PullAsync(); await ok.PullAsync();

        foreach (var d in new[] { fast, ok, late }) Assert.Equal(5, RatingOf(d, "tg:1"));
        Assert.Equal(ok.Ledger.Fingerprint(), fast.Ledger.Fingerprint());
        Assert.Equal(ok.Ledger.Fingerprint(), late.Ledger.Fingerprint());
    }

    [Fact]
    public async Task EqualStampsStillTieBreakByDevice_AndConcurrentEditsConverge()
    {
        var w = new World();
        var a = w.Device("a", TimeSpan.FromHours(1)); var b = w.Device("b", TimeSpan.FromHours(3));
        a.RecordJson(TgKinds.Track, "tg:2", Rating(2));
        b.RecordJson(TgKinds.Track, "tg:2", Rating(4));
        await a.FlushAsync(); await b.FlushAsync(); // same server second: both bounded to the same stamp
        w.Advance(TimeSpan.FromSeconds(30));
        await a.PullAsync(); await b.PullAsync();
        var c = w.Device("c"); await c.PullAsync();
        Assert.Equal(4, RatingOf(a, "tg:2")); // tie: highest device id
        Assert.Equal(a.Ledger.Fingerprint(), b.Ledger.Fingerprint());
        Assert.Equal(a.Ledger.Fingerprint(), c.Ledger.Fingerprint());
    }

    [Fact]
    public async Task TwoEditsBeforeOnePost_OnAFastClock_TheLaterEditWinsEverywhere()
    {
        var w = new World();
        var fast = w.Device("fast", TimeSpan.FromHours(5));
        var other = w.Device("other");
        fast.RecordJson(TgKinds.Track, "tg:3", Rating(1));
        fast.RecordJson(TgKinds.Track, "tg:3", Rating(2), T0); // explicit older stamp: still the newer edit of this device
        Assert.Equal(1, fast.PendingCount);                       // only the newest unposted edit is sent
        await fast.FlushAsync();
        await other.PullAsync(); await fast.PullAsync();
        Assert.Equal(2, RatingOf(other, "tg:3"));
        Assert.Equal(other.Ledger.Fingerprint(), fast.Ledger.Fingerprint());
    }

    [Fact]
    public async Task ReplyLostAfterAcceptance_RetryCopyIsIgnored_AndTheWriterAdoptsTheFirstCopysStamp()
    {
        var w = new World();
        var fast = w.Device("fast", TimeSpan.FromHours(2));
        var reader = w.Device("reader");
        fast.RecordJson(TgKinds.Track, "tg:4", Rating(4));
        w.Drive.AfterPost = () => { w.Drive.AfterPost = null; throw new TimeoutException("reply lost after acceptance"); };
        await Assert.ThrowsAsync<TimeoutException>(() => fast.FlushAsync());
        w.Advance(TimeSpan.FromMinutes(10));
        Assert.True(await fast.FlushAsync()); // same seq and payload, a later server date
        await reader.PullAsync(); await fast.PullAsync();
        Assert.Equal(T0 + SyncStore.MaxClockSkew, reader.Ledger.Get(TgKinds.Track, "tg:4")!.UpdatedUtc);
        Assert.Equal(reader.Ledger.Fingerprint(), fast.Ledger.Fingerprint());
    }

    [Fact]
    public async Task SnapshotThenLog_EqualsLogOnly_ForAFastWriter()
    {
        var w = new World();
        var fast = w.Device("fast", TimeSpan.FromHours(2));
        var keeper = w.Device("keeper");
        fast.RecordJson(TgKinds.Track, "tg:5", Rating(1));
        await fast.FlushAsync();
        w.Advance(TimeSpan.FromMinutes(1));
        await keeper.PullAsync();
        fast.RecordJson(TgKinds.Track, "tg:6", Rating(2)); // pending while the snapshot is taken
        w.Advance(TimeSpan.FromMinutes(30));
        await keeper.PostSnapshotAsync();
        w.Advance(TimeSpan.FromMinutes(30));
        await fast.FlushAsync();
        w.Advance(TimeSpan.FromMinutes(1));

        var fresh = w.Device("fresh"); Assert.True(await fresh.LoadSnapshotAsync()); await fresh.PullAsync();
        var replay = w.Device("replay"); await replay.PullAsync();
        await keeper.PullAsync(); await fast.PullAsync();
        Assert.Equal(replay.Ledger.Fingerprint(), fresh.Ledger.Fingerprint());
        Assert.Equal(replay.Ledger.Fingerprint(), keeper.Ledger.Fingerprint());
        Assert.Equal(replay.Ledger.Fingerprint(), fast.Ledger.Fingerprint());
    }

    // ── M7 ──

    [Fact]
    public async Task PlaysBeforeTheFirstPull_AddToTheCounterTheLogAlreadyHas()
    {
        var w = new World();
        var phone = w.Device("phone-1");
        for (var i = 0; i < 40; i++) phone.RecordPlay("tg:7");
        Assert.Equal(0, phone.PendingCount); // nothing written before the log was read
        await phone.PullAsync();
        await phone.FlushAsync();
        Assert.Equal(40, TgLibraryView.PlayCount(phone.Ledger, "tg:7"));

        // The same device id with its saved state lost (the risky case M7 describes).
        var again = w.Device("phone-1");
        again.RecordPlay("tg:7");
        Assert.False(again.Bootstrapped);
        Assert.Equal(0, again.PendingCount);
        w.Advance(TimeSpan.FromSeconds(10));
        await again.PullAsync(); // learns its own counter (40), then adds the early play
        await again.FlushAsync();
        w.Advance(TimeSpan.FromSeconds(10));
        var pc = w.Device("pc"); await pc.PullAsync();
        Assert.Equal(41, TgLibraryView.PlayCount(pc.Ledger, "tg:7"));
    }

    [Fact]
    public async Task EarlyPlays_SurviveARestartBeforeTheFirstPull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tg-m7-{Guid.NewGuid():N}.json");
        try
        {
            var w = new World();
            var phone = w.Device("phone", path: path);
            phone.RecordPlay("tg:8"); phone.RecordPlay("tg:8");
            var restarted = w.Device("phone", path: path);
            Assert.False(restarted.Bootstrapped);
            await restarted.PullAsync();
            Assert.True(restarted.Bootstrapped);
            Assert.Equal(2, TgLibraryView.PlayCount(restarted.Ledger, "tg:8"));
            restarted.RecordPlay("tg:8");
            Assert.Equal(3, TgLibraryView.PlayCount(restarted.Ledger, "tg:8"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EachInstallGetsItsOwnDeviceId_AndKeepsItAcrossRestarts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tg-id-{Guid.NewGuid():N}");
        try
        {
            var first = TgDeviceIdentity.LoadOrCreate(Path.Combine(dir, "device.json"), "phone");
            Assert.Equal(first, TgDeviceIdentity.LoadOrCreate(Path.Combine(dir, "device.json"), "phone"));
            var reinstall = TgDeviceIdentity.LoadOrCreate(Path.Combine(dir, "other", "device.json"), "phone");
            Assert.NotEqual(first, reinstall);
            Assert.StartsWith("phone-", first);
            Assert.DoesNotContain(TgSyncLog.SnapshotDeviceSeparator, first);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public sealed class RequestIdTests
{
    [Fact]
    public async Task ARetriedBatch_IsPostedWithTheSameRequestId_AcrossARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tg-rid-{Guid.NewGuid():N}.json");
        try
        {
            var drive = new RecordingDrive();
            var channel = (await drive.FindOrCreateChannelAsync(TgMarkers.Sync, "s", TgMarkers.For(TgMarkers.Sync, Guid.NewGuid()))).Id;
            var log = new TgSyncLog(drive, channel, "pc", new TgLedger(), cursorPath: path) { MinPostInterval = TimeSpan.Zero };
            log.Record(TgKinds.Track, "tg:1", "{}");
            drive.AfterPost = () => { drive.AfterPost = null; throw new TimeoutException("reply lost"); };
            await Assert.ThrowsAsync<TimeoutException>(() => log.FlushAsync());
            var restarted = new TgSyncLog(drive, channel, "pc", new TgLedger(), cursorPath: path) { MinPostInterval = TimeSpan.Zero };
            Assert.True(await restarted.FlushAsync());
            Assert.Equal(2, drive.Ids.Count);
            Assert.Equal(drive.Ids[0], drive.Ids[1]);
            Assert.NotEqual(TgRequestIds.For("batch:" + channel, "pc#1"), TgRequestIds.For("batch:" + channel, "pc#2"));
        }
        finally { File.Delete(path); if (File.Exists(path + ".bak")) File.Delete(path + ".bak"); }
    }

    private sealed class RecordingDrive : ITelegramDrive
    {
        private readonly FakeTelegramDrive _inner = new();
        public List<long> Ids { get; } = new();
        public Action? AfterPost { get => _inner.AfterPost; set => _inner.AfterPost = value; }
        public Task<long> GetSelfUserIdAsync(CancellationToken ct = default) => _inner.GetSelfUserIdAsync(ct);
        public Task<TgChannel> FindOrCreateChannelAsync(string role, string title, string markerIfNew, CancellationToken ct = default) => _inner.FindOrCreateChannelAsync(role, title, markerIfNew, ct);
        public Task<TgMessage> PostDocumentAsync(long channel, string fileName, Stream content, string caption, IProgress<long>? progress = null, CancellationToken ct = default)
            => throw new InvalidOperationException("posts must carry a request id");
        public Task<TgMessage> PostDocumentAsync(long channel, string fileName, Stream content, string caption, long requestId, IProgress<long>? progress = null, CancellationToken ct = default)
        { Ids.Add(requestId); return _inner.PostDocumentAsync(channel, fileName, content, caption, progress, ct); }
        public Task<IReadOnlyList<TgMessage>> ReadHistoryAsync(long channel, long afterId, int limit = 100, CancellationToken ct = default) => _inner.ReadHistoryAsync(channel, afterId, limit, ct);
        public Task<TgMessage?> GetMessageAsync(long peer, long id, CancellationToken ct = default) => _inner.GetMessageAsync(peer, id, ct);
        public Task<TgMessage?> GetPinnedAsync(long channel, CancellationToken ct = default) => _inner.GetPinnedAsync(channel, ct);
        public Task PinAsync(long channel, long id, CancellationToken ct = default) => _inner.PinAsync(channel, id, ct);
        public Task DeleteMessagesAsync(long channel, IReadOnlyList<long> ids, CancellationToken ct = default) => _inner.DeleteMessagesAsync(channel, ids, ct);
        public Task DownloadAsync(TgMessage message, Stream destination, long offset = 0, IProgress<long>? progress = null, CancellationToken ct = default) => _inner.DownloadAsync(message, destination, offset, progress, ct);
        public Task<TgMessage> ForwardAsync(long fromPeer, long id, long toChannel, CancellationToken ct = default) => _inner.ForwardAsync(fromPeer, id, toChannel, ct);
    }
}
