using System;
using FreeMusicFinder;
using Noctis.TelegramSync;
using TL;
using Xunit;

namespace Noctis.Tests;

/// <summary>Cloud #260: the sync adapter's protocol rules, checked without Telegram (no account, no network).</summary>
public class WTelegramDriveTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(4096, 0, 4096)]
    [InlineData(WTelegramDrive.Chunk + 10, WTelegramDrive.Chunk, 10)]
    [InlineData(3L * WTelegramDrive.Chunk, 3L * WTelegramDrive.Chunk, 0)]
    public void Resume_AsksFromAChunkBoundary_AndSkipsWhatWasSaved(long offset, long aligned, long skip)
    {
        Assert.Equal((aligned, skip), WTelegramDrive.Resume(offset));
        Assert.Equal(0, WTelegramDrive.Chunk % 4096);
        Assert.Equal(0, 1024 * 1024 % WTelegramDrive.Chunk);
    }

    [Fact]
    public void History_AsksForThePostsJustAfterTheCursor()
    {
        Assert.Equal((101, -100, 100), WTelegramDrive.HistoryWindow(100, 100));
        Assert.Equal((1, -1, 0), WTelegramDrive.HistoryWindow(0, 0));
        Assert.Equal((6, -100, 5), WTelegramDrive.HistoryWindow(5, 500));
    }

    [Fact]
    public void TelegramErrors_BecomeTheSyncEnginesOwn()
    {
        var message = new TgMessage(-100, 7, "", new TgDocument(1, "a.json", 10), DateTime.UtcNow);
        var flood = Assert.IsType<TelegramFloodWaitException>(WTelegramDrive.Map(new RpcException(420, "FLOOD_WAIT_X", 37), null));
        Assert.Equal(TimeSpan.FromSeconds(37), flood.RetryAfter);
        Assert.True(Assert.IsType<TelegramFloodWaitException>(WTelegramDrive.Map(new RpcException(420, "FLOOD_PREMIUM_WAIT_X", 5), null)).Premium);
        var expired = Assert.IsType<TelegramFileReferenceExpiredException>(WTelegramDrive.Map(new RpcException(400, "FILE_REFERENCE_EXPIRED"), message));
        Assert.Equal((-100L, 7L), (expired.Peer, expired.MessageId));
        Assert.Null(WTelegramDrive.Map(new RpcException(400, "CHANNEL_PRIVATE"), message));
    }

    [Fact]
    public void APost_KeepsItsServerDate_FileAndCaption()
    {
        var date = new DateTime(2026, 11, 5, 8, 30, 15);
        var post = new Message
        {
            id = 42, date = date, message = "noctis-plus sync batch",
            media = new MessageMediaDocument { document = new Document { id = 9, size = 1234, attributes = [new DocumentAttributeFilename { file_name = "noctis-sync.json" }] } },
        };
        var converted = WTelegramDrive.Convert(post, -1001);
        Assert.Equal(new TgMessage(-1001, 42, "noctis-plus sync batch", new TgDocument(9, "noctis-sync.json", 1234), DateTime.SpecifyKind(date, DateTimeKind.Utc)), converted);
        Assert.Equal(DateTimeKind.Utc, converted.DateUtc.Kind);
    }

    [Fact]
    public void RequestIds_AreStablePerPost_AndDifferBetweenPosts()
    {
        Assert.Equal(TgRequestIds.For("batch:1", "pc#3"), TgRequestIds.For("batch:1", "pc#3"));
        Assert.NotEqual(TgRequestIds.For("batch:1", "pc#3"), TgRequestIds.For("batch:2", "pc#3"));
        Assert.NotEqual(0, TgRequestIds.For("song:1", "محسن چاوشی - امیر بی گزند.flac"));
    }
}
