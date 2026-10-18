using LibVLCSharp.Shared;
using Noctis.Features.ProgressivePlayback;
using Xunit;

namespace Noctis.Tests;

public class ProgressiveMediaOwnershipTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    public async Task OwnedInputDecodesPartialAudio_WithTheAppsDemuxSetting_AndReleasesTheFileAfterStop(string extension)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires bundled Windows LibVLC.");
        Core.Initialize(); using var data = new TestPersistenceService();
        var audio = await ProgressiveDecoderTests.AudioAsync(extension, data.DataDirectory, noisy: true);
        var partial = Path.Combine(data.DataDirectory, "download.fmf.part");
        var transfer = new ProgressiveTransfer(data.DataDirectory, "download", TimeSpan.FromSeconds(24));
        var writer = transfer.Attach(new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1));
        ((IProgressiveAudioWriter)writer).SetFormatPolicy(_ => true);
        ((IProgressiveAudioWriter)writer).BeginAudio(audio.Length, extension);
        writer.Write(audio.AsSpan(0, audio.Length * 2 / 3));
        string? path = null;
        for (int n = 0; n < 100 && path is null; n++) { path = transfer.ReadyPath; if (path is null) await Task.Delay(20); }
        Assert.NotNull(path); Assert.False(File.Exists(path));
        using var vlc = new LibVLC("--intf=dummy", "--no-video", "--no-stats", "--quiet", "--demux=avformat");
        using var media = ProgressiveMedia.Create(vlc, ProgressiveTransfer.Acquire(path)!);
        media.AddOption(extension == "mp3" ? ":demux=mp3" : ":demux=flac");
        using var player = new MediaPlayer(vlc);
        var decoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var underrun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        media.Buffering += waiting => { if (waiting) underrun.TrySetResult(); };
        long samples = 0;
        MediaPlayer.LibVLCAudioPlayCb play = (_, _, count, _) => { Interlocked.Add(ref samples, count); decoded.TrySetResult(); };
        player.SetAudioCallbacks(play, null, null, null, null); player.SetAudioFormat("S16N", 48000, 1);
        try
        {
            Assert.True(player.Play(media)); await decoded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(samples > 0); Assert.False(media.Complete);
            output.WriteLine($"{extension}: decoded {samples:N0} PCM samples from {audio.Length * 2 / 3:N0}/{audio.Length:N0} bytes, producer still active.");
            // A reader may be waiting for missing bytes when skip/Stop arrives.
            media.CancelRead();
            await Task.Run(player.Stop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(media.Cancelled);
        }
        finally
        {
            media.CancelRead(); await Task.Run(player.Stop).WaitAsync(TimeSpan.FromSeconds(5));
            media.Dispose(); transfer.End(null, new IOException("Synthetic download cancelled")); GC.KeepAlive(play);
        }
        using var exclusive = File.Open(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
