using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using Noctis.Features.ProgressivePlayback;
using AUri = Android.Net.Uri;

namespace Noctis.Android.Services;

/// <summary>Wraps the player's data source factory: a file Music Finder is still receiving is read through
/// <see cref="ProgressiveReadSession"/>; everything else (finished files, SAF content://, the desktop's
/// authenticated LAN streams) goes to the inner source unchanged.</summary>
internal sealed class ProgressiveDataSourceFactory(IDataSourceFactory inner) : Java.Lang.Object, IDataSourceFactory
{
    public IDataSource CreateDataSource() => new ProgressiveDataSource(inner.CreateDataSource());
}

/// <summary>
/// Media3 reads on its loading thread and cancels a load by interrupting that thread, which does not wake a .NET
/// monitor: every wait here is a 250 ms slice followed by an interrupt check, so a skip or release never hangs on a
/// stalled transfer, and a slow network is buffering, never the end. Exceptions leave as Java IOExceptions, which the
/// player handles as load errors (retry/report) rather than as crashes.
/// </summary>
internal sealed class ProgressiveDataSource(IDataSource inner) : Java.Lang.Object, IDataSource
{
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(250);
    private ProgressiveReadSession? _session;
    private AUri? _uri;
    private long _remaining;
    private bool _innerOpen;
    private string? _originalPath;
    private Func<string?>? _completedPath;

    public AUri? Uri => _session != null ? _uri : inner.Uri;

    public void AddTransferListener(ITransferListener? transferListener)
    {
        if (transferListener != null) inner.AddTransferListener(transferListener);
    }

    public long Open(DataSpec? dataSpec)
    {
        var spec = dataSpec ?? throw new Java.Lang.IllegalArgumentException("No data spec");
        var path = LocalPath(spec.Uri);
        if (path != _originalPath) { _originalPath = null; _completedPath = null; }
        if (path is not null && ProgressiveReadSession.TryOpen(path) is { } session)
        {
            // Close releases the reader immediately, but a later seek may reopen after the
            // producer has collision-renamed its file. Keep only this attempt's resolver,
            // never an open reader or a process-wide alias map. Completion may follow Close.
            _originalPath = path; _completedPath = () => session.CompletedPath;
            try
            {
                while (true)
                {
                    var reached = Map(() => session.TrySeek(spec.Position, Slice));
                    if (reached == true) break;
                    if (reached == false) throw new Java.IO.EOFException("The position is past the end of this song.");
                    ThrowIfInterrupted();
                }
                var length = session.Length;
                _remaining = spec.Length != C.LengthUnset ? spec.Length : length >= 0 ? length - spec.Position : C.LengthUnset;
                _session = session; _uri = spec.Uri;
                return _remaining;
            }
            catch { session.Dispose(); throw; }
        }
        _innerOpen = true;
        if (path is not null && path == _originalPath && _completedPath?.Invoke() is { } final)
        {
            using var file = new Java.IO.File(final);
            using var redirected = spec.WithUri(AUri.FromFile(file)); // preserves position/length/headers
            return inner.Open(redirected);
        }
        return inner.Open(spec);
    }

    public int Read(byte[]? buffer, int offset, int length)
    {
        if (_session is not { } session) return inner.Read(buffer, offset, length);
        if (length == 0) return 0;
        if (_remaining == 0) return C.ResultEndOfInput;
        var want = _remaining == C.LengthUnset ? length : (int)Math.Min(length, _remaining);
        while (true)
        {
            var read = 0;
            if (Map(() => session.TryRead(buffer!, offset, want, Slice, out read)))
            {
                if (read == 0) return C.ResultEndOfInput; // only the end of a complete transfer
                if (_remaining != C.LengthUnset) _remaining -= read;
                return read;
            }
            ThrowIfInterrupted(); // an underflow: keep buffering unless the player stopped this load
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _session, null) is { } session) { session.Dispose(); _uri = null; return; }
        if (_innerOpen) { _innerOpen = false; inner.Close(); }
    }

    private static T Map<T>(Func<T> work)
    {
        try { return work(); }
        catch (OperationCanceledException) { throw new Java.IO.InterruptedIOException("The read was closed."); }
        catch (IOException ex) { throw new Java.IO.IOException(ex.Message); }
    }

    private static void ThrowIfInterrupted()
    {
        if (Java.Lang.Thread.Interrupted()) throw new Java.IO.InterruptedIOException("The load was cancelled.");
    }

    /// <summary>The file path of a file:// (or scheme-less) URI; null for anything else.</summary>
    private static string? LocalPath(AUri? uri)
        => uri is null || (uri.Scheme is { } scheme && scheme != "file") ? null : uri.Path;
}
