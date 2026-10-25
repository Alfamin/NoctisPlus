using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FreeMusicFinder;

internal interface ISonicEncoder : IDisposable
{
    string Model { get; }
    bool Ready { get; }
    Task<SonicVector?> EncodeAsync(byte[] preview, CancellationToken ct);
}

/// <summary>One lazy CPU session, two threads, serialized inference. It never downloads a model and never opens music.
/// Encoded previews and decoded PCM stay in memory; ffmpeg is the app's existing configured helper.</summary>
internal sealed class SonicEncoder(string modelPath, string model, Func<string?> ffmpeg, string? expectedSha256 = null) : ISonicEncoder
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _session;
    private DateTime _used;
    private volatile bool _disposed;
    private Timer? _idle;
    private (long Bytes, DateTime Written)? _verified;
    private (long Bytes, DateTime Written)? _broken;
    public string Model => model;
    public bool Ready
    {
        get
        {
            try { var f = new FileInfo(modelPath); return !_disposed && f.Exists && _broken != (f.Length, f.LastWriteTimeUtc) && ffmpeg() is not null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    public async Task<SonicVector?> EncodeAsync(byte[] preview, CancellationToken ct)
    {
        if (!Ready) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return null;
            _used = DateTime.UtcNow;
            _idle ??= new Timer(_ => ReleaseWhenIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _idle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var pcm = await DecodeAsync(ffmpeg()!, preview, ct).ConfigureAwait(false);
            if (pcm.Length < 5 * SonicClapFeatures.Rate || pcm.Any(x => !float.IsFinite(x)) || pcm.Average(x => (double)x * x) < 1e-10) return null;
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (_session is null)
                {
                    var file = new FileInfo(modelPath);
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Redirected sound model");
                    var stamp = (file.Length, file.LastWriteTimeUtc);
                    if (expectedSha256 is not null && _verified != stamp)
                    {
                        using var stream = File.OpenRead(modelPath);
                        if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                        { _broken = stamp; throw new InvalidDataException("Sound model checksum mismatch"); }
                        _verified = stamp;
                    }
                    using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
                    try { _session = new InferenceSession(modelPath, options); }
                    catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or TypeInitializationException)
                    { _broken = stamp; throw; }
                }
                var vectors = new List<float[]>();
                for (int at = 0; at < pcm.Length; at += SonicClapFeatures.Samples)
                {
                    int n = Math.Min(SonicClapFeatures.Samples, pcm.Length - at);
                    if (n < 5 * SonicClapFeatures.Rate) break;
                    var features = SonicClapFeatures.Extract(pcm.AsSpan(at, n), ct);
                    using var run = new RunOptions();
                    using var cancel = ct.Register(() => run.Terminate = true);
                    var tensor = new DenseTensor<float>(features, new[] { 1, 1, SonicClapFeatures.Frames, SonicClapFeatures.Bins });
                    using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input_features", tensor)], _session.OutputNames, run);
                    var vector = SonicVector.Create(Model, "preview", results.First().AsEnumerable<float>());
                    if (vector is not null) vectors.Add(vector.Values);
                }
                return vectors.Count == 0 ? null : SonicVector.Create(Model, "preview", Enumerable.Range(0, vectors[0].Length).Select(i => vectors.Average(v => v[i])));
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            if (_disposed) { _idle?.Dispose(); _session?.Dispose(); _session = null; }
            else _idle?.Change(TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    private void ReleaseWhenIdle()
    {
        if (!_gate.Wait(0)) return;
        try { if (DateTime.UtcNow - _used >= TimeSpan.FromMinutes(2)) { _session?.Dispose(); _session = null; } }
        finally { _gate.Release(); }
    }

    private static async Task<float[]> DecodeAsync(string exe, byte[] data, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-t", "30", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1" }) psi.ArgumentList.Add(arg);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = Process.Start(psi) ?? throw new IOException("Audio decoder unavailable");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) { }
        void Kill() { try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
        using var cancel = deadline.Token.Register(Kill);
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        var write = Task.Run(async () => { await process.StandardInput.BaseStream.WriteAsync(data, deadline.Token).ConfigureAwait(false); process.StandardInput.Close(); }, deadline.Token);
        try
        {
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                int n = await process.StandardOutput.BaseStream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (n == 0) break;
                if (output.Length + n > 31 * 48000 * sizeof(float)) throw new InvalidDataException("Audio outside preview budget");
                output.Write(buffer, 0, n);
            }
            await write.ConfigureAwait(false); await errors.ConfigureAwait(false); await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || output.Length % sizeof(float) != 0) throw new InvalidDataException("Invalid preview audio");
            return MemoryMarshal.Cast<byte, float>(output.ToArray()).ToArray();
        }
        finally
        {
            Kill();
            // Observe failed/broken stdin and stderr even when decoding was cancelled or exceeded its output budget.
            _ = write.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _ = errors.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_gate.Wait(0)) try { _idle?.Dispose(); _session?.Dispose(); _session = null; } finally { _gate.Release(); }
    }
}
