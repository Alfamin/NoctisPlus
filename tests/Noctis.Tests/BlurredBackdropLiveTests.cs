using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-18: with Settings open over the lyrics page, the blurred background "skips/jumps
/// weirdly". The scrim was a frozen snapshot of the app, retaken (with a cross-fade) on every
/// Settings property change, tab switches and search keystrokes included: the moving lyrics
/// under it stood still, then jumped forward. Live mode blurs the app layer itself instead,
/// so it moves smoothly under the sheet and is never retaken; the snapshot remains the Liquid
/// Glass fallback, and only changes that can repaint the app retake it.
/// </summary>
public class BlurredBackdropLiveTests
{
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

    /// <summary>MainWindow's shape: the app layer (a dark page with a white "lyric line" that
    /// moves) and the live backdrop as its sibling over it.</summary>
    private static (Window win, Panel layer, TranslateTransform move, BlurredBackdrop backdrop) Build()
    {
        var line = new Border
        {
            Width = 120, Height = 40, Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(100, 280, 0, 0),
        };
        var move = new TranslateTransform();
        line.RenderTransform = move;
        var layer = new Panel { Background = Brushes.Black, Children = { line } };
        var backdrop = new BlurredBackdrop { IsVisible = false, Opacity = 0, Target = layer, LiveBlur = true };
        var win = new Window
        {
            Width = 800, Height = 600, RequestedThemeVariant = ThemeVariant.Dark,
            Background = Brushes.Black,
            Content = new Panel { Children = { layer, backdrop } },
        };
        win.Show();
        PumpUntil(() => false, 60);
        return (win, layer, move, backdrop);
    }

    private static void Open(BlurredBackdrop backdrop)
    {
        backdrop.IsVisible = true;
        var prepare = backdrop.PrepareAsync();
        Assert.True(PumpUntil(() => prepare.IsCompleted, 3000), "PrepareAsync never finished");
        backdrop.Opacity = 1;
        PumpUntil(() => false, 300); // the fade settles
    }

    private static void Close(BlurredBackdrop backdrop)
    {
        backdrop.Opacity = 0;
        backdrop.IsVisible = false;
        backdrop.Release();
    }

    private static double RadiusOn(Visual target) => Assert.IsAssignableFrom<IBlurEffect>(target.Effect).Radius;

    [AvaloniaFact]
    public void Live_BlursTheTargetInPlace_NeverSnapshots_AndClearsOnClose()
    {
        var (win, layer, _, backdrop) = Build();
        try
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                Assert.Null(layer.Effect);
                Open(backdrop);
                Assert.True(backdrop.IsLive);
                Assert.False(backdrop.HasSnapshot);
                Assert.Equal(0, backdrop.CaptureCount);
                Assert.Equal(BlurredBackdrop.LiveRadiusFor(BackdropSnapshot.BlurRadius), RadiusOn(layer), 3);
                Assert.Equal(0, backdrop.SnapshotImage.Opacity, 3);
                Assert.Equal(backdrop.BlurDimOpacity, backdrop.DimLayer.Opacity, 3);

                Close(backdrop);
                Assert.False(backdrop.IsLive);
                Assert.Null(layer.Effect);
                Assert.Equal(1, backdrop.DimLayer.Opacity, 3);
            }
        }
        finally { Close(backdrop); win.Close(); }
    }

    /// <summary>The blur fades with the layer: at half Opacity half the radius, at 0 no effect
    /// (no offscreen layer) at all, so the open and close fades carry it.</summary>
    [AvaloniaFact]
    public void Live_BlurFollowsTheLayersOpacity()
    {
        var (win, layer, _, backdrop) = Build();
        try
        {
            backdrop.Transitions = null;
            backdrop.IsVisible = true;
            var prepare = backdrop.PrepareAsync();
            Assert.True(PumpUntil(() => prepare.IsCompleted, 3000));
            Assert.True(backdrop.IsLive);
            Assert.Null(layer.Effect); // still at Opacity 0

            var full = BlurredBackdrop.LiveRadiusFor(BackdropSnapshot.BlurRadius);
            backdrop.Opacity = 0.5;
            Assert.Equal(full / 2, RadiusOn(layer), 3);
            backdrop.Opacity = 1;
            Assert.Equal(full, RadiusOn(layer), 3);
            backdrop.Opacity = 0;
            Assert.Null(layer.Effect);
        }
        finally { Close(backdrop); win.Close(); }
    }

    /// <summary>The bug itself: content moving under the sheet, and everything that used to
    /// retake the snapshot (a setting change, a theme switch, a resize), never retake in live
    /// mode; the same blur stays on the moving layer.</summary>
    [AvaloniaFact]
    public void Live_MovingContentAndRefreshRequests_NeverRetake()
    {
        var (win, layer, move, backdrop) = Build();
        try
        {
            Open(backdrop);
            var radius = RadiusOn(layer);
            for (var frame = 0; frame < 30; frame++)
            {
                move.Y -= 4; // the synced lyrics scrolling
                if (frame % 5 == 0) backdrop.RefreshSoon();
                if (frame == 10) backdrop.ScheduleRefresh();
                if (frame == 15) win.Width = 900;
                if (frame == 20) win.RequestedThemeVariant = ThemeVariant.Light;
                PumpUntil(() => false, 16);
            }
            PumpUntil(() => false, BlurredBackdrop.RefreshDelay.Milliseconds + 200);
            Assert.Equal(0, backdrop.CaptureCount);
            Assert.False(backdrop.HasSnapshot);
            Assert.False(backdrop.IsRefreshPending);
            Assert.Equal(radius, RadiusOn(layer), 3);
        }
        finally { Close(backdrop); win.Close(); }
    }

    [AvaloniaFact]
    public void Live_BackgroundBlurSlider_SetsTheRadius_OffIsThePlainDim()
    {
        var (win, layer, _, backdrop) = Build();
        try
        {
            Open(backdrop);
            BackdropSnapshot.BlurRadius = 5;
            Assert.Equal(BlurredBackdrop.LiveRadiusFor(5), RadiusOn(layer), 3);

            BackdropSnapshot.BlurRadius = 0; // Off
            Assert.Null(layer.Effect);
            Assert.True(PumpUntil(() => Math.Abs(backdrop.DimLayer.Opacity - 1) < 0.001, 2000), "dim never back to full");

            BackdropSnapshot.BlurRadius = 2;
            Assert.Equal(BlurredBackdrop.LiveRadiusFor(2), RadiusOn(layer), 3);
            Assert.True(PumpUntil(() => Math.Abs(backdrop.DimLayer.Opacity - backdrop.BlurDimOpacity) < 0.001, 2000));
            Assert.Equal(0, backdrop.CaptureCount);
        }
        finally
        {
            BackdropSnapshot.BlurRadius = BackdropSnapshot.DefaultBlurRadius;
            Close(backdrop);
            win.Close();
        }
    }

    /// <summary>Liquid Glass on: the snapshot as before (GlassPanels can't frost inside an
    /// effect layer). Switching it from the open sheet changes mode in place, both ways.</summary>
    [AvaloniaFact]
    public void Live_LiquidGlass_FallsBackToTheSnapshot_AndSwitchesInPlace()
    {
        var (win, layer, _, backdrop) = Build();
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            Open(backdrop);
            Assert.False(backdrop.IsLive);
            Assert.Null(layer.Effect);
            Assert.True(PumpUntil(() => backdrop.HasSnapshot, 3000), "no snapshot with glass on");

            AppGlass.Clear(); // glass off from the sheet
            Assert.True(backdrop.IsLive);
            Assert.False(backdrop.HasSnapshot);
            Assert.NotNull(layer.Effect);

            var captures = backdrop.CaptureCount;
            AppGlass.Set(true, Colors.Black, Colors.Black); // and on again
            Assert.False(backdrop.IsLive);
            Assert.Null(layer.Effect);
            Assert.True(PumpUntil(() => backdrop.HasSnapshot, 3000), "no snapshot after glass back on");
            Assert.Equal(captures + 1, backdrop.CaptureCount);
        }
        finally
        {
            AppGlass.Clear();
            Close(backdrop);
            win.Close();
        }
    }

    private static byte[] Frame(Window win, out int width)
    {
        PumpUntil(() => false, 20);
        using var frame = win.CaptureRenderedFrame()!;
        width = frame.PixelSize.Width;
        var px = new byte[width * frame.PixelSize.Height * 4];
        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(0, 0, width, frame.PixelSize.Height), handle.AddrOfPinnedObject(), px.Length, width * 4); }
        finally { handle.Free(); }
        return px;
    }

    /// <summary>Rendered: the moving line shows blurred where it is now on every frame (its
    /// brightness centre follows the move), not frozen where it was at the open.</summary>
    [AvaloniaFact]
    public void Live_Rendered_MovingContentStaysBlurredAndFollowsTheMove()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var (win, _, move, backdrop) = Build();
        try
        {
            Open(backdrop);
            double? previous = null;
            for (var step = 0; step < 4; step++)
            {
                var px = Frame(win, out var w);
                var h = px.Length / 4 / w;
                double sum = 0, weighted = 0;
                var peak = 0;
                for (var y = 0; y < h; y++)
                {
                    var g = px[(y * w + 160) * 4 + 1]; // a column through the line
                    sum += g;
                    weighted += g * (double)y;
                    peak = Math.Max(peak, g);
                }
                var centre = weighted / sum;
                // Blurred: the white line never reaches white under the blur and the dim.
                Assert.InRange(peak, 1, 200);
                if (previous is { } p)
                    Assert.InRange(previous.Value - centre, 30, 50); // moved up 40 px
                previous = centre;
                move.Y -= 40;
            }
        }
        finally { Close(backdrop); win.Close(); }
    }

    private static double EdgeWidth(Func<int, double> lum, int n)
    {
        double lo = -1, hi = -1;
        for (var x = 0; x < n; x++)
        {
            var v = lum(x);
            if (lo < 0 && v >= 0.1) lo = x;
            if (hi < 0 && v >= 0.9) hi = x;
        }
        return hi - lo;
    }

    /// <summary>LiveRadiusFor's constants: at the same Background Blur value the live blur
    /// spreads a sharp edge as far as the snapshot's does (10-90% width, within a quarter-res
    /// pixel step either side).</summary>
    [AvaloniaFact]
    public async Task LiveRadius_MatchesTheSnapshotsBlur()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        grid.Children.Add(new Border { Background = Brushes.Black });
        var white = new Border { Background = Brushes.White };
        Grid.SetColumn(white, 1);
        grid.Children.Add(white);
        var layer = new Panel { Children = { grid } };
        var win = new Window { Width = 1200, Height = 200, Background = Brushes.Black, Content = layer };
        win.Show();
        PumpUntil(() => false, 60);
        try
        {
            foreach (var r in new[] { 1.0, 3, 10 })
            {
                var source = await BackdropSnapshot.CaptureSourceAsync(layer, win, "test");
                Assert.NotNull(source);
                var px = new byte[source!.Width * source.Height * 4];
                source.BlurInto(px, r);
                var row = source.Height / 2;
                var snapshotWidth = EdgeWidth(x => px[(row * source.Width + x) * 4 + 1] / 255.0, source.Width)
                                    * 1200.0 / source.Width;

                layer.Effect = new ImmutableBlurEffect(BlurredBackdrop.LiveRadiusFor(r));
                var frame = Frame(win, out var w);
                var liveWidth = EdgeWidth(x => frame[(100 * w + x) * 4 + 1] / 255.0, w);
                layer.Effect = null;

                var step = 1 / BackdropSnapshot.SnapshotScaling;
                Assert.True(Math.Abs(liveWidth - snapshotWidth) <= 2 * step + 0.15 * snapshotWidth,
                    $"r={r}: live {liveWidth} px vs snapshot {snapshotWidth} px");
            }
        }
        finally { win.Close(); }
    }

    [Theory]
    [InlineData("SelectedSettingsTab")]
    [InlineData("SelectedTabTitle")]
    [InlineData("IsLyricsTabSelected")]
    [InlineData("IsAdvancedTabVisible")]
    [InlineData("SearchQuery")]
    [InlineData("ScanStatusText")]
    [InlineData("ExclusiveAudioStatus")]
    [InlineData("UpdateStatusIsError")]
    [InlineData("DownloadProgress")]
    [InlineData("DevLogText")]
    public void SheetOnlyChanges_DoNotRetake(string property) =>
        Assert.False(MainWindow.RepaintsAppUnderSettings(property));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LiquidGlassEnabled")]
    [InlineData("ActiveCustomThemeId")]
    [InlineData("AccentFollowsArtwork")]
    [InlineData("DiscordStatusDisplay")]
    public void ChangesThatRepaintTheApp_Retake(string? property) =>
        Assert.True(MainWindow.RepaintsAppUnderSettings(property));

    /// <summary>Every notification a real tab switch raises is the sheet's own (it raised a
    /// retake per click before).</summary>
    [AvaloniaFact]
    public void TabSwitch_RaisesNothingThatRetakes()
    {
        var vm = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new FakeHistoryLog());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        foreach (var tab in new[] { SettingsViewModel.TabAppearance, SettingsViewModel.TabLyrics, SettingsViewModel.TabAdvanced, SettingsViewModel.TabGeneral })
            vm.SelectedSettingsTab = tab;
        vm.SearchQuery = "blur";
        vm.SearchQuery = "";
        Assert.NotEmpty(raised);
        Assert.Empty(raised.Where(MainWindow.RepaintsAppUnderSettings));
    }
}
