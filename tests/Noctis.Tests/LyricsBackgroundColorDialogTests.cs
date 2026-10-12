using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-12: the lyrics page's background colour picker, a strip that grew sideways under
/// the player bar, as a centred pill pop-up: a titled card, Artwork / Solid / Gradient as a
/// segmented pill, and below it only that mode's choices in a grid, Auto first, the current
/// one ringed. Every option the strip had is still here and still applies live and saves
/// the same settings.
/// </summary>
public class LyricsBackgroundColorDialogTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoctisTests", "lbgdlg-" + Guid.NewGuid().ToString("N"));

    public LyricsBackgroundColorDialogTests(ITestOutputHelper o)
    {
        _o = o;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    /// <summary>settings.json in memory: every load is a fresh copy, as on disk.</summary>
    private sealed class SettingsStore : TestPersistenceService
    {
        private string _json = JsonSerializer.Serialize(new AppSettings());
        public AppSettings Current { get => JsonSerializer.Deserialize<AppSettings>(_json)!; set => _json = JsonSerializer.Serialize(value); }
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Current);
        public override Task SaveSettingsAsync(AppSettings settings) { Current = settings; return Task.CompletedTask; }
    }

    private static (LyricsViewModel Vm, PlayerViewModel Player, SettingsStore Settings) MakeViewModel()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        player.CurrentTrack = new Track { Title = "Soy Peor", Artist = "Bad Bunny", Album = "Soy Peor - Single" };
        var settings = new SettingsStore();
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            settings, new FakeLibraryService());
        return (vm, player, settings);
    }

    internal static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
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

    private static void Pump(int ms) => PumpUntil(() => false, ms);

    private static T Named<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static List<Button> Swatches(Visual root, string grid)
        => Named<ItemsControl>(root, grid).GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("lc-swatch")).ToList();

    private static void Click(Button button) => button.Command!.Execute(button.CommandParameter);

    /// <summary>The page in a window with its view model, the way MainWindow hosts it.</summary>
    private static (Window Owner, LyricsView View) ShowPage(LyricsViewModel vm, ThemeVariant? theme = null)
    {
        var view = new LyricsView { DataContext = vm };
        var owner = new Window { Width = 1280, Height = 800, Content = view, RequestedThemeVariant = theme ?? ThemeVariant.Dark };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        return (owner, view);
    }

    private static LyricsBackgroundColorDialog OpenFromMenu(LyricsViewModel vm, Window owner)
    {
        // What the player bar's ⋯ ▸ Background Color runs.
        vm.OpenBackgroundColorPickerCommand.Execute(null);
        LyricsBackgroundColorDialog? dialog = null;
        Assert.True(PumpUntil(() => (dialog = owner.OwnedWindows.OfType<LyricsBackgroundColorDialog>().SingleOrDefault()) is { IsVisible: true }),
            "the pop-up never opened");
        var host = dialog!.GetVisualDescendants().OfType<PillDialogHost>().Single();
        Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }), "the open animation never settled");
        return dialog;
    }

    /// <summary>
    /// Every option the old strip had (three modes; Auto plus 32 named colours; 30 named
    /// gradients) is in the pop-up, in a grid with names on hover, Auto first in both grids.
    /// The mode is a segmented pill, and only the chosen mode's grid shows.
    /// </summary>
    [AvaloniaFact]
    public void OpensCentredFromTheMenu_WithEveryOption()
    {
        EnsureAppStyles();
        var (vm, _, _) = MakeViewModel();
        var (owner, _) = ShowPage(vm);
        try
        {
            var dialog = OpenFromMenu(vm, owner);
            var host = Assert.IsType<PillDialogHost>(dialog.Content);
            Assert.False(host.BlurBackdrop); // a frozen snapshot would hide the live repaint
            var card = Named<Grid>(dialog, "PickerCard");
            Assert.Equal(HorizontalAlignment.Center, host.Card!.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Center, host.Card.VerticalAlignment);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("PlaybackBar.BackgroundColor"));
            Assert.Contains("pill-primary", Named<Button>(dialog, "DoneButton").Classes);

            var segments = new[] { "ArtworkSegment", "SolidSegment", "GradientSegment" }.Select(n => Named<RadioButton>(dialog, n)).ToList();
            Assert.Equal(new[] { Loc.T("Lyrics.Artwork"), Loc.T("Lyrics.Solid"), Loc.T("Lyrics.Gradient") }, segments.Select(s => s.Content));
            Assert.Same(vm.SelectColorModeArtworkCommand, segments[0].Command);
            Assert.Equal(Loc.T("Lyrics.ShowBlurredAlbumArtworkTip"), ToolTip.GetTip(segments[0]));
            Assert.Same(vm.SelectColorModeSolidCommand, segments[1].Command);
            Assert.Same(vm.SelectColorModeGradientCommand, segments[2].Command);
            Assert.True(segments[0].IsChecked);
            Assert.True(Named<Border>(dialog, "ArtworkPanel").IsEffectivelyVisible);
            Assert.False(Named<StackPanel>(dialog, "SolidPanel").IsEffectivelyVisible);

            vm.SelectColorModeSolidCommand.Execute(null);
            Assert.True(PumpUntil(() => Named<StackPanel>(dialog, "SolidPanel").IsEffectivelyVisible));
            Assert.True(segments[1].IsChecked);
            Assert.False(segments[0].IsChecked);
            Assert.False(Named<Border>(dialog, "ArtworkPanel").IsEffectivelyVisible);
            var solid = Swatches(dialog, "SolidGrid");
            Assert.Equal(33, solid.Count);
            Assert.Equal(11, Assert.IsType<Avalonia.Controls.Primitives.UniformGrid>(Named<ItemsControl>(dialog, "SolidGrid").ItemsPanelRoot).Columns);
            Assert.Contains("auto", solid[0].Classes);
            Assert.Equal(Loc.T("LyricsColor.Auto"), ToolTip.GetTip(solid[0]));
            Assert.Equal("Deep Navy", ToolTip.GetTip(solid[1]));
            Assert.Equal(vm.SolidSwatches.Select(s => s.Key), solid.Select(b => (string)b.CommandParameter!));
            Assert.All(solid.Skip(1), b => Assert.Equal(((ColorSwatch)b.DataContext!).Name, ToolTip.GetTip(b)));

            vm.SelectColorModeGradientCommand.Execute(null);
            Assert.True(PumpUntil(() => Named<StackPanel>(dialog, "GradientPanel").IsEffectivelyVisible));
            Assert.False(Named<StackPanel>(dialog, "SolidPanel").IsEffectivelyVisible);
            var gradient = Swatches(dialog, "GradientGrid");
            Assert.Equal(31, gradient.Count);
            Assert.Contains("auto", gradient[0].Classes);
            Assert.Equal("", gradient[0].CommandParameter);
            Assert.Equal("Purple Night", ToolTip.GetTip(gradient[1]));
            Assert.Equal(30, gradient.Count(b => ((string)b.CommandParameter!).StartsWith("grad:")));

            // Switching modes keeps one card height (no jump).
            var height = card.Bounds.Height;
            vm.SelectColorModeSolidCommand.Execute(null);
            Pump(50);
            Assert.Equal(height, card.Bounds.Height);
            vm.SelectColorModeArtworkCommand.Execute(null);
            Pump(50);
            Assert.Equal(height, card.Bounds.Height);

            // A second request while it is up doesn't stack another one.
            vm.OpenBackgroundColorPickerCommand.Execute(null);
            Pump(100);
            Assert.Single(owner.OwnedWindows.OfType<LyricsBackgroundColorDialog>());
            dialog.Close();
            Assert.True(PumpUntil(() => !dialog.IsVisible));
        }
        finally { owner.Close(); }
    }

    /// <summary>A pick repaints the page behind the pop-up at once, is saved under the same
    /// settings keys as before, and moves the accent ring to it.</summary>
    [AvaloniaFact]
    public void Picks_ApplyLive_SaveAsBefore_AndRingTheChoice()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, PicksProbe);
    }

    private static void PicksProbe()
    {
        var (vm, _, settings) = MakeViewModel();
        var (owner, view) = ShowPage(vm);
        try
        {
            var dialog = OpenFromMenu(vm, owner);
            Named<RadioButton>(dialog, "SolidSegment").Command!.Execute(null);
            Assert.True(PumpUntil(() => Named<StackPanel>(dialog, "SolidPanel").IsEffectivelyVisible));
            Assert.False(settings.Current.LyricsShowArtworkBackground);

            var solid = Swatches(dialog, "SolidGrid");
            Assert.Contains("selected", solid[0].Classes); // Auto until something is picked
            var sage = solid.Single(b => (string)b.CommandParameter! == "#5C8A6E");
            Click(sage);
            Assert.True(PumpUntil(() => sage.Classes.Contains("selected")));
            Assert.DoesNotContain("selected", solid[0].Classes);
            Assert.Single(solid, b => b.Classes.Contains("selected"));
            var ring = sage.GetVisualDescendants().OfType<Ellipse>().Single(e => e.Name == "Ring");
            Assert.Equal(Color.Parse("#E74856"), AccentTestHarness.ColorOf(ring.Stroke));

            // Live: the page's own background is the picked colour now.
            var page = Named<Grid>(view, "MainLayoutGrid");
            Assert.Equal(Color.Parse("#5C8A6E"), ((ISolidColorBrush)page.Background!).Color);
            Assert.Equal("#5C8A6E", settings.Current.LyricsBackgroundColorHex);

            Named<RadioButton>(dialog, "GradientSegment").Command!.Execute(null);
            Assert.True(PumpUntil(() => Named<StackPanel>(dialog, "GradientPanel").IsEffectivelyVisible));
            var gradient = Swatches(dialog, "GradientGrid");
            var dusk = gradient.Single(b => (string)b.CommandParameter! == "grad:#134E5E,#71B280");
            Click(dusk);
            Assert.True(PumpUntil(() => dusk.Classes.Contains("selected")));
            Assert.IsAssignableFrom<ILinearGradientBrush>(page.Background);
            Assert.Equal("grad:#134E5E,#71B280", settings.Current.LyricsBackgroundColorHex);

            // Auto, first in the gradient grid, goes back to the artwork's own gradient.
            Click(gradient[0]);
            Assert.True(PumpUntil(() => gradient[0].Classes.Contains("selected")));
            Assert.DoesNotContain("selected", dusk.Classes);
            Assert.Equal("", settings.Current.LyricsBackgroundColorHex);
            Assert.True(vm.IsColorModeGradient);

            Named<RadioButton>(dialog, "ArtworkSegment").Command!.Execute(null);
            Assert.True(PumpUntil(() => Named<Border>(dialog, "ArtworkPanel").IsEffectivelyVisible));
            Assert.True(settings.Current.LyricsShowArtworkBackground);
            Assert.True(Named<Grid>(view, "LyricsBlurBackdrop").IsVisible);
            dialog.Close();
            Assert.True(PumpUntil(() => !dialog.IsVisible));
        }
        finally { owner.Close(); }
    }

    /// <summary>Escape closes it (animated, once) without reaching the page's window, whose
    /// keys work again right after; so do Done and a click outside the card. A click inside
    /// the card does not close it.</summary>
    [AvaloniaFact]
    public void EscapeDoneAndOutsideClick_Close_AndThePageKeepsItsKeys()
    {
        EnsureAppStyles();
        var (vm, _, _) = MakeViewModel();
        var (owner, _) = ShowPage(vm);
        var ownerKeys = new List<Key>();
        owner.AddHandler(InputElement.KeyDownEvent, (_, e) => ownerKeys.Add(e.Key), RoutingStrategies.Tunnel, handledEventsToo: true);
        try
        {
            var dialog = OpenFromMenu(vm, owner);
            var closed = 0;
            dialog.Closed += (_, _) => closed++;
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(dialog.GetVisualDescendants().OfType<PillDialogHost>().Single().IsClosing);
            Assert.True(PumpUntil(() => closed > 0), "Esc never closed it");
            Pump(250);
            Assert.Equal(1, closed);
            Assert.DoesNotContain(Key.Escape, ownerKeys);

            // The page's window takes keys again (its shortcuts live there).
            owner.Activate();
            owner.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Assert.Contains(Key.Space, ownerKeys);

            // Done.
            dialog = OpenFromMenu(vm, owner);
            Named<Button>(dialog, "DoneButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(PumpUntil(() => !dialog.IsVisible), "Done never closed it");

            // Inside the card: stays. Outside: closes.
            dialog = OpenFromMenu(vm, owner);
            var card = Named<Grid>(dialog, "PickerCard");
            var inside = card.TranslatePoint(new Point(20, 20), dialog)!.Value;
            dialog.MouseDown(inside, MouseButton.Left);
            dialog.MouseUp(inside, MouseButton.Left);
            Pump(300);
            Assert.True(dialog.IsVisible);
            dialog.MouseDown(new Point(10, 10), MouseButton.Left);
            dialog.MouseUp(new Point(10, 10), MouseButton.Left);
            Assert.True(PumpUntil(() => !dialog.IsVisible), "a click outside the card never closed it");
        }
        finally { owner.Close(); }
    }

    /// <summary>The note about a background video, which plays over every mode, shows only
    /// while one is on.</summary>
    [AvaloniaFact]
    public void VideoNote_ShowsOnlyWhileAVideoPlays()
    {
        EnsureAppStyles();
        var (vm, player, _) = MakeViewModel();
        var dialog = new LyricsBackgroundColorDialog { DataContext = vm, Width = 1000, Height = 760 };
        dialog.Show();
        try
        {
            var note = Named<TextBlock>(dialog, "VideoNote");
            Assert.False(note.IsVisible);
            player.LyricsBackgroundMediaPath = System.IO.Path.Combine(_dir, "clip.mp4");
            Assert.True(PumpUntil(() => note.IsVisible));
        }
        finally { dialog.Close(); PumpUntil(() => !dialog.IsVisible); }
    }

    // ── Shots ──

    /// <summary>
    /// Bug (owner screenshots 10-12): the hover and selected rings looked cut flat at the top
    /// and bottom. The ring was drawn right at the edge of its 44 px swatch, and a grid row is
    /// exactly that tall, so at a scaled display its soft outer edge fell outside the row and
    /// was clipped (the columns are wider, so the sides stayed round). The ring now sits
    /// inside the swatch with room on every side, in both grids.
    /// </summary>
    [AvaloniaFact]
    public void SwatchRings_SitInsideTheSwatch_WithRoomOnEverySide()
    {
        EnsureAppStyles();
        var (vm, _, _) = MakeViewModel();
        var (owner, _) = ShowPage(vm);
        try
        {
            var dialog = OpenFromMenu(vm, owner);
            foreach (var (mode, grid) in new (Action, string)[]
                     {
                         (() => vm.SelectColorModeSolidCommand.Execute(null), "SolidGrid"),
                         (() => vm.SelectColorModeGradientCommand.Execute(null), "GradientGrid"),
                     })
            {
                mode();
                Assert.True(PumpUntil(() => Named<ItemsControl>(dialog, grid).IsEffectivelyVisible));
                foreach (var swatch in Swatches(dialog, grid))
                {
                    var ring = swatch.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(e => e.Name == "Ring");
                    var dot = swatch.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single(e => e.Classes.Contains("lc-dot"));
                    var r = ring.Bounds;
                    Assert.True(r.X >= 2 && r.Y >= 2 && r.Right <= swatch.Bounds.Width - 2 && r.Bottom <= swatch.Bounds.Height - 2,
                        $"{grid}: ring {r} touches the edge of its {swatch.Bounds.Size} swatch");
                    // And the dot still fits inside the ring with a gap.
                    Assert.True(dot.Bounds.Width + 2 * ring.StrokeThickness + 2 <= r.Width,
                        $"{grid}: dot {dot.Bounds.Width} doesn't fit inside ring {r.Width}");
                }
            }
            dialog.Close();
            PumpUntil(() => !dialog.IsVisible);
        }
        finally { owner.Close(); }
    }

    /// <summary>
    /// Renders the page with the pop-up open under Skia, one PNG per mode (and Solid on the
    /// Light theme). Explicit only: NOCTIS_TEST_SKIA=1 and NOCTIS_LYRICS_BG_SHOTS names the
    /// file prefix; NOCTIS_LYRICS_BG_SHOTS_DIR overrides the output folder.
    /// </summary>
    [AvaloniaFact]
    public void Shots_SavePngs()
    {
        var prefix = Environment.GetEnvironmentVariable("NOCTIS_LYRICS_BG_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(prefix)) return;
        var dir = Environment.GetEnvironmentVariable("NOCTIS_LYRICS_BG_SHOTS_DIR")
                  ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoctisLyricsBgShots");
        Directory.CreateDirectory(dir);
        EnsureAppStyles();

        // A cover with some colour in it, so Artwork and the Auto swatches show something.
        var cover = System.IO.Path.Combine(_dir, "cover.png");
        using (var bmp = new SKBitmap(600, 600))
        using (var canvas = new SKCanvas(bmp))
        using (var paint = new SKPaint())
        {
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(600, 600),
                new[] { SKColor.Parse("#D9480F"), SKColor.Parse("#7B2CBF"), SKColor.Parse("#1D3557") }, SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, 600, 600, paint);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(cover, data.ToArray());
        }
        DominantColorExtractor.WarmFromFile(cover);

        foreach (var (name, theme, set) in new (string, ThemeVariant, Action<LyricsViewModel>)[]
                 {
                     ("artwork", ThemeVariant.Dark, vm => vm.SelectColorModeArtworkCommand.Execute(null)),
                     ("solid", ThemeVariant.Dark, vm => { vm.SelectColorModeSolidCommand.Execute(null); vm.SetBackgroundColorCommand.Execute("#5C8A6E"); }),
                     ("gradient", ThemeVariant.Dark, vm => { vm.SelectColorModeGradientCommand.Execute(null); vm.SetBackgroundColorCommand.Execute("grad:#134E5E,#71B280"); }),
                     ("solid-auto-light", ThemeVariant.Light, vm => { vm.SelectColorModeSolidCommand.Execute(null); vm.SetBackgroundColorCommand.Execute(""); }),
                 })
        AccentTestHarness.WithAccent("#E74856", theme, () =>
        {
            var (vm, player, _) = MakeViewModel();
            player.CurrentArtPath = cover;
            player.AlbumArt = new Bitmap(cover);
            var (owner, _) = ShowPage(vm, theme);
            try
            {
                Pump(300);
                set(vm);
                Pump(300);
                // The headless pop-up window can't show through to the page, so it gets the
                // page's frame as its own background: what the screen composes.
                var page = owner.CaptureRenderedFrame()!;
                var dialog = OpenFromMenu(vm, owner);
                dialog.RequestedThemeVariant = theme;
                dialog.Width = owner.Width;
                dialog.Height = owner.Height;
                dialog.Background = new ImageBrush(page)
                {
                    Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                };
                Pump(500);
                var path = System.IO.Path.Combine(dir, $"{prefix}-{name}.png");
                dialog.CaptureRenderedFrame()!.Save(path);
                _o.WriteLine(path);
                dialog.Close();
                PumpUntil(() => !dialog.IsVisible);
            }
            finally { owner.Close(); }
        });
    }
}
