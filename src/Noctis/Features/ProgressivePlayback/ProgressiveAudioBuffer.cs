using System.Diagnostics;

namespace Noctis.Features.ProgressivePlayback;

/// <summary>
/// A transfer's disk-backed bytes, including the holes left by parallel Telegram chunks.
/// Readers wait for real bytes; a temporary tail is never EOF. Playback never owns the
/// transfer, so cancelling one reader leaves the download and other readers intact.
/// </summary>
internal sealed class ProgressiveAudioBuffer : IDisposable
{
    private readonly object _gate = new();
    private FileStream _file;
    private readonly bool _leaveOpen;
    private readonly List<(long Start, long End)> _ranges = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Exception? _error;
    private bool _complete, _disposed;
    private int _generation;
    private long _expected;
    internal Stream Writer { get; }
    internal long Prefix { get { lock (_gate) return PrefixLocked; } }
    internal long Expected { get { lock (_gate) return _expected; } }
    internal bool Complete { get { lock (_gate) return _complete && _error is null; } }
    private long PrefixLocked => _ranges.Count > 0 && _ranges[0].Start == 0 ? _ranges[0].End : 0;

    // The file should have a one-byte FileStream buffer: publication follows a completed
    // OS write, not data still sitting in FileStream's private managed buffer.
    internal ProgressiveAudioBuffer(FileStream file, bool leaveOpen = false)
    {
        if (!file.CanRead || !file.CanWrite || !file.CanSeek) throw new ArgumentException("A read/write seekable transfer file is required.", nameof(file));
        _file = file; _leaveOpen = leaveOpen; Writer = new TransferWriter(this);
    }

    internal void Begin(long expected)
    {
        if (expected <= 0 || expected > 4L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(expected));
        lock (_gate) { ThrowIfDisposed(); _expected = expected; _clock.Restart(); }
    }

    /// <summary>Conservative startup based on duration and observed contiguous throughput.
    /// If the network cannot sustain playback, more of the recording is buffered first.</summary>
    internal bool ReadyToPlay(TimeSpan duration)
    {
        lock (_gate)
        {
            if (_disposed || _error is not null) return false;
            if (_complete) return true;
            if (_expected <= 0 || duration.TotalSeconds <= 0) return false;
            var prefix = PrefixLocked;
            var seconds = duration.TotalSeconds;
            var bytesPerSecond = _expected / seconds;
            var elapsed = _clock.Elapsed.TotalSeconds;
            if (elapsed < .5) return false; // do not estimate throughput from a single burst
            var throughput = prefix / elapsed * .8; // headroom for fluctuating links
            var secondsNeeded = Math.Min(seconds, Math.Max(12, seconds - throughput / bytesPerSecond * Math.Max(0, seconds - 4)));
            var target = Math.Min(_expected, Math.Max(64 * 1024, bytesPerSecond * secondsNeeded));
            return prefix >= target;
        }
    }

    internal void Finish(Exception? error = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _error = error;
            if (error is null)
            {
                _file.Flush();
                if (_expected <= 0) _expected = _file.Length;
                if (_expected == 0 || PrefixLocked != _expected || _file.Length != _expected)
                    _error = new IOException("INCOMPLETE_AUDIO: the transfer ended with missing or unexpected bytes.");
            }
            _complete = true; Monitor.PulseAll(_gate);
        }
    }

    internal AudioReader OpenReader(CancellationToken ct = default)
    {
        lock (_gate) { ThrowIfDisposed(); return new(this, _generation, ct); }
    }

    /// <summary>The producer is finished; release write access while preserving the
    /// decoder's file. TagLib's normal read/share-read open rejects a live write handle.</summary>
    internal void SealReadOnly(string savedPath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_complete || _error is not null) throw new IOException("Only a validated complete transfer can release its writer.");
            if (_leaveOpen) throw new InvalidOperationException("A borrowed transfer file cannot change ownership.");
            var read = new FileStream(savedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            if (read.Length != _expected) { read.Dispose(); throw new IOException("The saved audio changed before playback could retain it."); }
            var write = _file; _file = read; write.Dispose();
        }
    }

    private int Read(int generation, long position, Span<byte> target, CancellationToken ct)
    {
        if (target.Length == 0) return 0;
        using var wake = ct.Register(() => { lock (_gate) Monitor.PulseAll(_gate); });
        lock (_gate)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested(); ThrowIfDisposed();
                if (generation != _generation) throw new IOException("A backup transfer replaced this audio. Wait for the complete download.");
                if (_error is not null) throw new IOException("The audio transfer was interrupted.", _error);
                var available = PrefixLocked - position;
                if (available > 0)
                    return RandomAccess.Read(_file.SafeFileHandle, target[..(int)Math.Min(available, target.Length)], position);
                if (_complete) return 0;
                Monitor.Wait(_gate);
            }
        }
    }

    private void Written(long start, long count)
    {
        var end = checked(start + count);
        int i = 0;
        while (i < _ranges.Count && _ranges[i].End < start) i++;
        while (i < _ranges.Count && _ranges[i].Start <= end)
        { start = Math.Min(start, _ranges[i].Start); end = Math.Max(end, _ranges[i].End); _ranges.RemoveAt(i); }
        _ranges.Insert(i, (start, end)); Monitor.PulseAll(_gate);
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Monitor.PulseAll(_gate); if (!_leaveOpen) _file.Dispose(); }
    }

    internal sealed class AudioReader : IDisposable
    {
        private readonly ProgressiveAudioBuffer _owner;
        private readonly int _generation;
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _token;
        private long _position;
        private int _disposed;
        internal AudioReader(ProgressiveAudioBuffer owner, int generation, CancellationToken ct)
        { _owner = owner; _generation = generation; _stop = CancellationTokenSource.CreateLinkedTokenSource(ct); _token = _stop.Token; }
        internal long Position => _position;
        internal int Read(Span<byte> target)
        { var count = _owner.Read(_generation, _position, target, _token); _position += count; return count; }
        internal bool Seek(long position)
        {
            lock (_owner._gate)
            {
                if (_owner._disposed || _token.IsCancellationRequested || _generation != _owner._generation || position < 0 || position > _owner.PrefixLocked) return false;
                _position = position; return true;
            }
        }
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _stop.Cancel(); _stop.Dispose(); }
    }

    private sealed class TransferWriter(ProgressiveAudioBuffer owner) : Stream
    {
        public override bool CanRead => !owner._disposed;
        public override bool CanSeek => !owner._disposed;
        public override bool CanWrite => !owner._disposed;
        public override long Length { get { lock (owner._gate) return owner._file.Length; } }
        public override long Position { get { lock (owner._gate) return owner._file.Position; } set { lock (owner._gate) owner._file.Position = value; } }
        public override long Seek(long offset, SeekOrigin origin) { lock (owner._gate) return owner._file.Seek(offset, origin); }
        public override void Flush() { lock (owner._gate) owner._file.Flush(); }
        public override Task FlushAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
        public override int Read(byte[] data, int offset, int count) { lock (owner._gate) return owner._file.Read(data, offset, count); }
        public override void Write(byte[] data, int offset, int count) => Write(data.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> data)
        {
            lock (owner._gate)
            {
                owner.ThrowIfDisposed();
                if (owner._complete) throw new IOException("The transfer has already ended.");
                long start = owner._file.Position; owner._file.Write(data); owner._file.Flush();
                if (data.Length > 0) owner.Written(start, data.Length);
            }
        }
        public override Task WriteAsync(byte[] data, int offset, int count, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Write(data, offset, count); return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Write(data.Span); return ValueTask.CompletedTask; }
        public override void SetLength(long length)
        {
            if (length != 0) throw new NotSupportedException("Only resetting a replacement transfer is supported.");
            lock (owner._gate)
            {
                owner.ThrowIfDisposed(); owner._file.SetLength(0); owner._file.Position = 0;
                owner._ranges.Clear(); owner._expected = 0; owner._complete = false; owner._error = null;
                owner._generation++; owner._clock.Restart(); Monitor.PulseAll(owner._gate);
            }
        }
        // The producer's file and consumer leases are owned by ProgressiveAudioBuffer.
        protected override void Dispose(bool disposing) { }
    }
}
