using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #90: with every island button switched on the island went past the window. It now fits: optional
/// buttons step aside, least needed first, and come back when there is room.</summary>
[Collection("ArtistCredit global configuration")]
public class IslandFitTests
{
    private static (Window Win, PlaybackBarView Bar) Open(double width)
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            IslandShowSkipButtons = true, IslandShowPlaybackSpeed = true, IslandShowSleepTimer = true, IslandShowShuffle = true,
            IslandShowEqualizer = true, IslandShowRepeat = true, IslandShowFavorite = true, IslandShowMiniPlayer = true, IslandShowTime = true,
        };
        player.CurrentTrack = new Track { Id = Guid.NewGuid(), Title = "Work", Artist = "Rihanna", Duration = TimeSpan.FromSeconds(200), FilePath = "C:/m/work.mp3" };
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var win = new Window { Width = width, Height = 600, Content = new Panel { Children = { bar } } };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        return (win, bar);
    }

    private static bool Shown(PlaybackBarView bar, string name) => bar.FindControl<Button>(name)!.IsVisible;

    [AvaloniaFact]
    public void EveryButtonOn_InANarrowWindow_FitsInside_AndTheLeastNeededStepAside()
    {
        var (win, bar) = Open(820);
        var island = bar.FindControl<Border>("IslandBorder")!;
        Assert.True(island.Bounds.Width <= 820 - 16, $"island {island.Bounds.Width} px in an 820 px window");
        Assert.False(Shown(bar, "MiniPlayerButton"));   // first to step aside
        Assert.True(Shown(bar, "NativeShuffleButton")); // the most needed stays
        win.Width = 1800; Dispatcher.UIThread.RunJobs();
        Assert.True(Shown(bar, "MiniPlayerButton") && Shown(bar, "SleepTimerButton") && Shown(bar, "PlaybackSpeedButton")); // all back
        win.Close();
    }

    [AvaloniaFact]
    public void AWideWindow_ShowsEveryButton_AndTheIslandGrowsForThem()
    {
        var (win, bar) = Open(1800);
        foreach (var name in new[] { "MiniPlayerButton", "SleepTimerButton", "PlaybackSpeedButton", "EqualizerButton", "SkipBackButton", "SkipForwardButton", "RepeatButton", "FavoriteButton", "NativeShuffleButton" })
            Assert.True(Shown(bar, name), name);
        Assert.True(bar.FindControl<Border>("IslandBorder")!.Bounds.Width > 536);
        win.Close();
    }
}
