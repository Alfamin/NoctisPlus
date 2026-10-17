using System.Diagnostics;
using Noctis.Features.LyricMotion;

namespace Noctis.ViewModels;

public partial class LyricsViewModel
{
    internal event Action<TimeSpan>? NativeLyricFrame;
    internal event Action? NativeMotionReset;
    internal event Action? NativeMotionFreeze;
    private double _nativeMotionHandoverUntil;
    private static double MotionNowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
    private bool HasNativeMotionHandover => NativeLyricFrame is not null && MotionNowMs < _nativeMotionHandoverUntil;

    private void BeginNativeMotionHandover()
    {
        if (NativeLyricFrame is not null && LyricMotionFeature.IsEnabled && LyricMotionFeature.Current.Motion)
            _nativeMotionHandoverUntil = MotionNowMs + WordMotion.HandoverMs + 20;
    }

    private void ResetNativeMotion()
    {
        _clock.Reset();
        _nativeMotionHandoverUntil = 0;
        NativeMotionReset?.Invoke();
    }

    private void OnPlayerSeekedForLyrics(object? sender, TimeSpan position)
    {
        IsAutoFollowPaused = false;
        ResetNativeMotion();
    }
}
