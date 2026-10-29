using System.Diagnostics;
using LibVLCSharp.Shared;
using Noctis.Features.ProgressivePlayback;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #204: seeking inside the part of a song still downloading, with real VLC and time-coded audio.
/// Its own class (owner 2026-10-29: the gates were slow) so it runs beside ProgressiveDecoderTests, not after it.</summary>
public class ProgressiveSeekTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    public async Task WhileTheSongArrives_ASeekInsideTheDownloadedPartPlaysThere_PastItWaitsForTheBytes(string extension)
    {
        // Owner #204 (2026-10-29): skip around in the part already downloaded. Second t is a 300 + 50*t Hz tone, so the
        // delivered PCM shows where playback went. 80% of a 20 s song has arrived; real VLC with the app's input.
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires the bundled Windows LibVLC decoder.");
        Core.Initialize();
        using var data = new TestPersistenceService();
        const int seconds = 20, rate = 48000;
        var audio = await ProgressiveDecoderTests.AudioAsync(extension, data.DataDirectory, wave: ProgressiveDecoderTests.TimeCoded(seconds));
        var path = Path.Combine(data.DataDirectory, "timed." + extension);
        using var buffer = new ProgressiveAudioBuffer(new FileStream(path + ".part", FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1));
        buffer.Begin(audio.Length);
        buffer.Writer.Write(audio.AsSpan(0, audio.Length * 8 / 10));
        using var input = new ProgressiveMediaInput(buffer);
        using var vlc = new LibVLC("--intf=dummy", "--no-video", "--no-stats", "--quiet");
        using var media = new Media(vlc, input);
        ProgressiveMedia.Configure(media, path);
        using var player = new MediaPlayer(vlc);
        var pcm = new List<short>();
        MediaPlayer.LibVLCAudioPlayCb play = (_, samples, count, _) =>
        { var block = new short[count]; System.Runtime.InteropServices.Marshal.Copy(samples, block, 0, (int)count); lock (pcm) pcm.AddRange(block); };
        player.SetAudioCallbacks(play, null, null, null, null);
        player.SetAudioFormat("S16N", rate, 1);
        int Mark() { lock (pcm) return pcm.Count; }
        List<int> Heard(int from)
        {
            short[] heard; lock (pcm) heard = pcm.Skip(from).ToArray();
            var list = new List<int>();
            for (var start = 0; start + rate / 10 <= heard.Length; start += rate / 10)
                if (ProgressiveDecoderTests.ToneSecond(heard.AsSpan(start, rate / 10), rate) is { } s && (list.Count == 0 || list[^1] != s)) list.Add(s);
            return list;
        }
        try
        {
            Assert.True(player.Play(media));
            await Task.Delay(1500);
            Assert.True(player.IsSeekable, "the song still arriving is seekable");
            var before = Mark();
            player.Time = 10_000;
            await Task.Delay(2500);
            var afterSeek = Heard(before);
            var past = Mark();
            output.WriteLine($"{extension}: after the seek to 10 s: {string.Join(",", afterSeek)}");
            Assert.Contains(afterSeek, s => s is 10 or 11);
            Assert.DoesNotContain(afterSeek, s => s is >= 2 and <= 8);   // not a restart from the top
            player.Time = 18_000;   // past the 16 s downloaded: waits, then plays from there
            await Task.Delay(1500);
            buffer.Writer.Write(audio.AsSpan(audio.Length * 8 / 10)); buffer.Finish();
            await Task.Delay(2500);
            var afterRest = Heard(past);
            output.WriteLine($"{extension}: after the seek past the edge and the rest arriving: {string.Join(",", afterRest)}");
            Assert.Contains(18, afterRest);
        }
        finally
        {
            input.CancelRead();
            await Task.Run(player.Stop).WaitAsync(TimeSpan.FromSeconds(5));
            GC.KeepAlive(play);
        }
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    public async Task WithTheRealSizeKnown_PlaybackStillStartsFromAFewPercent(string extension)
    {
        // #204: VLC is told the song's size; its look at the end of the file must not wait for the last bytes.
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires the bundled Windows LibVLC decoder.");
        Core.Initialize();
        using var data = new TestPersistenceService();
        var audio = await ProgressiveDecoderTests.AudioAsync(extension, data.DataDirectory, wave: ProgressiveDecoderTests.TimeCoded(20));
        var path = Path.Combine(data.DataDirectory, "start." + extension);
        using var buffer = new ProgressiveAudioBuffer(new FileStream(path + ".part", FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1));
        buffer.Begin(audio.Length);
        buffer.Writer.Write(audio.AsSpan(0, audio.Length / 20));
        using var input = new ProgressiveMediaInput(buffer);
        using var vlc = new LibVLC("--intf=dummy", "--no-video", "--no-stats", "--quiet");
        using var media = new Media(vlc, input);
        ProgressiveMedia.Configure(media, path);
        using var player = new MediaPlayer(vlc);
        var decoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer.LibVLCAudioPlayCb play = (_, _, _, _) => decoded.TrySetResult();
        player.SetAudioCallbacks(play, null, null, null, null);
        player.SetAudioFormat("S16N", 48000, 1);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Assert.True(player.Play(media));
            await decoded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            output.WriteLine($"{extension}: first audio after {clock.ElapsedMilliseconds} ms with 5% downloaded");
        }
        finally
        {
            input.CancelRead();
            await Task.Run(player.Stop).WaitAsync(TimeSpan.FromSeconds(5));
            GC.KeepAlive(play);
        }
    }

    [Fact]
    public void TheSeekableEdgeIsTheDownloadedShareLessAMargin()
    {
        var song = TimeSpan.FromSeconds(200);
        Assert.Equal(TimeSpan.Zero, ProgressiveMedia.SeekableUntilFor(0, 1000, song));
        Assert.Equal(TimeSpan.Zero, ProgressiveMedia.SeekableUntilFor(500, 0, song));
        Assert.Equal(TimeSpan.Zero, ProgressiveMedia.SeekableUntilFor(500, 1000, null));
        Assert.Equal(TimeSpan.FromSeconds(160 - 6), ProgressiveMedia.SeekableUntilFor(800, 1000, song));   // 3% of 200 s
        Assert.Equal(TimeSpan.FromSeconds(8 - 2), ProgressiveMedia.SeekableUntilFor(400, 1000, TimeSpan.FromSeconds(20)));   // at least 2 s
        Assert.Equal(TimeSpan.Zero, ProgressiveMedia.SeekableUntilFor(10, 1000, song));
    }
}
