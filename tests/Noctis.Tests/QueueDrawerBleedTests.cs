using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Queue drawer with Liquid Glass off (10-17, Gray theme): nothing frosts under it then, so a
/// translucent QueueDrawerBackground let the Home page show through sharp — the pink Resume /
/// Play album buttons and the highlighted Last Played row read between the queue rows. The
/// drawer's own fill is opaque in every theme, and a pack's drawer colour is laid on opaque
/// (with glass on only its RGB tints the frost, at Glass Opacity, so the alpha meant nothing
/// there).
/// </summary>
public class QueueDrawerBleedTests
{
    public static IEnumerable<object[]> AllThemes() =>
        new[] { "Gray", "Light", "Ink", "Smoke" }.Select(t => new object[] { t });

    private static ResourceDictionary ThemeDictionary(string theme)
    {
        if (theme is "Ink" or "Smoke") return ThemeOverlayParityTests.LoadOverlay(theme);
        ThemeOverlayParityTests.BaseDarkKeys(); // loads the app font / icons Styles.axaml needs
        var styles = Assert.IsType<Styles>(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml"),
        }.Loaded);
        var variant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        return Assert.IsType<ResourceDictionary>(styles.Resources.ThemeDictionaries[variant]);
    }

    [AvaloniaTheory]
    [MemberData(nameof(AllThemes))]
    public void DrawerFill_IsOpaque_InEveryTheme(string theme)
    {
        Assert.True(ThemeDictionary(theme).TryGetValue("QueueDrawerBackground", out var v));
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(v);
        Assert.Equal(255, brush.Color.A);
        Assert.Equal(1.0, brush.Opacity);
    }

    [Fact]
    public void PackDrawerColour_IsLaidOnOpaque_KeepingItsRgb()
    {
        var theme = new PackTheme("p", "P", "t", "T", false, null, new Dictionary<string, Color>
        {
            ["QueueDrawerBackground"] = Color.Parse("#F21F1929"),
            ["IslandBackground"] = Color.Parse("#801F1929"),
        });
        var r = PackThemeBuilder.Build(theme);
        Assert.Equal(Color.Parse("#FF1F1929"), ((ISolidColorBrush)r["QueueDrawerBackground"]).Color);
        // Other translucent pack colours are left as the pack wrote them.
        Assert.Equal(Color.Parse("#801F1929"), ((ISolidColorBrush)r["IslandBackground"]).Color);
    }

    // ── Real Skia: the page under the drawer must not change a single drawer pixel ──

    private static Track Trk(int i) => new()
    {
        Id = Guid.NewGuid(), Title = "Die With A Smile " + i, Artist = "Lady Gaga, Bruno Mars",
        FilePath = TestPaths.Primary("t", $"q{i}.mp3"), Duration = TimeSpan.FromMinutes(4),
    };

    /// <summary>A Home-like page: the pink Continue Listening buttons and the pink
    /// highlighted Last Played row with its artist / plays text, under the drawer's spot.</summary>
    private static Control BusyPage()
    {
        var pink = new SolidColorBrush(Color.Parse("#FF7EAC"));
        var page = new StackPanel { Spacing = 10, Margin = new Thickness(0, 60, 0, 0) };
        for (var i = 0; i < 9; i++)
        {
            var row = new Border
            {
                Height = 44,
                Background = i % 3 == 0 ? pink : new SolidColorBrush(Color.Parse("#3A3A3A")),
                Child = new TextBlock
                {
                    Text = "Lady Gaga, Bruno Mars        4:11        1 play   Resume   Play album",
                    Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40, 0, 0, 0),
                },
            };
            page.Children.Add(row);
        }
        return page;
    }

    [AvaloniaTheory]
    [MemberData(nameof(AllThemes))]
    public void PageContent_DoesNotShowThroughTheDrawer_GlassOff(string theme)
    {
        if (!HeadlessTestApp.RealRendering) Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        AddToPlaylistDialogTests.EnsureAppStyles();

        var vm = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.ReplaceQueueAndPlay(Enumerable.Range(0, 12).Select(Trk).ToArray(), 0);

        var main = new MainWindow();
        var drawer = main.FindControl<Border>("QueuePopupPanel")!;
        var list = main.FindControl<ListBox>("QueuePopupListBox")!;
        ((Panel)drawer.Parent!).Children.Remove(drawer);
        // The drawer's fill is a MainWindow style (Liquid Glass clears it): bring it along.
        var host = new Window { Width = 720, Height = 640 };
        foreach (var s in main.Styles.ToList())
        {
            main.Styles.Remove(s);
            host.Styles.Add(s);
        }
        if (theme is "Ink" or "Smoke") host.Resources.MergedDictionaries.Add(ThemeOverlayParityTests.LoadOverlay(theme));
        host.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;

        drawer.Transitions = null;
        drawer.IsVisible = true;
        drawer.Opacity = 1;
        drawer.RenderTransform = null;
        // No MainWindowViewModel here: the empty-state hints' bindings never resolve.
        main.FindControl<StackPanel>("QueueEmptyHint")!.IsVisible = false;
        main.FindControl<StackPanel>("QueueNothingUpNext")!.IsVisible = false;
        list.ItemsSource = vm.QueueRows;
        list.ContainerPrepared += (_, e) => QueuePanelRows.Stamp(list, e.Container, e.Index, vm);

        var page = BusyPage();
        host.Content = new Panel
        {
            Background = theme == "Light" ? Brushes.White : new SolidColorBrush(Color.Parse("#252525")),
            Children = { page, drawer },
        };
        host.Show();
        try
        {
            WriteableBitmap Shot()
            {
                for (var i = 0; i < 6; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }
                using var frame = host.CaptureRenderedFrame()!;
                using var ms = new MemoryStream();
                frame.Save(ms);
                ms.Position = 0;
                return WriteableBitmap.Decode(ms);
            }

            using var busy = Shot();
            page.IsVisible = false;
            using var plain = Shot();

            var dir = Environment.GetEnvironmentVariable("NOCTIS_QUEUE_BLEED_SHOTS");
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                page.IsVisible = true;
                using var save = host.CaptureRenderedFrame()!;
                save.Save(Path.Combine(dir, $"queue-drawer-{theme.ToLowerInvariant()}.png"));
            }

            // Inside the drawer, clear of its rounded corners: identical with and without the page.
            var b = drawer.Bounds;
            var scale = busy.PixelSize.Width / host.Bounds.Width;
            var rect = new PixelRect((int)((b.X + 16) * scale), (int)((b.Y + 16) * scale),
                (int)((b.Width - 32) * scale), (int)((b.Height - 32) * scale));
            Assert.Equal(0, MaxChannelDelta(busy, plain, rect));
        }
        finally
        {
            host.Close();
            main.Close();
        }
    }

    private static int MaxChannelDelta(WriteableBitmap a, WriteableBitmap b, PixelRect r)
    {
        using var la = a.Lock();
        using var lb = b.Lock();
        var ra = new byte[la.RowBytes];
        var rb = new byte[lb.RowBytes];
        var max = 0;
        for (var y = r.Y; y < r.Bottom; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(la.Address + y * la.RowBytes, ra, 0, ra.Length);
            System.Runtime.InteropServices.Marshal.Copy(lb.Address + y * lb.RowBytes, rb, 0, rb.Length);
            for (var x = r.X * 4; x < r.Right * 4; x++)
                max = Math.Max(max, Math.Abs(ra[x] - rb[x]));
        }
        return max;
    }
}
