using System.Diagnostics;
using LibVLCSharp.Shared;
using Noctis.Features.ProgressivePlayback;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Real native decoder, generated audio, no sound output and no remote access.</summary>
public class ProgressiveDecoderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("wav")]
    [InlineData("mp3")]
    [InlineData("flac")]
    public async Task VlcDecodesAudioBeforeTheTransferEnds_AndStopUnblocksAnUnderrun(string extension)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires the bundled Windows LibVLC decoder.");
        Core.Initialize();
        using var data = new TestPersistenceService();
        var audio = await AudioAsync(extension, data.DataDirectory);
        using var buffer = new ProgressiveAudioBuffer(new FileStream(Path.Combine(data.DataDirectory, "generated.part"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1));
        buffer.Begin(audio.Length); buffer.Writer.Write(audio.AsSpan(0, audio.Length / 3)); // withhold the final two thirds
        using var input = new ProgressiveMediaInput(buffer);
        using var vlc = new LibVLC("--intf=dummy", "--no-video", "--no-stats", "--quiet");
        using var media = new Media(vlc, input);
        media.AddOption(":network-caching=100"); media.AddOption(":file-caching=100");
        using var player = new MediaPlayer(vlc);
        var decoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long samples = 0;
        MediaPlayer.LibVLCAudioPlayCb play = (_, _, count, _) => { Interlocked.Add(ref samples, count); decoded.TrySetResult(); };
        player.SetAudioCallbacks(play, null, null, null, null);
        player.SetAudioFormat("S16N", 48000, 1);
        var clock = Stopwatch.StartNew();
        try
        {
            Assert.True(player.Play(media));
            await decoded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            output.WriteLine($"{extension}: first decoded PCM {clock.ElapsedMilliseconds} ms; {buffer.Prefix:N0}/{buffer.Expected:N0} bytes present; producer complete={buffer.Complete}; samples={samples}.");
            Assert.False(buffer.Complete); Assert.True(samples > 0); Assert.True(buffer.Prefix < buffer.Expected);
        }
        finally
        {
            input.CancelRead(); // cancel BEFORE VLC.Stop: the decoder may be blocked on missing bytes
            await Task.Run(player.Stop).WaitAsync(TimeSpan.FromSeconds(5));
            GC.KeepAlive(play);
        }
    }

    internal static async Task<byte[]> AudioAsync(string extension, string folder, bool noisy = false)
    {
        var audio = Wave(24);
        if (noisy) new Random(217).NextBytes(audio.AsSpan(44)); // resist FLAC compression for a realistic partial-file size
        if (extension != "wav")
        {
            var ffmpeg = Environment.GetEnvironmentVariable("NOCTIS_TEST_FFMPEG") ?? new AudioConverterService(() => "", new MetadataService()).GetFfmpegPath();
            if (ffmpeg is null) Assert.Skip("FFmpeg is unavailable to encode a generated " + extension + " fixture.");
            var source = Path.Combine(folder, "tone.wav"); File.WriteAllBytes(source, audio);
            var encoded = Path.Combine(folder, "tone." + extension);
            var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", source, encoded }) info.ArgumentList.Add(arg);
            using var encoder = Process.Start(info)!; var errors = encoder.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await encoder.WaitForExitAsync(timeout.Token); }
            catch { encoder.Kill(entireProcessTree: true); throw; }
            Assert.True(encoder.ExitCode == 0, await errors); audio = File.ReadAllBytes(encoded);
        }
        return audio;
    }

    internal static byte[] Wave(int seconds)
    {
        const int rate = 48000; var bytes = rate * seconds * 2;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(bytes + 36); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(bytes);
        for (int n = 0; n < rate * seconds; n++) writer.Write((short)(Math.Sin(n * 2 * Math.PI * 440 / rate) * 1200));
        return stream.ToArray();
    }
}
