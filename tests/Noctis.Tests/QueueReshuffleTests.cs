using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Queue ▸ Reshuffle (Discord, vari): a new random order for Up Next while Shuffle is on. Only
/// Up Next moves — the playing song, the played songs and the Shuffle-off order stay — every
/// queued song stays queued ("Play Next" ones too, which ToggleShuffle shuffles like the rest),
/// and with Shuffle off (or nothing to trade places) the command is disabled.
/// </summary>
public class QueueReshuffleTests
{
    [Fact]
    public void WeightedReshuffle_NeverReordersATrueShuffleSession()
    {
        var vm = CreateVm();
        vm.PlayShuffled(Album(20));
        var order = vm.UpNext.ToArray();
        foreach (var mode in new[] { PlayerShuffleMode.EqualChance, PlayerShuffleMode.NoRepeats,
                     PlayerShuffleMode.Discovery, PlayerShuffleMode.NeverPlayed, PlayerShuffleMode.ForYou })
        {
            vm.ShuffleMode = mode;
            Assert.False(vm.ShowReshuffle);
            Assert.False(vm.ReshuffleCommand.CanExecute(null));
            vm.ReshuffleCommand.Execute(null);
            Assert.Equal(order, vm.UpNext);
        }
    }
    private static PlayerViewModel CreateVm() =>
        new(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());

    private static Track Song(int number, bool skipWhenShuffling = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = $"Song {number}",
        Artist = "A",
        Album = "Album",
        TrackNumber = number,
        DiscNumber = 1,
        FilePath = TestPaths.Primary("Music", "Album", $"{number:00}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
        SkipWhenShuffling = skipWhenShuffling,
    };

    private static List<Track> Album(int count) => Enumerable.Range(1, count).Select(n => Song(n)).ToList();

    private static List<Guid> Ids(IEnumerable<Track> tracks) => tracks.Select(t => t.Id).ToList();

    [Fact]
    public void Reshuffle_NewUpNextOrder_KeepsThePlayingSong_ThePlayedSongs_AndEveryQueuedSong()
    {
        var vm = CreateVm();
        vm.KeepPlayedInQueue = true;
        vm.PlayShuffled(Album(30));
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        var playNext = Song(99);
        vm.AddNext(playNext, announce: false);

        var current = vm.CurrentTrack;
        var history = vm.History.ToList();
        var played = vm.PlayedTracks.ToList();
        var before = vm.UpNext.ToList();
        Assert.True(vm.ReshuffleCommand.CanExecute(null));

        vm.ReshuffleCommand.Execute(null);

        Assert.True(vm.IsShuffleEnabled);
        Assert.Same(current, vm.CurrentTrack);
        Assert.Equal(history, vm.History);
        Assert.Equal(played, vm.PlayedTracks);
        // Same songs, one row each (the Play Next one included), in a new order.
        Assert.Equal(before.Count, vm.UpNext.Count);
        Assert.Equal(Ids(before).Order(), Ids(vm.UpNext).Order());
        Assert.Contains(playNext, vm.UpNext);
        Assert.NotEqual(Ids(before), Ids(vm.UpNext));
        // The Queue panel's rows follow: played, the playing song, then the new Up Next.
        Assert.Equal(played.Append(current!).Concat(vm.UpNext), vm.QueueRows);
    }

    [Fact]
    public void Reshuffle_IsDisabled_WithShuffleOff_AndDoesNothing()
    {
        var vm = CreateVm();
        var album = Album(10);
        vm.ReplaceQueueAndPlay(album, 0);
        Assert.False(vm.IsShuffleEnabled);
        var before = vm.UpNext.ToList();

        Assert.False(vm.ReshuffleCommand.CanExecute(null));
        vm.ReshuffleCommand.Execute(null);
        Assert.Equal(before, vm.UpNext);

        // Shuffle on enables it; off again disables it.
        vm.ToggleShuffleCommand.Execute(null);
        Assert.True(vm.ReshuffleCommand.CanExecute(null));
        vm.ToggleShuffleCommand.Execute(null);
        Assert.False(vm.ReshuffleCommand.CanExecute(null));
    }

    [Fact]
    public void Reshuffle_NeedsTwoSongsUpNext_AndFollowsTheQueue()
    {
        var vm = CreateVm();
        vm.ReplaceQueueAndPlay(Album(3), 0);
        vm.ToggleShuffleCommand.Execute(null);
        var changes = 0;
        vm.ReshuffleCommand.CanExecuteChanged += (_, _) => changes++;
        Assert.True(vm.ReshuffleCommand.CanExecute(null)); // two up next

        vm.NextCommand.Execute(null); // one left
        Assert.False(vm.ReshuffleCommand.CanExecute(null));
        Assert.True(changes > 0, "the buttons hear the queue change");

        vm.AddToQueue(Song(9), announce: false);
        Assert.True(vm.ReshuffleCommand.CanExecute(null));
    }

    [Fact]
    public void Reshuffle_TwoSongs_AlwaysSwapsThem()
    {
        for (var run = 0; run < 25; run++)
        {
            var vm = CreateVm();
            vm.ReplaceQueueAndPlay(Album(3), 0);
            vm.ToggleShuffleCommand.Execute(null);
            var before = vm.UpNext.ToList();
            vm.ReshuffleCommand.Execute(null);
            Assert.Equal(new[] { before[1], before[0] }, vm.UpNext);
        }
    }

    [Fact]
    public void Reshuffle_SongsTheShuffleHoldsOut_KeepTheirSlot_InsteadOfBeingDropped()
    {
        var vm = CreateVm();
        vm.ReplaceQueueAndPlay(Album(12), 0);
        vm.ToggleShuffleCommand.Execute(null);
        // Queued by hand while shuffled: Skip When Shuffling and a snoozed song.
        var skip = Song(50, skipWhenShuffling: true);
        var snoozed = Song(51);
        snoozed.SnoozedUntil = DateTime.UtcNow.AddDays(1);
        vm.AddToQueue(skip, announce: false);
        vm.AddNext(snoozed, announce: false);
        var skipAt = vm.UpNext.IndexOf(skip);
        var snoozedAt = vm.UpNext.IndexOf(snoozed);
        var count = vm.UpNext.Count;

        vm.ReshuffleCommand.Execute(null);

        Assert.Equal(count, vm.UpNext.Count);
        Assert.Equal(skipAt, vm.UpNext.IndexOf(skip));
        Assert.Equal(snoozedAt, vm.UpNext.IndexOf(snoozed));
    }

    [Fact]
    public void Reshuffle_ThenShuffleOff_StillContinuesAfterThePlayingSong()
    {
        // GitHub #110's Shuffle-off order is untouched by a reshuffle.
        var vm = CreateVm();
        var album = Album(20);
        vm.ReplaceQueueAndPlay(album, 0);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null); // Song 3 plays
        vm.ToggleShuffleCommand.Execute(null);
        vm.ReshuffleCommand.Execute(null);
        vm.ReshuffleCommand.Execute(null);

        vm.ToggleShuffleCommand.Execute(null); // off

        Assert.Same(album[2], vm.CurrentTrack);
        Assert.Equal(Ids(album.Skip(3)), Ids(vm.UpNext));
    }

    [Fact]
    public void Reshuffle_RepeatAllWrap_StillReplaysEverySong()
    {
        var vm = CreateVm();
        var album = Album(6);
        vm.ReplaceQueueAndPlay(album, 0);
        vm.RepeatMode = RepeatMode.All;
        vm.ToggleShuffleCommand.Execute(null);
        vm.ReshuffleCommand.Execute(null);

        // Play the pass out: the wrap starts a new pass holding all six songs.
        for (var i = 0; i < album.Count; i++) vm.NextCommand.Execute(null);
        var pass = vm.UpNext.Append(vm.CurrentTrack!).ToList();
        Assert.Equal(Ids(album).Order(), Ids(pass).Order());
    }
    // ── The Queue panel's header chip ──

    private static string RepoFile(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Noctis.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root!.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void QueuePanelHeader_ReshuffleChip_IsWiredToThePlayer_ShownWithShuffleOn()
    {
        // MainWindow's bindings need a MainWindowViewModel, which tests do not build: the
        // header's wiring is checked in the XAML, the chip itself in the mount below.
        var xaml = RepoFile("src", "Noctis", "Views", "MainWindow.axaml");
        var at = xaml.IndexOf("x:Name=\"QueueReshuffleButton\"", StringComparison.Ordinal);
        Assert.True(at > 0);
        var chip = xaml.Substring(at, xaml.IndexOf("</Button>", at, StringComparison.Ordinal) - at);
        Assert.Contains("Classes=\"queue-clear-played\"", chip);
        Assert.Contains("Command=\"{Binding Player.ReshuffleCommand}\"", chip);
        Assert.Contains("IsVisible=\"{Binding Player.ShowReshuffle}\"", chip);
        Assert.Contains("ToolTip.Tip=\"{loc:T Queue.Reshuffle}\"", chip);
        // Beside Clear Played, in the header.
        Assert.True(xaml.IndexOf("x:Name=\"QueueClearPlayedButton\"", StringComparison.Ordinal) - at is > 0 and < 2000);
    }

    [AvaloniaFact]
    public void QueuePanelHeader_ReshuffleChip_ReadsItsName_AndReshuffles()
    {
        AccessibleNames.Install();
        AddToPlaylistDialogTests.EnsureAppStyles();
        var vm = CreateVm();
        vm.KeepPlayedInQueue = true;
        vm.PlayShuffled(Enumerable.Range(1, 12).Select(n => Song(n)).ToList());
        vm.NextCommand.Execute(null);

        var main = new MainWindow();
        var drawer = main.FindControl<Border>("QueuePopupPanel")!;
        var list = main.FindControl<ListBox>("QueuePopupListBox")!;
        ((Panel)drawer.Parent!).Children.Remove(drawer);
        var host = new Window { Width = 400, Height = 640 };
        foreach (var style in main.Styles.ToList())
        {
            main.Styles.Remove(style);
            host.Styles.Add(style);
        }
        drawer.Transitions = null;
        drawer.IsVisible = true;
        drawer.Opacity = 1;
        drawer.RenderTransform = null;
        drawer.Height = 520;
        // No MainWindowViewModel here: stand in for the bindings the header would resolve.
        main.FindControl<StackPanel>("QueueEmptyHint")!.IsVisible = false;
        main.FindControl<StackPanel>("QueueNothingUpNext")!.IsVisible = false;
        list.ItemsSource = vm.QueueRows;
        list.ContainerPrepared += (_, e) => QueuePanelRows.Stamp(list, e.Container, e.Index, vm);
        var chip = main.FindControl<Button>("QueueReshuffleButton")!;
        var clearPlayed = main.FindControl<Button>("QueueClearPlayedButton")!;
        chip.Command = vm.ReshuffleCommand;
        chip.IsVisible = vm.IsShuffleEnabled;
        clearPlayed.IsVisible = true;
        clearPlayed.IsEnabled = vm.HasPlayedQueueRows;
        host.Content = new Panel { Background = new SolidColorBrush(Color.Parse("#252525")), Children = { drawer } };
        host.Show();
        try
        {
            for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); host.UpdateLayout(); }
            Assert.True(chip.IsEffectivelyVisible);
            Assert.True(chip.IsEffectivelyEnabled);
            Assert.Equal(Loc.T("Queue.Reshuffle"), ControlAutomationPeer.CreatePeerForElement(chip).GetName());
            // Left of Clear Played, on the same line, apart.
            var c = chip.TranslatePoint(default, drawer)!.Value;
            var p = clearPlayed.TranslatePoint(default, drawer)!.Value;
            Assert.True(c.X + chip.Bounds.Width <= p.X - 4, $"chip {c} {chip.Bounds.Size}, Clear Played {p}");
            Assert.InRange(c.Y + chip.Bounds.Height / 2 - (p.Y + clearPlayed.Bounds.Height / 2), -1, 1);

            var before = vm.UpNext.ToList();
            chip.Command!.Execute(null);
            Assert.NotEqual(before, vm.UpNext);

            var dir = Environment.GetEnvironmentVariable("NOCTIS_QUEUE_MINI_SHOTS_DIR");
            if (HeadlessTestApp.RealRendering && !string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                for (var i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
                host.CaptureRenderedFrame()!.Save(Path.Combine(dir, "queue-panel-header-reshuffle.png"));
            }
        }
        finally
        {
            host.Close();
            main.Close();
        }
    }
}
