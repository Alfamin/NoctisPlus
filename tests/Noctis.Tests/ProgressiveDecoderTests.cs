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

    [Fact]
    public async Task ABurstThenLateChunks_PlaysEverySecond_NothingDropped()
    {
        // Owner report 2026-10-18: while a song downloaded, playback kept skipping a few seconds ahead although the
        // timeline ran on. VLC re-synced its clock to the burst of already-downloaded audio and then dropped the
        // audio it judged late. Second t of this song is a 300 + 50*t Hz tone, so the delivered PCM shows which
        // seconds were played. Real VLC with the app's own options; a third arrives at once, the rest below real time.
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires the bundled Windows LibVLC decoder.");
        Core.Initialize();
        using var data = new TestPersistenceService();
        const int seconds = 12, rate = 48000;
        var audio = await AudioAsync("flac", data.DataDirectory, wave: TimeCoded(seconds));
        var path = Path.Combine(data.DataDirectory, "timed.flac");
        using var buffer = new ProgressiveAudioBuffer(new FileStream(path + ".part", FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1));
        buffer.Begin(audio.Length);
        var burst = audio.Length / 3; buffer.Writer.Write(audio.AsSpan(0, burst));
        using var input = new ProgressiveMediaInput(buffer);
        using var vlc = new LibVLC("--intf=dummy", "--no-video", "--no-stats", "--quiet");
        using var media = new Media(vlc, input);
        ProgressiveMedia.Configure(media, path);
        using var player = new MediaPlayer(vlc);
        var pcm = new List<short>(); var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer.LibVLCAudioPlayCb play = (_, samples, count, _) =>
        { var block = new short[count]; System.Runtime.InteropServices.Marshal.Copy(samples, block, 0, (int)count); lock (pcm) pcm.AddRange(block); };
        player.SetAudioCallbacks(play, null, null, null, null);
        player.SetAudioFormat("S16N", rate, 1);
        player.EndReached += (_, _) => ended.TrySetResult();
        try
        {
            Assert.True(player.Play(media));
            // The rest at 0.8x the recording's rate in uneven chunks: VLC runs into the edge again and again.
            var bytesPerSecond = audio.Length / (double)seconds; var jitter = new Random(5);
            for (var offset = burst; offset < audio.Length;)
            {
                var chunk = Math.Min(audio.Length - offset, (int)(bytesPerSecond * 0.8 * (0.15 + jitter.NextDouble() * 0.5)));
                await Task.Delay(TimeSpan.FromSeconds(chunk / (bytesPerSecond * 0.8)));
                buffer.Writer.Write(audio.AsSpan(offset, chunk)); offset += chunk;
            }
            buffer.Finish();
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(seconds + 20));
        }
        finally
        {
            input.CancelRead();
            await Task.Run(player.Stop).WaitAsync(TimeSpan.FromSeconds(5));
            GC.KeepAlive(play);
        }
        short[] heard; lock (pcm) heard = pcm.ToArray();
        var played = new HashSet<int>();
        for (var start = 0; start + rate / 10 <= heard.Length; start += rate / 10)
            if (ToneSecond(heard.AsSpan(start, rate / 10), rate) is { } second) played.Add(second);
        var missing = Enumerable.Range(0, seconds).Where(s => !played.Contains(s)).ToArray();
        output.WriteLine($"delivered {heard.Length / (double)rate:0.00} s of {seconds} s; seconds played: {string.Join(",", played.Order())}");
        Assert.Empty(missing);
        Assert.True(heard.Length >= rate * (seconds - 0.5), $"only {heard.Length / (double)rate:0.00} s of {seconds} s delivered");
    }

    /// <summary>Mono 48 kHz WAV whose second t is a 300 + 50*t Hz tone, with a little noise so FLAC stays realistic in size.</summary>
    private static byte[] TimeCoded(int seconds)
    {
        const int rate = 48000; var noise = new Random(11);
        var wave = Wave(seconds);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(wave.AsSpan(44));
        for (var n = 0; n < samples.Length; n++)
            samples[n] = (short)(Math.Sin(2 * Math.PI * (300 + 50 * (n / rate)) * n / rate) * 14000 + (noise.NextDouble() - .5) * 3000);
        return wave;
    }

    private static int? ToneSecond(ReadOnlySpan<short> window, int rate)
    {
        // Strongest of the candidate tones by direct correlation (Goertzel-style), robust to the added noise.
        int? best = null; double bestPower = 0, total = 0;
        foreach (var s in window) total += (double)s * s;
        if (total / window.Length < 1e6) return null; // silence
        for (var second = 0; second < 40; second++)
        {
            double f = 300 + 50 * second, re = 0, im = 0;
            for (var n = 0; n < window.Length; n++) { var a = 2 * Math.PI * f * n / rate; re += window[n] * Math.Cos(a); im += window[n] * Math.Sin(a); }
            var power = re * re + im * im;
            if (power > bestPower) { bestPower = power; best = second; }
        }
        return best;
    }

    internal static async Task<byte[]> AudioAsync(string extension, string folder, bool noisy = false, byte[]? wave = null)
    {
        var audio = wave ?? Wave(24);
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
