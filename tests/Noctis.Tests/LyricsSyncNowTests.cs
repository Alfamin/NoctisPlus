using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Noctis Plus "Sync now" (owner, 2026-10-17): after a manual scroll the lyrics page keeps
/// following if the current line is still on screen (no Follow pill); only when the current
/// line is off screen does the pill appear, and it stays (no timed snap-back) until the user
/// taps it or brings the line back into view.
/// </summary>
public class LyricsSyncNowTests
{
    [AvaloniaFact]
    public void ScrollAway_ShowsThePillAndKeepsIt_ScrollBackFollowsAgain_SmallScrollNeverShowsIt()
    {
        var library = new FakeLibraryService(); var persistence = new TestPersistenceService();
        using var vm = new LyricsViewModel(new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService()),
            new NoLyrics(), new NoNetEase(), new MetadataService(), persistence, library);
        var lines = Enumerable.Range(0, 60).Select(i => new LyricLine
        {
            Text = $"Line number {i}", Timestamp = TimeSpan.FromSeconds(i * 3), EndTimestamp = TimeSpan.FromSeconds(i * 3 + 3),
        }).ToList();
        vm.LyricLines.ReplaceAll(lines);
        vm.ActiveLineIndex = 5;
        var page = new LyricsView { DataContext = vm };
        var window = new Window { Content = page, Width = 1400, Height = 850 };
        try
        {
            window.Show(); Pump(300);
            var scroller = page.FindControl<ScrollViewer>("LyricsScrollViewer")!;
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height * 2, "the test needs a scrollable page");
            var home = scroller.Offset.Y;

            // A nudge that leaves the current line in view: following carries on, no pill.
            scroller.Offset = new Vector(0, home + 30); Settle(page);
            Assert.False(vm.IsAutoFollowPaused);

            // Far away: the pill appears once the scroll settles...
            scroller.Offset = new Vector(0, home + 2500); Settle(page);
            Assert.True(vm.IsAutoFollowPaused, $"sync={vm.IsSyncTabSelected} active={vm.ActiveLineIndex} offset={scroller.Offset.Y} home={home} extent={scroller.Extent.Height} viewport={scroller.Viewport.Height}");
            // ...and stays: no timer is left to drag the view back, and the next line keeps it parked.
            Assert.Null(TimerOf(page));
            vm.ActiveLineIndex = 6; Pump(100);
            Assert.True(vm.IsAutoFollowPaused);
            Assert.Equal(home + 2500, scroller.Offset.Y, 1);

            // Back to the current line: following again, pill gone.
            scroller.Offset = new Vector(0, home + 40); Settle(page);
            Assert.False(vm.IsAutoFollowPaused);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SidePanel_SameRule_PillOnlyWhileTheCurrentLineIsOutOfView()
    {
        var library = new FakeLibraryService(); var persistence = new TestPersistenceService();
        using var vm = new LyricsViewModel(new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService()),
            new NoLyrics(), new NoNetEase(), new MetadataService(), persistence, library);
        vm.LyricLines.ReplaceAll(Enumerable.Range(0, 60).Select(i => new LyricLine
        {
            Text = $"Line number {i}", Timestamp = TimeSpan.FromSeconds(i * 3), EndTimestamp = TimeSpan.FromSeconds(i * 3 + 3),
        }).ToList());
        vm.ActiveLineIndex = 5;
        var panel = new LyricsPanelView { DataContext = vm };
        var window = new Window { Content = panel, Width = 420, Height = 800 };
        try
        {
            window.Show(); Pump(300);
            var scroller = panel.FindControl<ScrollViewer>("PanelScrollViewer")!;
            var pill = panel.FindControl<Button>("PanelFollowButton")!;
            var home = scroller.Offset.Y;
            void Wheel() => typeof(LyricsPanelView).GetMethod("OnUserScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [null, null]);
            void SettlePanel() => typeof(LyricsPanelView).GetMethod("SettleUserScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);

            Wheel(); scroller.Offset = new Vector(0, home + 20); Pump(30); SettlePanel(); Pump(30);
            Assert.False(pill.IsVisible);
            Wheel(); scroller.Offset = new Vector(0, home + 2500); Pump(30); SettlePanel(); Pump(30);
            Assert.True(pill.IsVisible);
            Wheel(); scroller.Offset = new Vector(0, home + 10); Pump(30); SettlePanel(); Pump(30);
            Assert.False(pill.IsVisible);
        }
        finally { window.Close(); }
    }

    // Headless tests do not run DispatcherTimers: check one is armed, then run its tick.
    private static void Settle(LyricsView page)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(TimerOf(page));
        typeof(LyricsView).GetMethod("SettleUserScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
        Pump(50);
    }

    private static DispatcherTimer? TimerOf(LyricsView page) => (DispatcherTimer?)typeof(LyricsView)
        .GetField("_autoFollowResumeTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);

    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(15); } while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class NoLyrics : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string title, double seconds, CancellationToken ct = default) => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string title, CancellationToken ct = default) => Task.FromResult(new List<LrcLibResult>());
    }
    private sealed class NoNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string title, double seconds, CancellationToken ct = default) => Task.FromResult<LrcLibResult?>(null);
    }
}
