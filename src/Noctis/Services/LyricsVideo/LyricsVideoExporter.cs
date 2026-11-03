using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Noctis.Helpers;

namespace Noctis.Services.LyricsVideo;

/// <summary>
/// Owner #243: renders the Reel and encodes it. Frames are drawn on every core at once (each frame is a pure function
/// of its time) and streamed in order straight into ffmpeg's stdin — no image files in between — together with the
/// song's audio for the clip, faded in and out. The beat pulse comes from the clip's own audio, run through the same
/// detector the live background uses, so the flowing cover breathes on the actual kicks.
/// </summary>
internal static class LyricsVideoExporter
{
    /// <summary>One way to encode H.264: the graphics card's encoder when there is one (seconds for a Reel, and it leaves
    /// every CPU core to the drawing), else x264. All aim high (Instagram re-encodes the Reel on upload).</summary>
    internal sealed record VideoEncoder(string Name, string[] Args);

    internal static readonly VideoEncoder X264 = new("libx264",
        ["-preset", "veryfast", "-crf", "17", "-maxrate", "30M", "-bufsize", "60M", "-profile:v", "high", "-level:v", "4.2"]);

    internal static readonly VideoEncoder[] HardwareEncoders =
    [
        new("h264_nvenc", ["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "19", "-b:v", "0", "-maxrate", "30M", "-bufsize", "60M", "-profile:v", "high"]),
        new("h264_amf", ["-quality", "quality", "-rc", "cqp", "-qp_i", "16", "-qp_p", "18", "-qp_b", "20", "-profile:v", "high"]),
        new("h264_qsv", ["-global_quality", "20", "-profile:v", "high"]),
    ];

    private static VideoEncoder? _chosen;

    /// <summary>The first hardware encoder that really encodes on this PC (a tiny test encode), else x264; worked out once.</summary>
    internal static async Task<VideoEncoder> ChooseEncoderAsync(string ffmpegPath, CancellationToken ct)
    {
        if (_chosen is { } known) return known;
        foreach (var encoder in HardwareEncoders)
        {
            try
            {
                var psi = new ProcessStartInfo(ffmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=1080x1920:r=30:d=0.2",
                             "-pix_fmt", "yuv420p", "-c:v", encoder.Name })
                    psi.ArgumentList.Add(a);
                foreach (var a in encoder.Args) psi.ArgumentList.Add(a);
                foreach (var a in new[] { "-f", "null", "-" }) psi.ArgumentList.Add(a);
                using var probe = Process.Start(psi);
                if (probe is null) continue;
                _ = probe.StandardError.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try { await probe.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { probe.Kill(true); } catch { }
                    continue;
                }
                if (probe.ExitCode == 0) return _chosen = encoder;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsVideo:EncoderProbe", $"{encoder.Name}: {ex.Message}");
            }
        }
        return _chosen = X264;
    }

    /// <summary>Instagram's own Reels recommendation, with headroom for its re-encode: H.264 High, 1080×1920, 30 fps.</summary>
    internal static List<string> FfmpegArgs(string audioPath, string outputPath, LyricsVideoSpec spec, VideoEncoder encoder)
    {
        var start = spec.ClipStart.ToString("0.###", CultureInfo.InvariantCulture);
        var duration = spec.Duration.ToString("0.###", CultureInfo.InvariantCulture);
        var fadeOut = Math.Max(0, spec.Duration - (spec.Outro ? 1.6 : 0.6)).ToString("0.###", CultureInfo.InvariantCulture);
        var fadeOutLength = spec.Outro ? "1.6" : "0.6";
        var fadeIn = spec.Intro ? "0.8" : "0.25";
        return
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "yuv420p", "-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-s", $"{LyricsVideoSpec.Width}x{LyricsVideoSpec.Height}",
            "-r", LyricsVideoSpec.Fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
            "-ss", start, "-t", duration, "-i", audioPath,
            "-map", "0:v:0", "-map", "1:a:0",
            // Frames arrive already in BT.709 YUV (converted on the rendering cores); tagged so phones show the colours rendered.
            "-c:v", encoder.Name, .. encoder.Args,
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-g", "60", "-bf", "2",
            "-af", $"afade=t=in:st=0:d={fadeIn},afade=t=out:st={fadeOut}:d={fadeOutLength}",
            "-c:a", "aac", "-b:a", "256k", "-ar", "48000",
            "-movflags", "+faststart",
            "-t", duration,
            outputPath,
        ];
    }

    /// <summary>
    /// Renders and encodes the Reel to <paramref name="outputPath"/>. <paramref name="progress"/> gets 0..1 as frames are
    /// encoded. Returns null on success, else a short reason.
    /// </summary>
    public static async Task<string?> ExportAsync(LyricsVideoSpec spec, string audioPath, string ffmpegPath, string outputPath,
        IProgress<double>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var pulse = await BeatPulseAsync(ffmpegPath, audioPath, spec, ct).ConfigureAwait(false);
        using var scene = new LyricsVideoScene(spec);
        var encoder = await ChooseEncoderAsync(ffmpegPath, ct).ConfigureAwait(false);
        var failure = await EncodeAsync(scene, pulse, audioPath, ffmpegPath, outputPath, encoder, progress, ct).ConfigureAwait(false);
        if (failure is not null && encoder != X264 && !ct.IsCancellationRequested)
        {
            // The graphics card's encoder gave up part way: x264 from here on, this time and the next.
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsVideo:HardwareEncoderFailed", $"{encoder.Name}: {failure}");
            _chosen = encoder = X264;
            progress?.Report(0);
            failure = await EncodeAsync(scene, pulse, audioPath, ffmpegPath, outputPath, encoder, progress, ct).ConfigureAwait(false);
        }
        if (failure is null)
            DebugLogger.Info(DebugLogger.Category.Lyrics, "LyricsVideo:Done",
                $"{spec.FrameCount} frames ({spec.Duration:0.0} s) in {watch.Elapsed.TotalSeconds:0.0} s with {encoder.Name}");
        return failure;
    }

    private static async Task<string?> EncodeAsync(LyricsVideoScene scene, Func<double, double> pulse, string audioPath, string ffmpegPath,
        string outputPath, VideoEncoder encoder, IProgress<double>? progress, CancellationToken ct)
    {
        var spec = scene.Spec;
        var frames = spec.FrameCount;
        var frameBytes = LyricsVideoRenderer.FrameBytes;

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in FfmpegArgs(audioPath, outputPath, spec, encoder)) psi.ArgumentList.Add(arg);
        using var ffmpeg = Process.Start(psi);
        if (ffmpeg is null) return "ffmpeg did not start";
        var stderr = ffmpeg.StandardError.ReadToEndAsync(ct);
        var stdin = ffmpeg.StandardInput.BaseStream;

        // Frames are drawn ahead on every core into a small window of buffers and written strictly in order
        // (x264 needs cores of its own; a graphics card's encoder does not).
        var workers = Math.Clamp(Environment.ProcessorCount - (encoder == X264 ? 2 : 1), 1, 12);
        var window = workers * 2;
        var ready = new TaskCompletionSource<byte[]>[frames];
        for (var i = 0; i < frames; i++) ready[i] = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slots = new SemaphoreSlim(window, window);
        var next = -1;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var renderers = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            using var renderer = new LyricsVideoRenderer(scene);
            while (true)
            {
                await slots.WaitAsync(linked.Token).ConfigureAwait(false);
                var index = Interlocked.Increment(ref next);
                if (index >= frames) { slots.Release(); return; }
                var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
                try
                {
                    renderer.RenderFrame(index, pulse, buffer.AsSpan(0, frameBytes));
                    ready[index].TrySetResult(buffer);
                }
                catch (Exception ex)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    ready[index].TrySetException(ex);
                    throw;
                }
            }
        }, linked.Token)).ToArray();

        string? failure = null;
        var written = 0;
        try
        {
            for (var i = 0; i < frames; i++)
            {
                var buffer = await ready[i].Task.WaitAsync(linked.Token).ConfigureAwait(false);
                try { await stdin.WriteAsync(buffer.AsMemory(0, frameBytes), linked.Token).ConfigureAwait(false); }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    written = i + 1;
                    slots.Release();
                }
                progress?.Report((i + 1) / (double)frames);
            }
            stdin.Close();
            await ffmpeg.WaitForExitAsync(ct).ConfigureAwait(false);
            if (ffmpeg.ExitCode != 0) failure = LastLine(await stderr.ConfigureAwait(false)) ?? $"ffmpeg exit {ffmpeg.ExitCode}";
        }
        catch (OperationCanceledException)
        {
            failure = "Cancelled";
        }
        catch (IOException)
        {
            // ffmpeg closed its input early: it failed, and its error says why.
            try { await ffmpeg.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            failure = LastLine(await stderr.ConfigureAwait(false)) ?? "ffmpeg stopped";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(renderers).ConfigureAwait(false); } catch { }
            for (var i = written; i < frames; i++)
                if (ready[i].Task.IsCompletedSuccessfully) ArrayPool<byte>.Shared.Return(ready[i].Task.Result);
            if (!ffmpeg.HasExited) try { ffmpeg.Kill(entireProcessTree: true); } catch { }
        }

        if (failure is not null)
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsVideo:Failed", $"{encoder.Name}: {failure}");
            return failure;
        }
        return null;
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    /// <summary>
    /// The beat pulse over the clip: its audio decoded (mono, 22.05 kHz) and fed through <see cref="BeatMeter"/> on a
    /// clock that is the audio's own time, then smoothed with the background's attack/release
    /// (<see cref="FlowingArtworkMotion.Smooth"/>). Falls back to the tagged BPM grid, else no pulse.
    /// </summary>
    internal static async Task<Func<double, double>> BeatPulseAsync(string ffmpegPath, string audioPath, LyricsVideoSpec spec, CancellationToken ct)
    {
        float[]? samples = null;
        const int rate = 22050;
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[]
                     {
                         "-hide_banner", "-loglevel", "error",
                         "-ss", spec.ClipStart.ToString("0.###", CultureInfo.InvariantCulture),
                         "-t", spec.Duration.ToString("0.###", CultureInfo.InvariantCulture),
                         "-i", audioPath, "-ac", "1", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1",
                     })
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            if (process is not null)
            {
                _ = process.StandardError.ReadToEndAsync(ct);
                using var memory = new MemoryStream();
                await process.StandardOutput.BaseStream.CopyToAsync(memory, ct).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var bytes = memory.GetBuffer().AsSpan(0, (int)memory.Length);
                samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes[..(bytes.Length / 4 * 4)]).ToArray();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsVideo:AudioUnreadable", ex.Message);
        }

        if (samples is not { Length: > rate / 10 })
        {
            var bpm = spec.Bpm;
            return t => BeatPulseSource.FromBpmGrid(new BeatContext(bpm, t * 1000, true));
        }
        return PulseFromSamples(samples, rate, spec.ClipStart);
    }

    /// <summary>The smoothed pulse at 1 ms steps for <paramref name="samples"/> starting at track time <paramref name="start"/>.</summary>
    internal static Func<double, double> PulseFromSamples(float[] samples, int rate, double start)
    {
        double clock = 0;
        var meter = new BeatMeter(() => clock);
        const int chunk = 441; // 20 ms at 22.05 kHz
        var lengthMs = samples.Length * 1000.0 / rate;
        var curve = new float[(int)Math.Ceiling(lengthMs) + 1];
        var fed = 0;
        double smoothed = 0;
        for (var ms = 0; ms < curve.Length; ms++)
        {
            // Feed every sample heard by this millisecond, on the meter's clock at the time it is heard.
            while (fed < samples.Length && fed * 1000.0 / rate <= ms)
            {
                var count = Math.Min(chunk, samples.Length - fed);
                clock = fed * 1000.0 / rate;
                meter.Feed(samples, fed, count, 1, rate, 0);
                fed += count;
            }
            clock = ms;
            var raw = meter.TryRead(ms, out var live) ? live : 0;
            smoothed = FlowingArtworkMotion.Smooth(smoothed, raw, ms == 0 ? 0 : 1);
            curve[ms] = (float)smoothed;
        }
        return t =>
        {
            var ms = (t - start) * 1000;
            if (ms <= 0) return curve[0];
            var i = (int)ms;
            if (i + 1 >= curve.Length) return curve[^1];
            var f = ms - i;
            return curve[i] + (curve[i + 1] - curve[i]) * f;
        };
    }
}
