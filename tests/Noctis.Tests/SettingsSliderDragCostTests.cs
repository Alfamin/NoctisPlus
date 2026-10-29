using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-18: Settings sliders (Glass Opacity, Background Blur, Track Box Opacity) glide when
/// moved slowly but stutter when dragged fast. A fast drag is ~60 value changes a second; each
/// one must stay cheap on the UI thread: no settings write per step, no app-wide marquee reset
/// per step, and no fresh snapshot of the whole app under the Settings sheet per step.
/// </summary>
public class SettingsSliderDragCostTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public SettingsSliderDragCostTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        BackdropSnapshot.BlurRadius = BackdropSnapshot.DefaultBlurRadius; // process-wide
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class CountingPersistence : TestPersistenceService
    {
        public int Saves;
        public override Task SaveSettingsAsync(AppSettings settings)
        {
            Interlocked.Increment(ref Saves);
            return Task.CompletedTask;
        }
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static async Task<(SettingsViewModel vm, CountingPersistence persistence, PlayerViewModel player)> LoadedSettings()
    {
        var persistence = new CountingPersistence();
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.SetPlayer(player);
        await vm.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        persistence.Saves = 0;
        return (vm, persistence, player);
    }

    /// <summary>The sliders on every Settings page, as (name, minimum, maximum, setter).</summary>
    private static IEnumerable<(string Name, double Min, double Max, Action<SettingsViewModel, double> Set)> Sliders()
    {
        yield return ("PlaybackBarBackgroundOpacity", 0, 1, (vm, v) => vm.PlaybackBarBackgroundOpacity = v);
        yield return ("BackgroundBlurAmount", 0, 1, (vm, v) => vm.BackgroundBlurAmount = v);
        yield return ("PlaybackBarTrackBoxOpacity", 0, 1, (vm, v) => vm.PlaybackBarTrackBoxOpacity = v);
        yield return ("MiniPlayerBackgroundOpacity", 0, 1, (vm, v) => vm.MiniPlayerBackgroundOpacity = v);
        yield return ("AlbumPageTintStrength", 0, 100, (vm, v) => vm.AlbumPageTintStrength = (int)Math.Round(v));
        yield return ("AlbumTileTargetSize", 140, 320, (vm, v) => vm.AlbumTileTargetSize = v);
        yield return ("PlaybackBarIslandWidth", 340, 1400, (vm, v) => vm.PlaybackBarIslandWidth = v);
        yield return ("LyricsDriftMovement", 0, 300, (vm, v) => vm.LyricsDriftMovement = (int)Math.Round(v));
        yield return ("LyricsKawarpWarp", 0, 3, (vm, v) => vm.LyricsKawarpWarp = Math.Round(v, 1));
        yield return ("LyricsKawarpBlur", 1, 16, (vm, v) => vm.LyricsKawarpBlur = (int)Math.Round(v));
        yield return ("LyricsMinLineOpacity", 0, 60, (vm, v) => vm.LyricsMinLineOpacity = (int)Math.Round(v));
        yield return ("CrossfadeDuration", 1, 12, (vm, v) => vm.CrossfadeDuration = v);
        yield return ("PlayPauseFadeMs", 100, 2000, (vm, v) => vm.PlayPauseFadeMs = Math.Round(v / 50) * 50);
        yield return ("ReplayGainPreampDb", -12, 12, (vm, v) => vm.ReplayGainPreampDb = v);
    }

    /// <summary>A fast drag: 60 samples sweeping the whole range, edge to edge.</summary>
    private static IEnumerable<double> FastDrag(double min, double max)
    {
        for (var i = 0; i < 60; i++)
        {
            var t = i / 59.0;
            yield return min + (max - min) * (i % 2 == 0 ? t : 1 - t * 0.5);
        }
    }

    /// <summary>No slider writes the settings file per drag sample: the write is debounced to
    /// the drag's trailing edge (one save), and the value that lands is the last one.</summary>
    [AvaloniaFact]
    public async Task FastDrag_OnEverySettingsSlider_SavesOnTheTrailingEdgeOnly()
    {
        var failures = new List<string>();
        foreach (var (name, min, max, set) in Sliders())
        {
            var (vm, persistence, _) = await LoadedSettings();
            var sw = Stopwatch.StartNew();
            var worst = 0.0;
            foreach (var v in FastDrag(min, max))
            {
                var step = Stopwatch.StartNew();
                set(vm, v);
                Dispatcher.UIThread.RunJobs();
                worst = Math.Max(worst, step.Elapsed.TotalMilliseconds);
            }
            var during = persistence.Saves;
            _out.WriteLine($"{name}: {sw.Elapsed.TotalMilliseconds:F1} ms for 60 samples, worst {worst:F2} ms, {during} saves during the drag");
            if (during > 0) failures.Add($"{name}: {during} saves during the drag");

            // The trailing edge persists it.
            var saved = PumpUntil(() => persistence.Saves > 0, 2000);
            if (!saved) failures.Add($"{name}: never saved after the drag");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static T Field<T>(MarqueeTextBlock m, string name) =>
        (T)typeof(MarqueeTextBlock).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(m)!;

    private static void SetField(MarqueeTextBlock m, string name, object value) =>
        typeof(MarqueeTextBlock).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(m, value);

    /// <summary>Glass Opacity / Track Box Opacity / Minimum Line Opacity pushed the whole player
    /// settings block per sample, which re-raised MarqueeTextBlock's global switch: every
    /// scrolling title in the app snapped back to its start and restarted its 7 s rest on each
    /// step of the drag. A slider sample now leaves a running marquee where it is.</summary>
    [AvaloniaFact]
    public async Task FastDrag_OnAppearanceSliders_DoesNotResetScrollingMarquees()
    {
        var marquee = new MarqueeTextBlock { Text = "I Forgot That You Exist (Taylor's Version) — a long title", FontSize = 14, MaxDisplayWidth = 120 };
        var win = new Window { Width = 200, Height = 80, Content = new Border { Width = 120, Height = 40, Child = marquee } };
        win.Show();
        try
        {
            PumpUntil(() => false, 60);
            var (vm, _, _) = await LoadedSettings();
            var drags = new (string Name, Action<double> Set)[]
            {
                ("PlaybackBarBackgroundOpacity", v => vm.PlaybackBarBackgroundOpacity = v),
                ("PlaybackBarTrackBoxOpacity", v => vm.PlaybackBarTrackBoxOpacity = v),
                ("LyricsMinLineOpacity", v => vm.LyricsMinLineOpacity = (int)Math.Round(v * 60)),
            };
            foreach (var (name, set) in drags)
            {
                SetField(marquee, "_offset", -37.5);
                var generation = Field<int>(marquee, "_lapGeneration");
                foreach (var v in FastDrag(0, 1)) set(v);
                Dispatcher.UIThread.RunJobs();
                Assert.True(Field<double>(marquee, "_offset") == -37.5, $"{name}: the marquee snapped back to its start");
                Assert.True(generation == Field<int>(marquee, "_lapGeneration"), $"{name}: the marquee restarted its rest");
            }
        }
        finally { win.Close(); }
    }

    /// <summary>The marquee switches are still re-applied when one of them really changes.</summary>
    [AvaloniaFact]
    public async Task MarqueeToggle_StillReachesAttachedMarquees()
    {
        var marquee = new MarqueeTextBlock { Text = "I Forgot That You Exist (Taylor's Version) — a long title", FontSize = 14, MaxDisplayWidth = 120, IsCoverFlow = true };
        var win = new Window { Width = 200, Height = 80, Content = new Border { Width = 120, Height = 40, Child = marquee } };
        win.Show();
        var saved = MarqueeTextBlock.GlobalCoverFlowScrollEnabled;
        try
        {
            PumpUntil(() => false, 60);
            var (vm, _, _) = await LoadedSettings();
            SetField(marquee, "_offset", -37.5);
            vm.CoverFlowMarqueeEnabled = !vm.CoverFlowMarqueeEnabled;
            Assert.Equal(0, Field<double>(marquee, "_offset"));
        }
        finally
        {
            MarqueeTextBlock.GlobalCoverFlowScrollEnabled = saved;
            win.Close();
        }
    }
    /// <summary>Every Settings change retakes the blurred snapshot of the app under the sheet
    /// (MainWindow's handler), and a fast drag is a change per pointer move: the scrim retook
    /// the whole app back to back, 57 times for a one-second drag with the headless stubs (30
    /// with real Skia, 7 s of UI-thread work). A stream of requests retakes once at its start
    /// and once when it goes quiet, which catches the final look.</summary>
    [AvaloniaFact]
    public void FastDrag_RetakesTheSheetBackdropOnceAtTheStartAndOnceWhenItSettles()
    {
        var layer = new Panel { Children = { new Border { Background = Brushes.SteelBlue } } };
        var backdrop = new BlurredBackdrop { IsVisible = false, Opacity = 0, Target = layer };
        var win = new Window
        {
            Width = 800, Height = 600, RequestedThemeVariant = ThemeVariant.Dark, Background = Brushes.Black,
            Content = new Panel { Children = { layer, backdrop } },
        };
        win.Show();
        try
        {
            PumpUntil(() => false, 60);
            backdrop.IsVisible = true;
            var prepare = backdrop.PrepareAsync();
            Assert.True(PumpUntil(() => prepare.IsCompleted && backdrop.HasSnapshot, 3000), "no snapshot");
            backdrop.Opacity = 1;
            PumpUntil(() => false, 400);
            var before = backdrop.CaptureCount;

            for (var i = 0; i < 60; i++)
            {
                backdrop.RefreshSoon();
                var frame = Stopwatch.StartNew();
                while (frame.ElapsedMilliseconds < 16)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Thread.Sleep(1);
                }
            }
            var during = backdrop.CaptureCount - before;
            _out.WriteLine($"{during} retakes during the drag");
            Assert.InRange(during, 0, 1);

            Assert.True(PumpUntil(() => backdrop.CaptureCount - before == during + 1 && !backdrop.IsRefreshPending,
                (int)BlurredBackdrop.RefreshDelay.TotalMilliseconds + 2000), "the settled look was never retaken");
            PumpUntil(() => false, 400);
            Assert.Equal(during + 1, backdrop.CaptureCount - before);
        }
        finally
        {
            backdrop.Release();
            win.Close();
        }
    }

    /// <summary>Background Blur re-blurs the snapshot the scrim already holds; it repaints
    /// nothing under the sheet, so it must not ask for a retake as well.</summary>
    [Fact]
    public void BackgroundBlur_DoesNotRetakeTheSheetBackdrop_OtherSettingsDo()
    {
        Assert.False(MainWindow.SettingChangeRepaintsUnderSheet(nameof(SettingsViewModel.BackgroundBlurAmount)));
        Assert.False(MainWindow.SettingChangeRepaintsUnderSheet(nameof(SettingsViewModel.BackgroundBlurLabel)));
        Assert.False(MainWindow.SettingChangeRepaintsUnderSheet("ScanProgress"));
        Assert.True(MainWindow.SettingChangeRepaintsUnderSheet(nameof(SettingsViewModel.PlaybackBarBackgroundOpacity)));
        Assert.True(MainWindow.SettingChangeRepaintsUnderSheet(nameof(SettingsViewModel.PlaybackBarTrackBoxOpacity)));
        Assert.True(MainWindow.SettingChangeRepaintsUnderSheet(null));
    }
}
