using System.Runtime.CompilerServices;
using Noctis.Features.ProgressivePlayback;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Release 2.7.1: a streamed song whose download is still running has no file yet. The
/// missing-file check (PlayerViewModel.CheckMissing, via IAudioPlayer.SourceExists) passed
/// it over, which stopped its download. SourceExists must count a running transfer.
/// </summary>
public class StreamingSongNotMissingTests
{
    [Fact]
    public async Task RunningTransfer_WithNoFileYet_IsNotMissing()
    {
        using var data = new TestPersistenceService();
        var part = Path.Combine(data.DataDirectory, "scratch.fmf.part");
        var target = Path.Combine(data.DataDirectory, "song.flac");
        var transfer = new ProgressiveTransfer(data.DataDirectory, "song", TimeSpan.FromMinutes(3));
        var writer = transfer.Attach(new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1));
        ((IProgressiveAudioWriter)writer).SetFormatPolicy(_ => true);
        ((IProgressiveAudioWriter)writer).BeginAudio(1024 * 1024, "flac");
        var player = (VlcAudioPlayer)RuntimeHelpers.GetUninitializedObject(typeof(VlcAudioPlayer));
        try
        {
            var bytes = new byte[1024 * 1024]; "fLaC"u8.CopyTo(bytes);
            writer.Write(bytes);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (transfer.ReadyPath is null)
            {
                Assert.True(DateTime.UtcNow < until, "partial audio never became ready");
                await Task.Delay(10);
            }

            Assert.False(File.Exists(target));
            Assert.True(ProgressiveTransfer.IsAvailable(target));
            Assert.True(player.SourceExists(target));
        }
        finally
        {
            transfer.End(null, new IOException("test ended"));
        }

        // Once the download has failed and nothing is on disk, the song really is missing.
        Assert.False(player.SourceExists(target));
    }
}
