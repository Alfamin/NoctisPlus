#if DEBUG && NOCTIS_PROGRESSIVE_PROBE
using Android.App;
using Android.OS;
using Android.Widget;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using Noctis.Android.Services;
using Noctis.Features.ProgressivePlayback;
using ALog = Android.Util.Log;
using Environment = System.Environment;
using OperationCanceledException = System.OperationCanceledException;

namespace Noctis.Android.Diagnostics;

/// <summary>
/// Opt-in Debug-only device check of the PRODUCTION data-source factory and real Media3 decoder.
/// No account, network, app profile or music library is accessed. Supply a synthetic 40s probe.mp3/flac
/// in this audit package's private files directory. Not included in ordinary Debug or any Release build.
/// </summary>
[Activity(Name = "com.alfamin.noctisplus.ProgressiveProbeActivity", Exported = true)]
public sealed class ProgressivePlaybackProbe : Activity
{
    private const string Tag = "NoctisProbe";
    private readonly CancellationTokenSource _stop = new();
    private IExoPlayer? _player;
    private ProgressiveTransfer? _transfer;
    private TextView? _text;

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _text = new TextView(this) { Text = "Checking progressive playback…", TextSize = 20 };
        SetContentView(_text);
        try { await RunAsync(); }
        catch (OperationCanceledException) { Report("CANCELLED"); }
        catch (Exception ex) { Report("FAIL " + ex); }
    }

    private async Task RunAsync()
    {
        var extension = Intent?.GetStringExtra("format") == "flac" ? "flac" : "mp3";
        var folder = FilesDir!.AbsolutePath;
        var input = Path.Combine(folder, "probe." + extension);
        var part = Path.Combine(folder, "probe-growing.part");
        var final = Path.Combine(folder, "probe-renamed (2)." + extension);
        File.Delete(Path.Combine(folder, "probe-growing." + extension)); // only this probe's previous synthetic output
        var audio = await File.ReadAllBytesAsync(input, _stop.Token);
        var initial = (int)(audio.Length * .4);
        _transfer = new ProgressiveTransfer(folder, "probe-growing", TimeSpan.FromSeconds(40));
        var file = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1);
        var writer = _transfer.Attach(file);
        var format = (IProgressiveAudioWriter)writer;
        format.SetFormatPolicy(_ => true);
        format.BeginAudio(audio.Length, extension);
        await writer.WriteAsync(audio.AsMemory(0, initial), _stop.Token);
        await Task.Delay(650, _stop.Token);
        var path = _transfer.ReadyPath ?? throw new IOException("Initial 40% did not become playable");
        // Exercise a closed load whose transfer completes under a collision-renamed path.
        // This must retain metadata only, not a reader lease that pins the writer forever.
        var reopen = new ProgressiveDataSourceFactory(new DefaultDataSource.Factory(this)).CreateDataSource();
        using var originalFile = new Java.IO.File(path);
        using var originalUri = global::Android.Net.Uri.FromFile(originalFile);
        using var firstSpec = new DataSpec(originalUri!);
        reopen.Open(firstSpec); reopen.Close();
        Require(!_transfer.HasReaders, "Closed load retained a reader");
        _player = new ExoPlayerBuilder(this)
            .SetMediaSourceFactory(new DefaultMediaSourceFactory(new ProgressiveDataSourceFactory(new DefaultDataSource.Factory(this))))
            .Build();
        _player.Volume = 0; // real decoding/output clock, without disturbing the owner
        _player.SetMediaItem(MediaItem.FromUri(global::Android.Net.Uri.FromFile(new Java.IO.File(path))));
        _player.Prepare();
        _player.Play();
        await UntilAsync(() => _player.CurrentPosition >= 250, 12000);
        Require(!_transfer.Finished, "First audio waited for the complete transfer");
        Report($"FIRST_AUDIO format={extension} position={_player.CurrentPosition} received={initial}/{audio.Length} complete=False");

        _player.Pause();
        await Task.Delay(200, _stop.Token);
        var paused = _player.CurrentPosition;
        await Task.Delay(800, _stop.Token);
        Require(Math.Abs(_player.CurrentPosition - paused) <= 80, "Pause advanced the output position");
        _player.Play();
        await UntilAsync(() => _player.CurrentPosition >= paused + 250, 5000);
        Report("PAUSE_RESUME stable=True");

        // A disconnected sequential producer: no EOF, no looping buffered prefix, no runaway clock.
        await UntilAsync(() => _player.PlaybackState == BasePlayer.InterfaceConsts.StateBuffering, 24000);
        var stalled = _player.CurrentPosition;
        await Task.Delay(1000, _stop.Token);
        Require(_player.PlaybackState != BasePlayer.InterfaceConsts.StateEnded, "Underflow reported end of song");
        Require(Math.Abs(_player.CurrentPosition - stalled) <= 100, "Position advanced while starved");
        Report($"STARVED position={stalled} stable=True");
        await writer.WriteAsync(audio.AsMemory(initial), _stop.Token);
        File.Move(part, final, overwrite: true);
        _transfer.End(final);
        Require(!File.Exists(path), "Old probe output would mask a missing-path regression");
        using var seekSpec = new DataSpec(originalUri!, 1000, 16);
        Require(reopen.Open(seekSpec) == 16, "Reopen lost the requested byte range");
        var check = new byte[16];
        Require(reopen.Read(check, 0, check.Length) == 16 && check.AsSpan().SequenceEqual(audio.AsSpan(1000, 16)), "Reopen read the wrong file/offset");
        reopen.Close();
        Report("RENAMED_REOPEN passed=True closedBeforeCompletion=True");
        await UntilAsync(() => _player.CurrentPosition >= stalled + 250, 7000);
        Report($"RESUMED position={_player.CurrentPosition} noRestart=True");

        _player.SeekTo(2000);
        await UntilAsync(() => _player.CurrentPosition is >= 1800 and < 4500 && _player.IsPlaying, 7000);
        Report("COMPLETED_SEEK passed=True");
        _player.Stop();
        _player.ClearMediaItems();
        Report($"PASS format={extension} firstAudioBeforeComplete pauseResume underflowResume completedSeek");
    }

    private async Task UntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException($"Playback wait expired (state={_player?.PlaybackState}, position={_player?.CurrentPosition})");
            await Task.Delay(50, _stop.Token);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }
    private void Report(string text) { ALog.Info(Tag, text); if (_text != null) _text.Text = text; }

    protected override void OnDestroy()
    {
        _stop.Cancel();
        _player?.Release();
        _transfer?.End(null, new OperationCanceledException("Probe closed"));
        base.OnDestroy();
    }
}
#endif
