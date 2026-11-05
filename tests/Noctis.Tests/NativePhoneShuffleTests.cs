using System;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Features.TrueShuffle;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class NativePhoneShuffleTests
{
    [Fact]
    public async Task CustomModeKeepsPlayingSong_ManualQueueComesFirst_AndOffPreservesIt()
    {
        var songs = Enumerable.Range(0, 4).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.Player.PlayTracks(songs.Take(3).ToArray(), 0);
        await rig.Shell.Player.ChooseShuffleAsync(PhoneShuffleMode.NoRepeats);
        Assert.True(rig.Shell.Player.IsShuffleEnabled);
        Assert.Equal(PhoneShuffleMode.NoRepeats, rig.Shell.Player.TrueShuffleMode);
        Assert.Single(rig.Player.PlayedPaths);
        Assert.Same(songs[0], rig.Shell.Player.CurrentTrack);
        rig.Shell.Player.AddToQueue(songs[3]);
        Assert.Same(songs[3], rig.Shell.Player.UpNext[0]);
        await rig.Shell.Player.ChooseShuffleAsync(PhoneShuffleMode.Off);
        Assert.False(rig.Shell.Player.IsShuffleEnabled);
        Assert.Equal(new[] { songs[3], songs[1], songs[2] }, rig.Shell.Player.UpNext);
    }

    [Fact]
    public async Task NeverPlayedUsesTheLibrarySource_ExcludesDislikedAndSnoozed_AndNeverFallsBack()
    {
        var songs = Enumerable.Range(0, 5).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        songs[1].PlayCount = 10;
        songs[2].IsDisliked = true;
        songs[3].SnoozedUntil = DateTime.UtcNow.AddDays(1);
        using var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.Player.PlayTracks([songs[0]], 0);
        await rig.Shell.Player.ChooseShuffleAsync(PhoneShuffleMode.NeverPlayed, ShuffleScope.WholeLibrary);
        Assert.Same(songs[4], Assert.Single(rig.Shell.Player.UpNext));
        await rig.Shell.Player.ChooseShuffleAsync(PhoneShuffleMode.Off);
        rig.Shell.Player.ClearUpNextCommand.Execute(null); // explicit empty queue; a failed mode choice must keep it empty
        songs[4].PlayCount = 2;
        await rig.Shell.Player.ChooseShuffleAsync(PhoneShuffleMode.NeverPlayed, ShuffleScope.WholeLibrary);
        Assert.Equal(PhoneShuffleMode.Off, rig.Shell.Player.TrueShuffleMode);
        Assert.Empty(rig.Shell.Player.UpNext);
        Assert.Contains("No unplayed", rig.Shell.Player.Notice);
    }

    [Fact]
    public async Task LatestModeSelectionWins_AndClearDoesNotAcceptALatePlan()
    {
        var songs = Enumerable.Range(0, 200).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.Player.PlayTracks(songs, 0);
        var first = rig.Shell.Player.ChooseShuffleAsync(PhoneShuffleMode.Discovery);
        await rig.Shell.Player.ChooseShuffleAsync(PhoneShuffleMode.Off);
        await first;
        Assert.Equal(PhoneShuffleMode.Off, rig.Shell.Player.TrueShuffleMode);
        Assert.False(rig.Shell.Player.IsShuffleBusy);
        Assert.Equal(songs.Skip(1), rig.Shell.Player.UpNext);
        var next = rig.Shell.Player.ChooseShuffleAsync(PhoneShuffleMode.NoRepeats);
        rig.Shell.Player.ClearUpNextCommand.Execute(null);
        await next;
        Assert.Empty(rig.Shell.Player.UpNext);
    }

    [Fact]
    public async Task RestoredCustomModeIsLit_AndCanBeChangedBeforeStartingAudio()
    {
        var songs = Enumerable.Range(0, 4).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.Player.PlayTracks(songs, 0);
        await rig.Shell.Player.ChooseShuffleAsync(PhoneShuffleMode.EqualChance);
        await rig.Shell.Player.SaveStateAsync();
        var audio = new FakeAudioPlayer();
        using var restored = new NowPlayingViewModel(audio, rig.Library, rig.Persistence, marshal: a => a());
        await restored.RestoreStateAsync();
        Assert.True(restored.IsShuffleEnabled);
        Assert.Equal(PhoneShuffleMode.EqualChance, restored.TrueShuffleMode);
        await restored.ChooseShuffleAsync(PhoneShuffleMode.Discovery);
        Assert.Empty(audio.PlayedPaths);
        Assert.False(restored.IsPlaying);
        restored.TogglePlayPauseCommand.Execute(null);
        Assert.Equal(songs[0].FilePath, Assert.Single(audio.PlayedPaths));
    }

    [Fact]
    public void NativePickerListsModesWithExactlyOneModeAndScopeChecked()
    {
        using var rig = MobileFixtures.MakeRig();
        rig.Shell.ShowShuffleModesCommand.Execute(null);
        var menu = Assert.IsType<OptionsMenuViewModel>(rig.Shell.OptionsMenu);
        Assert.Equal(new[] { "Off", "Noctis shuffle", "Equal chance", "No repeats", "Discovery", "Never played", "Queue", "Library", "Source" }, menu.Items.Select(i => i.Label));
        Assert.Single(menu.Items.Take(6).Where(i => i.IsChecked));
        Assert.Single(menu.Items.Skip(6).Where(i => i.IsChecked));
    }

    [Fact]
    public async Task ExplicitRequestsBypassNoRepeat_StayFirstAcrossModeChanges_AndSurviveRestart()
    {
        var songs = Enumerable.Range(0, 4).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var player = rig.Shell.Player;
        player.PlayTracks(songs, 0);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));
        await player.ChooseShuffleAsync(PhoneShuffleMode.NoRepeats);
        player.AddToQueue(new[] { songs[0], songs[0] });
        await player.ChooseShuffleAsync(PhoneShuffleMode.Discovery);
        Assert.Equal(new[] { songs[0], songs[0] }, player.UpNext.Take(2));
        await player.SaveStateAsync();
        var audio = new FakeAudioPlayer();
        using var restored = new NowPlayingViewModel(audio, rig.Library, rig.Persistence, marshal: a => a());
        await restored.RestoreStateAsync();
        restored.NextCommand.Execute(null);
        Assert.Same(songs[0], restored.CurrentTrack);
        restored.NextCommand.Execute(null);
        Assert.Same(songs[0], restored.CurrentTrack);
        Assert.Equal(2, audio.PlayedPaths.Count);
    }

    [Fact]
    public async Task RepeatAllStartsANewEngineCycle_WhileClearNeverRefills()
    {
        var songs = Enumerable.Range(0, 3).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var player = rig.Shell.Player;
        player.PlayTracks(songs, 0);
        await player.ChooseShuffleAsync(PhoneShuffleMode.NoRepeats);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));
        player.CycleRepeatCommand.Execute(null);
        Assert.Equal(Noctis.Models.RepeatMode.All, player.RepeatMode);
        player.NextCommand.Execute(null);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));
        player.NextCommand.Execute(null);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));
        var old = player.CurrentTrack;
        var count = rig.Player.PlayedPaths.Count;
        player.NextCommand.Execute(null);
        for (var i = 0; i < 500 && rig.Player.PlayedPaths.Count == count; i++) await Task.Delay(10);
        Assert.Equal(count + 1, rig.Player.PlayedPaths.Count);
        Assert.NotSame(old, player.CurrentTrack);
        Assert.Equal(PhoneShuffleMode.NoRepeats, player.TrueShuffleMode);
        player.ClearUpNextCommand.Execute(null);
        count = rig.Player.PlayedPaths.Count;
        player.NextCommand.Execute(null);
        Assert.Equal(count, rig.Player.PlayedPaths.Count);
        Assert.Empty(player.UpNext);
        Assert.True(player.IsShuffleEnabled);
    }

    [Fact]
    public async Task NeverPlayedStopsAtTheEnd_EvenWhenRepeatAllIsOn()
    {
        var songs = Enumerable.Range(0, 2).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.Player.PlayTracks(songs, 0);
        await rig.Shell.Player.ChooseShuffleAsync(PhoneShuffleMode.NeverPlayed);
        rig.Shell.Player.CycleRepeatCommand.Execute(null);
        rig.Player.RaiseTrackEnded();
        Assert.Same(songs[1], rig.Shell.Player.CurrentTrack);
        rig.Player.RaiseTrackEnded();
        Assert.False(rig.Shell.Player.IsPlaying);
        Assert.Equal(2, rig.Player.PlayedPaths.Count);
    }
}
