using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Noctis.Features.ProgressivePlayback;

/// <summary>VLC callback source. Only already-downloaded contiguous bytes can be sought:
/// end-of-file probes never jump into unwritten holes or wait for the last chunk.
/// VLC3 also requires a seek callback to support PAUSE on callback inputs.</summary>
internal sealed class ProgressiveMediaInput : MediaInput
{
    private readonly ProgressiveAudioBuffer _buffer;
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _cancel;
    private int _disposed;
    private ProgressiveAudioBuffer.AudioReader? _reader;
    private readonly byte[] _scratch = new byte[64 * 1024];
    internal Exception? Error { get; private set; }
    internal event Action<bool>? Buffering;
    internal bool Cancelled => _cancel.IsCancellationRequested;
    internal ProgressiveMediaInput(ProgressiveAudioBuffer buffer, CancellationToken cancel = default)
    { _buffer = buffer; _stop = CancellationTokenSource.CreateLinkedTokenSource(cancel); _cancel = _stop.Token; CanSeek = true; }
    public override bool Open(out ulong size)
    {
        size = ulong.MaxValue;
        try
        {
            _cancel.ThrowIfCancellationRequested();
            if (Error is not null) return false;
            _reader?.Dispose(); _reader = _buffer.OpenReader(_cancel); return true;
        }
        catch (Exception ex) { Error = ex; return false; }
    }
    public override int Read(IntPtr target, uint length)
    {
        var waiting = false;
        try
        {
            if (Error is not null) return -1;
            waiting = _reader is { } reader && !_buffer.Complete && _buffer.Prefix <= reader.Position;
            if (waiting) NotifyBuffering(true);
            var read = _reader?.Read(_scratch.AsSpan(0, (int)Math.Min(length, (uint)_scratch.Length))) ?? -1;
            if (read > 0) Marshal.Copy(_scratch, 0, target, read);
            return read;
        }
        catch (Exception ex)
        {
            // Once per input, always in session.log: an early end of a download-backed song names its cause.
            if (Error is null && !_cancel.IsCancellationRequested)
                try { Noctis.Services.DebugLog.Write("Playback", $"download input read failed: {ex.GetType().Name}: {ex.Message}"); } catch { }
            Error = ex;
            if (ex is IOException && !_cancel.IsCancellationRequested)
            {
                // Returning -1 lets VLC replay its internal prefetch buffer, without
                // invoking Seek/Open or EndReached. Park this input instead. A resumed
                // transfer or Stop/Next cancels this read before disposing native media.
                NotifyBuffering(true);
                try { _cancel.WaitHandle.WaitOne(); } catch (ObjectDisposedException) { }
            }
            return -1; // never unwind a native callback
        }
        finally { if (waiting) NotifyBuffering(false); }
    }
    private void NotifyBuffering(bool waiting) { try { Buffering?.Invoke(waiting); } catch { /* native callbacks must not unwind observers */ } }
    // VLC's FLAC demuxer tries Seek(0) after a read failure. Accepting it replays
    // the same prefix forever without an EndReached event. Pause still has its
    // seek callback; only this terminally failed input is sealed against rewind.
    public override bool Seek(ulong offset) => Error is null && offset <= long.MaxValue && _reader?.Seek((long)offset) == true;
    internal void CancelRead() { try { if (Volatile.Read(ref _disposed) == 0) _stop.Cancel(); } catch (ObjectDisposedException) { } }
    public override void Close() { _reader?.Dispose(); _reader = null; }
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) { _stop.Cancel(); Close(); _stop.Dispose(); }
        base.Dispose(disposing);
    }
}
