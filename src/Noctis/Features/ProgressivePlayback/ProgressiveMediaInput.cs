using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Noctis.Features.ProgressivePlayback;

/// <summary>VLC callback source. Until transfer completion it is deliberately nonseekable:
/// end-of-file probes must not jump into unwritten holes or wait for the last chunk.</summary>
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
    { _buffer = buffer; _stop = CancellationTokenSource.CreateLinkedTokenSource(cancel); _cancel = _stop.Token; CanSeek = false; }
    public override bool Open(out ulong size)
    {
        size = ulong.MaxValue;
        try { _cancel.ThrowIfCancellationRequested(); _reader?.Dispose(); _reader = _buffer.OpenReader(_cancel); return true; }
        catch (Exception ex) { Error = ex; return false; }
    }
    public override int Read(IntPtr target, uint length)
    {
        var waiting = false;
        try
        {
            waiting = _reader is { } reader && !_buffer.Complete && _buffer.Prefix <= reader.Position;
            if (waiting) NotifyBuffering(true);
            var read = _reader?.Read(_scratch.AsSpan(0, (int)Math.Min(length, (uint)_scratch.Length))) ?? -1;
            if (read > 0) Marshal.Copy(_scratch, 0, target, read);
            return read;
        }
        catch (Exception ex) { Error = ex; return -1; } // never unwind a native callback
        finally { if (waiting) NotifyBuffering(false); }
    }
    private void NotifyBuffering(bool waiting) { try { Buffering?.Invoke(waiting); } catch { /* native callbacks must not unwind observers */ } }
    public override bool Seek(ulong offset) => false;
    internal void CancelRead() { try { if (Volatile.Read(ref _disposed) == 0) _stop.Cancel(); } catch (ObjectDisposedException) { } }
    public override void Close() { _reader?.Dispose(); _reader = null; }
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) { _stop.Cancel(); Close(); _stop.Dispose(); }
        base.Dispose(disposing);
    }
}
