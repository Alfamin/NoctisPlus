using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Features.TrueShuffle;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class NativeTrueShuffleTests
{
    private sealed class RestorePersistence : TestPersistenceService, IPersistenceService
    {
        public QueueState? Queue;
        Task<QueueState?> IPersistenceService.LoadQueueStateAsync() => Task.FromResult(Queue);
    }

    [AvaloniaFact]
    public void ForYouShortcut_UsesNativeModeAndClearsItsAccentWhenModeChanges()
    {
        using var rig = new Rig(); rig.Start();
        var changes = new List<string?>();
        rig.Player.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Pump(rig.Player.SelectShuffleModeCommand.ExecuteAsync(PlayerShuffleMode.ForYou));
        Assert.True(rig.Player.IsForYouShuffle);
        Assert.True(rig.Player.IsShuffleLit);
        Assert.Contains(nameof(PlayerViewModel.IsForYouShuffle), changes);
        rig.Choose(PlayerShuffleMode.NoRepeats);
        Assert.False(rig.Player.IsForYouShuffle);
        Assert.True(rig.Player.IsShuffleLit);
    }

    private sealed class Rig : IDisposable
    {
        public readonly FakeLibraryService Library = new();
        public readonly FakeAudioPlayer Audio = new();
        public readonly RestorePersistence Data = new();
        public readonly List<Track> Tracks;
        public readonly PlayerViewModel Player;
        public readonly TrueShuffleService Service;
        public Rig(int count = 12)
        {
            Tracks = Enumerable.Range(1, count).Select(i =>
            {
                var path = Path.Combine(Data.DataDirectory, i + ".wav"); File.WriteAllText(path, "synthetic fake-audio fixture");
                return new Track { Title = "Song " + i, Artist = "Synthetic", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
            }).ToList();
            Library.TrackList.AddRange(Tracks);
            Player = new(Audio, Library, Data, new FakeAnimatedCoverService());
            Service = new(new ShuffleQueue(Player, Library, () => Player.AllowExplicitContent, () => null), Data.DataDirectory)
            { WindowSize = 5, RefillAt = 2 };
            Service.Start(null, Path.Combine(Data.DataDirectory, "plugin-data", "dev.moshi.trueshuffle"));
        }
        public void Start() { Player.ReplaceQueueAndPlay(Tracks.ToList(), 0); Dispatcher.UIThread.RunJobs(); }
        public void Choose(PlayerShuffleMode mode, ShuffleScope scope = ShuffleScope.WholeLibrary) { Pump(Service.SelectAsync(mode, scope)); Settle(); }
        public void Settle() { Dispatcher.UIThread.RunJobs(); Pump(Service.Pending); Dispatcher.UIThread.RunJobs(); }
        public void Next() { Player.NextCommand.Execute(null); Settle(); }
        public void Dispose() { Service.Dispose(); Player.StopAndClear("test complete"); Data.Dispose(); }
    }

    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(12)) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Assert.True(task.IsCompleted, "Shuffle operation did not finish"); task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void RestoredSong_Play_ThenChooseMode_WorksImmediately_WithoutRestartingAudio()
    {
        using var rig = new Rig();
        rig.Data.Queue = new() { CurrentTrackId = rig.Tracks[0].Id, PositionSeconds = 20 };
        Pump(rig.Player.RestoreQueueStateAsync());
        Assert.Same(rig.Tracks[0], rig.Player.CurrentTrack);
        rig.Player.PlayPauseCommand.Execute(null); Dispatcher.UIThread.RunJobs();
        var started = rig.Audio.PlayedPaths.Count;
        Pump(rig.Service.SelectAsync(PlayerShuffleMode.NoRepeats)); // default Current queue, with only restored current
        Assert.Equal(PlayerShuffleMode.NoRepeats, rig.Player.ShuffleMode);
        Assert.True(rig.Player.IsShuffleLit);
        Assert.NotEmpty(rig.Player.UpNext);
        Assert.Same(rig.Tracks[0], rig.Player.CurrentTrack);
        Assert.Equal(started, rig.Audio.PlayedPaths.Count);
        Assert.Empty(rig.Player.ShuffleStatus);
    }

    [AvaloniaFact]
    public void RapidModeClicks_CancelEarlierPreparation_LatestClickWins_OffCancelsToo()
    {
        using var rig = new Rig(); rig.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Service.BeforePrepare = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var first = rig.Service.SelectAsync(PlayerShuffleMode.NoRepeats, ShuffleScope.WholeLibrary);
        Pump(entered.Task);
        Assert.True(rig.Player.IsShuffleBusy);
        Assert.Equal(PlayerShuffleMode.NoRepeats, rig.Player.ShuffleMode);
        rig.Service.BeforePrepare = null;
        Pump(rig.Service.SelectAsync(PlayerShuffleMode.Discovery, ShuffleScope.WholeLibrary)); Pump(first);
        Assert.Equal(PlayerShuffleMode.Discovery, rig.Player.ShuffleMode);
        Assert.False(rig.Player.IsShuffleBusy);
        Assert.True(rig.Player.IsShuffleLit);
        rig.Service.BeforePrepare = ct => Task.Delay(Timeout.Infinite, ct);
        var pending = rig.Service.SelectAsync(PlayerShuffleMode.ForYou, ShuffleScope.WholeLibrary);
        Pump(rig.Service.SelectAsync(PlayerShuffleMode.Off)); Pump(pending);
        Assert.False(rig.Player.IsShuffleLit); Assert.False(rig.Player.IsShuffleBusy);
    }

    [AvaloniaFact]
    public void PlayCountAndTagSaveDuringPreparation_DoesNotRefuseTheMode()
    {
        using var rig = new Rig(); rig.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Service.BeforePrepare = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var apply = rig.Service.SelectAsync(PlayerShuffleMode.Discovery, ShuffleScope.WholeLibrary); Pump(entered.Task);
        rig.Tracks[0].PlayCount++; rig.Tracks[1].Rating = 5; rig.Tracks[2].Title += " edited";
        release.TrySetResult(); Pump(apply);
        Assert.Equal(PlayerShuffleMode.Discovery, rig.Player.ShuffleMode);
        Assert.Empty(rig.Player.ShuffleStatus);
        Assert.NotEmpty(rig.Player.UpNext);
    }

    [AvaloniaFact]
    public void EveryMode_LightsThePlayer_AndNativeMenuShowsExactlyOneCheckmark()
    {
        using var rig = new Rig(60); rig.Start();
        var menu = new ShuffleMenu { Player = rig.Player };
        var button = new Button { Content = "Shuffle", Flyout = menu };
        var window = new Window { Content = button }; window.Show();
        try
        {
            foreach (var mode in Enum.GetValues<PlayerShuffleMode>().Where(m => m != PlayerShuffleMode.Off))
            {
                rig.Choose(mode);
                Assert.Equal(mode, rig.Player.ShuffleMode);
                Assert.True(rig.Player.IsShuffleLit);
                menu.ShowAt(button); Dispatcher.UIThread.RunJobs();
                var checkedItems = menu.Items.OfType<MenuItem>().Where(i => i.Icon is LineIcon { Opacity: 1 }).ToList();
                Assert.Single(checkedItems);
                menu.Hide();
            }
            rig.Choose(PlayerShuffleMode.Off); Assert.False(rig.Player.IsShuffleLit);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NativeFlyouts_BindDirectlyToTheirOwningPlayer()
    {
        using var rig = new Rig(); rig.Start(); rig.Player.IslandShowShuffle = true;
        var bar = new Noctis.Views.PlaybackBarView { DataContext = rig.Player };
        var queue = new Noctis.Views.QueueView { DataContext = new QueueViewModel(rig.Player) };
        var window = new Window { Content = new StackPanel { Children = { bar, queue } }, Width = 1000, Height = 700 };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var button = bar.FindControl<Button>("NativeShuffleButton")!;
            var menu = Assert.IsType<ShuffleMenu>(button.Flyout);
            Assert.Same(rig.Player, menu.Player);
            var queueMenu = Assert.IsType<ShuffleMenu>(queue.FindControl<Button>("NativeQueueShuffle")!.Flyout);
            Assert.Same(rig.Player, queueMenu.Player);
            menu.ShowAt(button); Dispatcher.UIThread.RunJobs();
            Assert.All(menu.Items.OfType<MenuItem>().Take(7), i => Assert.True(i.IsEnabled));
            menu.Hide();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RollingNoRepeats_ContinuesAcrossCycles_WithoutDuplicates_AndManualSongsStayAhead()
    {
        using var rig = new Rig(); rig.Start(); rig.Choose(PlayerShuffleMode.NoRepeats);
        var heard = new List<Guid> { rig.Player.CurrentTrack!.Id };
        for (var i = 0; i < 36; i++)
        {
            Assert.NotEmpty(rig.Player.UpNext);
            Assert.Equal(rig.Player.UpNext.Count, rig.Player.UpNext.Select(t => t.Id).Distinct().Count());
            Assert.DoesNotContain(rig.Player.UpNext, t => t.Id == rig.Player.CurrentTrack?.Id);
            rig.Next(); heard.Add(rig.Player.CurrentTrack!.Id);
        }
        Assert.Equal(12, heard.Take(12).Distinct().Count());
        var a = rig.Tracks[4]; var b = rig.Tracks[5];
        rig.Player.AddToQueue(a); rig.Player.AddToQueue(b);
        Assert.Equal(new[] { a.Id, b.Id }, rig.Player.UpNext.Take(2).Select(t => t.Id));
        Assert.Equal(1, rig.Player.UpNext.Count(t => t.Id == a.Id));
        rig.Player.ClearQueue(); rig.Settle();
        Assert.Empty(rig.Player.UpNext); Assert.False(rig.Player.IsShuffleLit);
    }

    [AvaloniaFact]
    public void PlayingAnotherList_SwitchesContext_AndPlayingASingleSongKeepsShuffle()
    {
        using var rig = new Rig(30); rig.Start(); rig.Choose(PlayerShuffleMode.NoRepeats);
        var requested = rig.Tracks[25]; rig.Player.AddToQueue(requested);
        var list = rig.Tracks.Skip(10).Take(8).ToList();
        rig.Player.ReplaceQueueAndPlay(list, 4); rig.Settle();
        Assert.Same(list[4], rig.Player.CurrentTrack);
        Assert.Equal(requested.Id, rig.Player.UpNext[0].Id);
        Assert.All(rig.Player.UpNext.Skip(1), t => Assert.Contains(t, list));
        Assert.Equal(PlayerShuffleMode.NoRepeats, rig.Player.ShuffleMode);
        var before = rig.Player.UpNext.ToList();
        rig.Player.ReplaceQueueAndPlay([rig.Tracks[28]], 0); rig.Settle();
        Assert.Equal(before.Select(t => t.Id), rig.Player.UpNext.Take(before.Count).Select(t => t.Id));
        Assert.True(rig.Player.IsShuffleLit);
    }

    [AvaloniaFact]
    public void RemovedLibraryTracks_AndNewEligibilityAreRespectedOnRefill()
    {
        using var rig = new Rig(20); rig.Start(); rig.Choose(PlayerShuffleMode.NeverPlayed);
        var notQueued = rig.Tracks.Where(t => t != rig.Player.CurrentTrack && !rig.Player.UpNext.Contains(t)).ToList();
        var missing = notQueued[0]; var nowPlayed = notQueued[1];
        rig.Library.TrackList.Remove(missing); nowPlayed.PlayCount = 1;
        for (var i = 0; i < 25 && rig.Player.UpNext.Count > 0; i++)
        {
            Assert.DoesNotContain(missing, rig.Player.UpNext); Assert.DoesNotContain(nowPlayed, rig.Player.UpNext);
            rig.Next();
        }
        Assert.Empty(rig.Player.UpNext);
        Assert.Contains("never-played", rig.Player.ShuffleStatus);
    }

    [AvaloniaFact]
    public void ChangingNormalPlaybackControls_EndsCustomShuffle_WithoutOverwritingTheChoice()
    {
        using var rig = new Rig(); rig.Start(); rig.Player.RepeatMode = RepeatMode.All; rig.Player.AutoplayEnabled = true;
        rig.Choose(PlayerShuffleMode.NoRepeats);
        Assert.Equal(RepeatMode.Off, rig.Player.RepeatMode); Assert.False(rig.Player.AutoplayEnabled);
        rig.Player.RepeatMode = RepeatMode.One;
        Assert.Equal(RepeatMode.One, rig.Player.RepeatMode); Assert.True(rig.Player.AutoplayEnabled);
        Assert.False(rig.Player.IsShuffleLit); Assert.Null(rig.Player.ShuffleGenerated);
    }

    [AvaloniaFact]
    public void FirstAndWarmActivation_Timings_AreRecordedOnTheActualNativePlayer()
    {
        using var rig = new Rig(2134); rig.Start(); rig.Service.WindowSize = 200;
        var report = new List<string>(); rig.Service.Timing = report.Add;
        foreach (var mode in new[] { PlayerShuffleMode.NoRepeats, PlayerShuffleMode.Discovery, PlayerShuffleMode.ForYou })
        {
            var watch = Stopwatch.StartNew(); rig.Choose(mode);
            report.Add($"{mode} TOTAL {watch.Elapsed.TotalMilliseconds:F2} ms, upcoming={rig.Player.UpNext.Count}");
        }
        var path = Environment.GetEnvironmentVariable("NOCTIS_SHUFFLE_TIMING_OUTPUT");
        if (!string.IsNullOrWhiteSpace(path)) File.WriteAllLines(path, report);
        Assert.Equal(PlayerShuffleMode.ForYou, rig.Player.ShuffleMode);
        Assert.InRange(rig.Player.UpNext.Count, 1, 25);
    }
}
