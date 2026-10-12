using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Double-clicking an Up Next row (Queue panel, mini player, a Cover Flow card ahead) played
/// it through ReplaceQueueAndPlay, i.e. as a brand-new queue: Shuffle switched off, Repeat All's
/// cycle shrank to the rows from the clicked one on, and radio ended. Found on GitHub #134.
/// </summary>
public class QueuePlayFromUpNextTests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player) CreateVm(bool keepPlayed = false)
    {
        var player = new FakeAudioPlayer();
        var vm = new PlayerViewModel(player, new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            KeepPlayedInQueue = keepPlayed
        };
        return (vm, player);
    }

    private static Track Trk(string name) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = TestPaths.Primary("t", $"{name}.mp3"),
        Duration = TimeSpan.FromMinutes(3)
    };

    private static Track[] Album(params string[] names) => names.Select(Trk).ToArray();

    private static void End(FakeAudioPlayer player, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            player.RaiseTrackEnded();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Titles(IEnumerable<Track> tracks) => string.Join(",", tracks.Select(t => t.Title));

    [AvaloniaFact]
    public void PlayFromUpNextAt_KeepsShuffleOn()
    {
        var (vm, _) = CreateVm(keepPlayed: false);
        vm.PlayShuffled(Album("a", "b", "c", "d", "e"));
        Assert.True(vm.IsShuffleEnabled);

        vm.PlayFromUpNextAt(1);

        Assert.True(vm.IsShuffleEnabled);
    }

    [AvaloniaFact]
    public void PlayFromUpNextAt_RepeatAllStillReplaysTheWholeQueue()
    {
        var (vm, player) = CreateVm(keepPlayed: false);
        vm.RepeatMode = RepeatMode.All;
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);

        vm.PlayFromUpNextAt(1); // c
        End(player, 2); // c, d → wraps

        Assert.Equal("a", vm.CurrentTrack?.Title);
        Assert.Equal("b,c,d", Titles(vm.UpNext));
    }

    [AvaloniaFact]
    public void PlayFromUpNextAt_KeepsRadioOn()
    {
        var (vm, _) = CreateVm(keepPlayed: false);
        vm.ReplaceQueueAndPlay(Album("a", "b", "c"), 0);
        vm.IsRadioActive = true;

        vm.PlayFromUpNextAt(1);

        Assert.True(vm.IsRadioActive);
        Assert.Equal("c", vm.CurrentTrack?.Title);
    }

    [AvaloniaFact]
    public void PlayFromUpNextAt_PreviousReturnsToTheSongThatWasPlaying()
    {
        var (vm, _) = CreateVm(keepPlayed: false);
        vm.ReplaceQueueAndPlay(Album("a", "b", "c", "d"), 0);

        vm.PlayFromUpNextAt(2); // d

        vm.PreviousCommand.Execute(null);
        Assert.Equal("a", vm.CurrentTrack?.Title);
        Assert.Equal("d", Titles(vm.UpNext));
    }
}
