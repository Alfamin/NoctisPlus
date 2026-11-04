namespace Noctis.Features.ProgressivePlayback;

/// <summary>
/// A player's read of a song that is still arriving (the phone's Media3 data source sits on this). It holds one lease on
/// the shared transfer and one reader: it never owns the download, so closing it (a skip, a seek, the player released)
/// leaves the saved download and other readers alone. Blocking: call from the player's loading thread, never the UI.
/// A temporary underflow waits; only the end of a complete transfer reads as the end; a failed transfer throws.
/// Seeking is within the bytes received so far; a position past them is waited for (the transfer is sequential and
/// cannot fetch an arbitrary range), and past the end of the complete file it is refused.
/// </summary>
internal sealed class ProgressiveReadSession : IDisposable
{
    /// <summary>Bounded reads keep each player read small whatever it asks for.</summary>
    internal const int MaxRead = 256 * 1024;
    private readonly ProgressiveTransfer.Lease _lease;
    private readonly ProgressiveAudioBuffer.AudioReader _reader;
    private readonly CancellationTokenSource _stop = new();
    private int _closed;

    private ProgressiveReadSession(ProgressiveTransfer.Lease lease)
    {
        _lease = lease;
        _reader = lease.Buffer.OpenReader(_stop.Token);
    }

    /// <summary>A session over the transfer still writing <paramref name="path"/>, or null when nothing is being written
    /// there (a finished song is then read as an ordinary file).</summary>
    internal static ProgressiveReadSession? TryOpen(string path)
    {
        var lease = ProgressiveTransfer.Acquire(path);
        if (lease is null) return null;
        try { return new ProgressiveReadSession(lease); }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>The song's size in bytes, or -1 while unknown.</summary>
    internal long Length => _lease.Buffer.Expected > 0 ? _lease.Buffer.Expected : -1;
    internal long Position => _reader.Position;
    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Moves to <paramref name="position"/>, waiting up to <paramref name="wait"/> for it to arrive.
    /// Null: still waiting (call again). False: unreachable (past the end, or the transfer failed).</summary>
    internal bool? TrySeek(long position, TimeSpan wait)
    {
        if (position < 0) return false;
        var reached = Guard(() => _reader.Reach(position, wait));
        return reached == true ? _reader.Seek(position) : reached;
    }

    /// <summary>At most <see cref="MaxRead"/> bytes into <paramref name="buffer"/>. False: nothing arrived within
    /// <paramref name="wait"/> (check for a stop, then call again). <paramref name="read"/> 0 is the genuine end.</summary>
    internal bool TryRead(byte[] buffer, int offset, int count, TimeSpan wait, out int read)
    {
        try { return _reader.TryRead(buffer.AsSpan(offset, Math.Min(count, MaxRead)), wait, out read); }
        catch (ObjectDisposedException) when (IsClosed) { throw new OperationCanceledException("The read was closed."); }
    }

    private T Guard<T>(Func<T> work)
    {
        try { return work(); }
        // Closed from another thread mid-wait: the reader's token or the buffer is gone. Either way a stop, not an error.
        catch (ObjectDisposedException) when (IsClosed) { throw new OperationCanceledException("The read was closed."); }
    }

    /// <summary>Unblocks a waiting read and releases the lease. Safe from any thread, more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        _reader.Dispose();
        _lease.Dispose();
    }
}
