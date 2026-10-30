using System.Buffers.Binary;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

public class OnlineTimingDurationTests
{
    [Fact]
    public async Task LiveMrRagerSourcesAcceptThePartialRecordingDuration()
    {
        var source = Environment.GetEnvironmentVariable("NOCTIS_TIMING_LIVE_RAGER");
        if (string.IsNullOrEmpty(source)) return;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".flac");
        try
        {
            using (var input = File.OpenRead(source))
            {
                var prefix = new byte[65536]; var count = await input.ReadAsync(prefix);
                await File.WriteAllBytesAsync(path, prefix[..count]);
            }
            var seconds = OnlineTimingDuration.Seconds(new Track { FilePath = path, Duration = TimeSpan.FromSeconds(298) });
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var red = new Noctis.Services.LrcRedLyricsService(http).SearchLyricsAsync("Kid Cudi", "Mr. Rager", seconds);
            var kugou = new Noctis.Services.KugouLyricsService(http).SearchLyricsAsync("Kid Cudi", "Mr. Rager", seconds);
            await Task.WhenAll(red, kugou);
            Assert.NotNull(OnlineWordTiming.WordTimed(await red, seconds));
            Assert.NotNull(OnlineWordTiming.WordTimed(await kugou, seconds));
            Console.WriteLine($"LIVE Mr. Rager: catalog298s, actual{seconds:0.00}s; BOTH real sources accepted with only64KiB present, {clock.ElapsedMilliseconds}ms; no audio download");
        }
        finally { File.Delete(path); }
    }

    internal static byte[] Flac(double seconds)
    {
        var data = new byte[42];
        "fLaC"u8.CopyTo(data); data[4] = 128; data[7] = 34;
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(18), (44100UL << 44) | (15UL << 36) | (ulong)Math.Round(seconds * 44100));
        return data;
    }

    [Fact]
    public void PartialFlacCarriesTheWholeRecordingLength()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".flac");
        try
        {
            File.WriteAllBytes(path, Flac(294.23));
            var track = new Track { FilePath = path, Duration = TimeSpan.FromSeconds(298) };
            Assert.InRange(OnlineTimingDuration.Seconds(track), 294.229, 294.231);
            Assert.Null(OnlineTimingDuration.HeaderSeconds(Flac(294.23).AsSpan(0, 41)));
            Assert.Null(OnlineTimingDuration.HeaderSeconds(Flac(0)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0xFB, 44100, 1152, 32, 0)]
    [InlineData(0xF3, 22050, 576, 17, 0)]
    [InlineData(0xF2, 22050, 576, 17, 2)]
    public void PartialMp3UsesFrameCount_NotDownloadedByteCount(int version, int rate, int samples, int side, int crc)
    {
        var data = new byte[80];
        data[0] = 255; data[1] = (byte)version; data[2] = 0x90;
        var at = 4 + side + crc;
        "Xing"u8.CopyTo(data.AsSpan(at));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at + 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at + 8), 11264);
        Assert.Equal(11264.0 * samples / rate, OnlineTimingDuration.HeaderSeconds(data)!.Value, 5);
        data[at + 7] = 0;
        Assert.Null(OnlineTimingDuration.HeaderSeconds(data));
    }
}
