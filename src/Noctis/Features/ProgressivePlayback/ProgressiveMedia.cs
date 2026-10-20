using LibVLCSharp.Shared;

namespace Noctis.Features.ProgressivePlayback;

/// <summary>Media owns its callback input and reader lease. Call CancelRead before native
/// Stop; dispose only after Stop has joined VLC's decoder/callback threads.</summary>
internal sealed class ProgressiveMedia : Media
{
    private readonly ProgressiveTransfer.Lease _lease;
    private readonly ProgressiveMediaInput _input;
    private int _disposed;
    internal static ProgressiveMedia Create(LibVLC vlc, ProgressiveTransfer.Lease lease)
    {
        var input = lease.CreateInput();
        try { return new(vlc, lease, input); }
        catch { input.Dispose(); lease.Dispose(); throw; }
    }
    private ProgressiveMedia(LibVLC vlc, ProgressiveTransfer.Lease lease, ProgressiveMediaInput input) : base(vlc, input)
    { _lease = lease; _input = input; _lease.Ended += OnFinished; }
    /// <summary>The VLC options every download-backed input plays with.</summary>
    internal static void Configure(Media media, string path)
    {
        media.AddOption(Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? ":demux=mp3" : ":demux=flac");
        // VLC re-syncs its clock to each new timestamp of this input as if it were a live broadcast: when the bytes
        // arrive late or in a burst it DROPS the audio it now thinks is late ("buffer too late: dropped"), so the song
        // skips seconds while the timeline runs on (owner report 2026-10-18; measured with a time-coded song:
        // seconds 4-10 never delivered). A download is not a broadcast: without the re-sync VLC waits for the bytes.
        media.AddOption(":clock-synchro=0");
    }

    internal bool Complete => _lease.Buffer.Complete;
    internal TimeSpan? Duration => _lease.Duration;
    internal bool Cancelled => _input.Cancelled;
    internal Exception? Error => _input.Error ?? _lease.Error;
    internal void CancelRead() => _input.CancelRead();
    internal event Action<bool>? Buffering { add => _input.Buffering += value; remove => _input.Buffering -= value; }
    internal event Action? Finished;
    private void OnFinished() => Finished?.Invoke();
    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lease.Ended -= OnFinished;
        try { base.Dispose(disposing); }
        finally { _input.Dispose(); _lease.Dispose(); }
    }
}
