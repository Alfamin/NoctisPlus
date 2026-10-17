using System.Diagnostics;

namespace Noctis.Features.LyricMotion;

/// <summary>Opt-in Debug timing; only writes inside the explicitly selected app profile.</summary>
internal static class MotionDiagnostics
{
#if DEBUG
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("NOCTISPLUS_DEBUG_MOTION_TIMING") == "1";
    private static int _frames, _renders, _lateFrames;
    private static long _lastFrame;
    private static double _frameMs, _stepMs, _renderMs, _maxStepMs, _maxGapMs;
    private static long _allocated;
    internal static void Frame(long start, long bytes)
    {
        var end = Stopwatch.GetTimestamp();
        var step = (end - start) * 1000.0 / Stopwatch.Frequency;
        _stepMs += step; _maxStepMs = Math.Max(_maxStepMs, step);
        _allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
        if (_lastFrame != 0)
        {
            var gap = (start - _lastFrame) * 1000.0 / Stopwatch.Frequency;
            _frameMs += gap; _maxGapMs = Math.Max(_maxGapMs, gap);
            if (gap > 25) _lateFrames++;
        }
        _lastFrame = start;
        if (++_frames % 180 == 0)
        {
            try
            {
                File.AppendAllText(Path.Combine(Helpers.AppPaths.DataRoot, "native-motion-timing.log"),
                    $"frames={_frames} gap_avg_ms={_frameMs / Math.Max(1, _frames - 1):F3} gap_max_ms={_maxGapMs:F3} gaps_over_25ms={_lateFrames} step_avg_ms={_stepMs / _frames:F4} step_max_ms={_maxStepMs:F3} step_bytes_per_frame={_allocated / _frames} renders={_renders} render_avg_ms={_renderMs / Math.Max(1, _renders):F4}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    internal static void Render(long start)
    {
        _renders++;
        _renderMs += (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    }
#else
    internal const bool Enabled = false;
    internal static void Frame(long start, long bytes) { }
    internal static void Render(long start) { }
#endif
}
