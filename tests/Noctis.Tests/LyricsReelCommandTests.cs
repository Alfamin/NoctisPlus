using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

[Collection("ArtistCredit global configuration")]
public class LyricsReelCommandTests
{
    [AvaloniaFact]
    public async Task MakeReel_ForwardsTheTrackAndAwaitsItsOpener()
    {
        var previous = LyricsReel.Opener;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Track? received = null;
        try
        {
            LyricsReel.Opener = track => { received = track; return release.Task; };
            var track = new Track { Title = "Selected, not playing" };
            var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(LyricsReel.OpenForTrackCommand);
            var pending = command.ExecuteAsync(track);
            Assert.Same(track, received);
            Assert.False(pending.IsCompleted);
            release.SetResult();
            await pending;
            received = null;
            await command.ExecuteAsync(null);
            Assert.Null(received);
        }
        finally { release.TrySetResult(); LyricsReel.Opener = previous; }
    }
}
