namespace Noctis.Services;

/// <summary>
/// Where the user's volume lands after LibVLC: the Windows process audio session
/// (<see cref="WindowsSessionVolume"/>), or the gapless engine's own output stage on
/// Linux (<see cref="GaplessSink.LevelControl"/>). VlcAudioPlayer drives both the same
/// way: the integer LibVLC volume stays pinned at 100 and the float level goes here.
/// </summary>
internal interface IOutputLevel : IDisposable
{
    /// <summary>True when the last <see cref="SetLevel"/> landed on the stream that is rendering.</summary>
    bool HoldsActiveSession { get; }

    /// <summary>Sets the amplitude level 0..1. Returns false when it could not be applied.</summary>
    bool SetLevel(double level);

    /// <summary>Forgets any cached target so the next <see cref="SetLevel"/> re-resolves it.</summary>
    void Invalidate();
}
