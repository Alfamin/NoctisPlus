using System.Collections.Concurrent;

namespace Noctis.Features.ProgressivePlayback;

/// <summary>A producer can publish its actual format before writing audio. Source policy
/// decides whether that format may play now or is only a provisional fallback.</summary>
internal interface IProgressiveAudioWriter
{
    void SetFormatPolicy(Func<string, bool> accepts);
    void BeginAudio(long size, string extension);
}

/// <summary>One download attempt, one disk file. The producer and VLC readers hold separate
/// leases; finishing a download never closes a file still being decoded. Registry keys are
/// the real final file paths, not fabricated network URLs or media-server identities.</summary>
internal sealed class ProgressiveTransfer(string folder, string fileName, TimeSpan? duration)
{
    private static readonly ConcurrentDictionary<string, ProgressiveTransfer> Ready = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string _basePath = Path.Combine(folder, fileName);
    private ProgressiveAudioBuffer? _buffer;
    private Func<string, bool> _accepts = _ => false;
    private string? _path, _registered;
    private bool _eligible, _finished, _released;
    private Exception? _error;
    private int _readers;
    private TimeSpan? Duration => duration;
    private event Action? Ended;

    internal Stream Attach(FileStream file)
    {
        lock (_gate)
        {
            if (_buffer is not null || _finished) throw new InvalidOperationException("This download attempt already owns a file.");
            _buffer = new(file);
            return new Writer(this, _buffer.Writer);
        }
    }

    internal string? ReadyPath
    {
        get
        {
            lock (_gate)
            {
                if (_finished) return _error is null ? _path : null;
                if (_buffer is null || !_eligible || _path is null || duration is null || !_buffer.ReadyToPlay(duration.Value)) return null;
                // Reject unsafe/unknown headers before handing bytes to the decoder. Large
                // ID3 tags must be entirely buffered; complete-file playback remains available.
                if (!HasAudioHeader(_buffer, Path.GetExtension(_path))) return null;
                if (_registered is null) { Ready[_path] = this; _registered = _path; }
                return _path;
            }
        }
    }
    internal bool Finished { get { lock (_gate) return _finished; } }
    internal bool HasReaders { get { lock (_gate) return _readers > 0; } }
    internal Exception? Error { get { lock (_gate) return _error; } }

    internal void End(string? savedPath, Exception? error = null)
    {
        Action? notify;
        lock (_gate)
        {
            if (_finished) return;
            _finished = true; _error = error;
            if (savedPath is not null) _path = savedPath;
            try
            {
                _buffer?.Finish(error);
                if (error is null && _buffer is { Complete: false }) _error = new IOException("The download ended before all audio bytes were available.");
                if (_error is null && savedPath is not null && _readers > 0) _buffer?.SealReadOnly(savedPath);
            }
            catch (Exception ex) { _error = ex; _buffer?.Finish(ex); }
            ReleaseIfUnused();
            notify = Ended;
        }
        if (notify is not null) foreach (Action listener in notify.GetInvocationList())
            try { listener(); } catch { /* observers never fail the saved download */ }
    }

    private void ReleaseIfUnused()
    {
        if (!_finished || _readers != 0 || _released) return;
        _released = true; Unregister(); _buffer?.Dispose();
    }
    private void Unregister()
    {
        if (_registered is not null) Ready.TryRemove(new KeyValuePair<string, ProgressiveTransfer>(_registered, this));
        _registered = null;
    }

    internal static Lease? Acquire(string path)
    {
        if (!Ready.TryGetValue(path, out var transfer)) return null;
        lock (transfer._gate)
        {
            if (transfer._finished || transfer._released || transfer._error is not null || transfer._buffer is null) return null;
            transfer._readers++;
            return new(transfer, transfer._buffer);
        }
    }
    internal static bool IsAvailable(string path) => Ready.ContainsKey(path);
    // A completed transfer stays registered while a player owns its reader.
    // That lease must not keep complete-file actions disabled for the rest of the song.
    internal static bool IsDownloading(string path) => Ready.TryGetValue(path, out var transfer) && !transfer.Finished;

    internal static byte[]? ReadPrefix(string path)
    {
        using var lease = Acquire(path);
        if (lease is null) return null;
        var bytes = new byte[(int)Math.Min(65536, lease.Buffer.Prefix)];
        if (bytes.Length == 0) return null;
        using var reader = lease.Buffer.OpenReader();
        return bytes.AsSpan(0, reader.Read(bytes)).ToArray();
    }

    // Keep the actual attempt, not just registry membership: cancellation also removes
    // a transfer, and must never be mistaken for a completed audio file.
    internal static async Task<bool> WaitForDownloadAsync(string path, CancellationToken ct)
    {
        if (!Ready.TryGetValue(path, out var transfer)) return File.Exists(path);
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEnded() => ended.TrySetResult(true);
        lock (transfer._gate)
        {
            if (!transfer._finished) transfer.Ended += OnEnded;
            else ended.TrySetResult(true);
        }
        try
        {
            await ended.Task.WaitAsync(ct).ConfigureAwait(false);
            lock (transfer._gate) return transfer._error is null && File.Exists(path);
        }
        finally { lock (transfer._gate) transfer.Ended -= OnEnded; }
    }

    internal sealed class Lease(ProgressiveTransfer owner, ProgressiveAudioBuffer buffer) : IDisposable
    {
        private int _disposed;
        internal ProgressiveAudioBuffer Buffer => buffer;
        internal TimeSpan? Duration => owner.Duration;
        internal Exception? Error => owner.Error;
        internal ProgressiveMediaInput CreateInput() => new(buffer);
        internal event Action? Ended { add => owner.Ended += value; remove => owner.Ended -= value; }
        internal Lease Retain()
        {
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                owner._readers++; return new(owner, buffer);
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate) { owner._readers--; owner.ReleaseIfUnused(); }
        }
    }

    private static bool HasAudioHeader(ProgressiveAudioBuffer buffer, string extension)
    {
        if (buffer.Prefix < 10) return false;
        using var reader = buffer.OpenReader();
        Span<byte> head = stackalloc byte[10];
        if (reader.Read(head) < 10) return false;
        long offset = 0;
        if (head[..3].SequenceEqual("ID3"u8))
        {
            for (int i = 6; i < 10; i++) if ((head[i] & 128) != 0) return false;
            offset = 10L + (head[6] << 21 | head[7] << 14 | head[8] << 7 | head[9]) + ((head[5] & 16) != 0 ? 10 : 0);
            if (offset > 16 * 1024 * 1024 || buffer.Prefix < offset + 10 || !reader.Seek(offset)) return false;
            if (reader.Read(head) < 10) return false;
        }
        return extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ? head[..4].SequenceEqual("fLaC"u8)
            : extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) && head[0] == 0xff && (head[1] & 0xe0) == 0xe0
                && (head[1] & 6) != 0 && (head[2] & 0xf0) is not 0 and not 0xf0 && (head[2] & 0x0c) != 0x0c;
    }

    private sealed class Writer(ProgressiveTransfer owner, Stream inner) : Stream, IProgressiveAudioWriter
    {
        public void SetFormatPolicy(Func<string, bool> accepts) { lock (owner._gate) owner._accepts = accepts; }
        public void BeginAudio(long size, string extension)
        {
            lock (owner._gate)
            {
                owner.Unregister();
                owner._buffer!.Begin(size);
                owner._eligible = extension is "mp3" or "flac" && owner._accepts(extension);
                owner._path = extension is "mp3" or "flac" ? owner._basePath + "." + extension : null;
            }
        }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override void SetLength(long value)
        {
            lock (owner._gate) { owner.Unregister(); owner._eligible = false; owner._path = null; inner.SetLength(value); }
        }
        protected override void Dispose(bool disposing) { /* producer End + decoder leases own the file */ }
    }
}
