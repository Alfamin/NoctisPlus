using Noctis.TelegramSync;
using Xunit;

namespace Noctis.TelegramSync.Tests;

public sealed class SyncGuardTests
{
    private static async Task<TgChannel> Make(FakeTelegramDrive d) =>
        await d.FindOrCreateChannelAsync(TgMarkers.Sync, "Noctis Sync", TgCreatorMarker.For(TgMarkers.Sync, Guid.NewGuid(), await d.GetSelfUserIdAsync()));

    [Fact]
    public async Task Refuses_other_account()
    {
        var d = new FakeTelegramDrive { SelfUserId = 42 };
        var ch = await Make(d);
        Assert.Equal(42, TgCreatorMarker.Creator(ch.Marker));
        d.SelfUserId = 99;
        var ran = false;
        var (status, _) = await new TgSyncGuard(d).RunAsync(ch, _ => { ran = true; return Task.CompletedTask; }, () => Make(d));
        Assert.Equal(TgGuardStatus.WrongAccount, status);
        Assert.False(ran);
    }

    [Fact]
    public async Task Recreates_deleted_channel_once_with_snapshot()
    {
        var d = new FakeTelegramDrive();
        var ch = await Make(d);
        d.DeleteChannel(ch.Id);
        var recreated = 0;
        async Task<TgChannel> Recreate()
        {
            recreated++;
            var c = await Make(d);
            await d.PostDocumentAsync(c.Id, "snapshot.json", new MemoryStream(new byte[] { 1 }), "snapshot");
            return c;
        }
        var (status, fresh) = await new TgSyncGuard(d).RunAsync(ch, c => d.ReadHistoryAsync(c.Id, 0), Recreate);
        Assert.Equal(TgGuardStatus.ChannelRecreated, status);
        Assert.Equal(1, recreated);
        Assert.NotEqual(ch.Id, fresh.Id);
        Assert.Single(await d.ReadHistoryAsync(fresh.Id, 0));
    }

    [Fact]
    public async Task Gone_again_stops_after_one_recreate()
    {
        var d = new FakeTelegramDrive();
        var ch = await Make(d);
        var recreated = 0;
        var (status, _) = await new TgSyncGuard(d).RunAsync(ch, c => throw new TelegramChannelGoneException(c.Id),
            () => { recreated++; return Make(d); });
        Assert.Equal(TgGuardStatus.ChannelGoneAgain, status);
        Assert.Equal(1, recreated);
    }

    [Fact]
    public async Task Signed_out_stops_cleanly()
    {
        var d = new FakeTelegramDrive();
        var ch = await Make(d);
        d.Revoked = true;
        var (status, _) = await new TgSyncGuard(d).RunAsync(ch, c => d.ReadHistoryAsync(c.Id, 0), () => Make(d));
        Assert.Equal(TgGuardStatus.SignedOut, status);
        Assert.Equal(TgGuardStatus.SignedOut, await new TgSyncGuard(d).CheckAccountAsync(ch));
    }
}
