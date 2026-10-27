using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Headless mount of the Home page after the 09-13 "Continue listening" layout: pins
/// that the XAML resolves its resources/bindings at runtime and that the hero, the two
/// side-by-side charts and the compact rows realize.
/// </summary>
public class HomeViewMountTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    private static readonly Guid AlbumId = Guid.NewGuid();

    private static Track T(string title, string artist, int plays) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = artist, AlbumArtist = artist, Album = "Un Verano Sin Ti",
        AlbumId = AlbumId, Duration = TimeSpan.FromSeconds(176), PlayCount = plays,
    };

    [AvaloniaFact]
    public async Task FirstImportedSongs_WithNoHistory_ShowAUsefulStartInsteadOfBlankHome()
    {
        EnsureAppStyles();
        var library = new FakeLibraryService();
        library.TrackList.Add(T("First song", "Synthetic", 0));
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService());
        using var vm = new HomeViewModel(player, library, new SidebarViewModel(persistence, library));
        await vm.RefreshAsync();
        Assert.False(vm.IsLibraryEmpty);
        Assert.True(vm.ShowWelcome);
        var view = new HomeView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 900 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Your music is ready");
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, vm.WelcomeActionCommand));
            player.PlayFromRow(library.TrackList, 0);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.ShowWelcome); // Never cover Continue listening while playback starts.
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] // Owner #183: two streams on an empty library drew Last Played under the centred welcome card.
    public async Task PlaysOnAnEmptyLibrary_SitAboveTheWelcomeCard_AndLastPlayedTakesTheWholeRow()
    {
        EnsureAppStyles();
        var library = new FakeLibraryService();
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService());
        using var empty = new HomeViewModel(player, library, new SidebarViewModel(persistence, library));
        await empty.RefreshAsync();
        Assert.True(empty.ShowWelcomeCentered); Assert.False(empty.ShowWelcomeInline);

        player.History.Add(T("Streamed", "Someone", 1)); // not in the library, as a stream is
        using var vm = new HomeViewModel(player, library, new SidebarViewModel(persistence, library));
        await vm.RefreshAsync();
        Assert.True(vm.IsLibraryEmpty); Assert.Single(vm.LastPlayedRows);
        Assert.False(vm.ShowWelcomeCentered); Assert.True(vm.ShowWelcomeInline);
        var view = new HomeView { DataContext = vm };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var last = view.FindControl<StackPanel>("LastPlayedPanel")!;
            Assert.Equal(0, Grid.GetColumn(last));
            Assert.Equal(0, view.FindControl<Grid>("HeroGrid")!.ColumnDefinitions[2].Width.Value);
            var welcome = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text == "Let's fill your library").ToList();
            var title = Assert.Single(welcome);
            Assert.True(title.TranslatePoint(default, view)!.Value.Y > last.TranslatePoint(new Point(0, last.Bounds.Height), view)!.Value.Y);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] // Owner #186: the first-run card offers Connect Telegram while nobody is signed in.
    public async Task TheWelcomeCard_OffersConnectTelegram_UntilSignedIn()
    {
        EnsureAppStyles();
        var library = new FakeLibraryService();
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService());
        using var vm = new HomeViewModel(player, library, new SidebarViewModel(persistence, library));
        await vm.RefreshAsync();
        Assert.False(vm.ShowConnectTelegram); // Free Music Finder off: no button
        bool? signedIn = false;
        var opened = 0;
        vm.TelegramSignedIn = () => signedIn;
        vm.ConnectTelegram = () => { opened++; signedIn = true; return Task.CompletedTask; };
        vm.RefreshTelegram();
        var view = new HomeView { DataContext = vm };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Button Connect() => view.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Connect Telegram");
            Assert.True(Connect().IsEffectivelyVisible);
            await vm.ConnectTelegramFromHomeCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, opened);
            Assert.False(vm.ShowConnectTelegram);
            Assert.False(Connect().IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HomePage_MountsHeroAndSideBySideCharts()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var a = T("Un Ratito", "Bad Bunny", 56);
        var b = T("ANGELS", "Chase Atlantic", 55);
        var c = T("INTRO", "Chase Atlantic", 54);
        lib.TrackList.AddRange(new[] { a, b, c });
        ((List<Album>)lib.Albums).Add(new Album
        {
            Id = AlbumId, Name = "Un Verano Sin Ti", Artist = "Bad Bunny", Tracks = new List<Track> { a, b, c },
        });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        player.History.Add(a);
        player.History.Add(b);
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        await vm.RefreshAsync();

        var view = new HomeView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        // Hero: the newest history entry, its kicker, the album after the artist (no year
        // tagged here), and the Resume button (nothing is playing).
        Assert.Same(a, vm.ContinueTrack);
        Assert.Equal(" · Un Verano Sin Ti", vm.ContinueDetail);
        Assert.False(vm.IsContinuePlaying);
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Continue listening", texts);
        Assert.Contains("Resume", texts);
        Assert.Contains("Most Played", texts);
        Assert.Contains("Last Played", texts);
        var hero = view.FindControl<Border>("ContinueHero");
        Assert.NotNull(hero);
        Assert.True(hero!.IsVisible);

        // Charts sit side by side (two star columns) at this width.
        var grid = view.FindControl<Grid>("HeroGrid")!;
        Assert.True(grid.ColumnDefinitions[2].Width.IsStar);
        Assert.Equal(2, Grid.GetColumn(view.FindControl<StackPanel>("LastPlayedPanel")!));

        // Albums row: one recently played album; covers use the Albums-page maths (Auto =
        // five across the row's own measured width, 2px slack) and carry the hover overlay.
        var row = view.FindControl<ItemsControl>("AlbumsRow")!;
        Assert.True(row.Bounds.Width > 0);
        Assert.Equal(Noctis.Helpers.AlbumGridMetrics.ComputeTileSize(row.Bounds.Width - 2, 5), vm.AlbumTileSize, precision: 0);
        var tiles = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
        Assert.Single(tiles);
        Assert.Single(tiles[0].GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("tile-play"));
        Assert.Single(tiles[0].GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("tile-more"));

        // Compact rows: 3 Most Played + 2 Last Played, none with a dots button.
        var rows = view.GetVisualDescendants().OfType<Button>().Where(r => r.Classes.Contains("home-chart-row")).ToList();
        Assert.Equal(5, rows.Count);
        Assert.Equal(0, rows.Sum(r => r.GetVisualDescendants().OfType<Button>().Count(x => x.Classes.Contains("chart-dots"))));
    }

    // -- The playing chart row is filled in the accent colour with contrasting text, as on
    //    the artist page (user ask 09-14). It used to be accent TEXT on the card fill; the
    //    heart keeps its own red unless told otherwise, which on a red accent would be
    //    invisible against the new fill. --
    [AvaloniaFact]
    public async Task HomePage_PlayingChartRow_IsAccentFilledWithContrastingTextAndHeart()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var a = T("Un Ratito", "Bad Bunny", 56);
        var b = T("ANGELS", "Chase Atlantic", 55);
        a.IsNowPlaying = true;
        a.IsFavorite = true;
        b.IsFavorite = true;
        lib.TrackList.AddRange(new[] { a, b });
        ((List<Album>)lib.Albums).Add(new Album
        {
            Id = AlbumId, Name = "Un Verano Sin Ti", Artist = "Bad Bunny", Tracks = new List<Track> { a, b },
        });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        await vm.RefreshAsync();

        var view = new HomeView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var accentFill = view.FindResource(Avalonia.Styling.ThemeVariant.Dark, "AccentButtonBackground");
        var rowForeground = view.FindResource(Avalonia.Styling.ThemeVariant.Dark, "AccentForegroundBrush");
        var bodies = view.GetVisualDescendants().OfType<Border>()
            .Where(x => x.Classes.Contains("chart-body")).ToList();
        Assert.NotEmpty(bodies);
        var playing = bodies.Where(x => x.Classes.Contains("playing")).ToList();
        Assert.NotEmpty(playing);

        foreach (var body in playing)
        {
            Assert.Same(accentFill, body.Background);
            Assert.Equal(new Avalonia.CornerRadius(6), body.CornerRadius);
            var title = body.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("chart-title"));
            Assert.Same(rowForeground, title.Foreground);
            var heart = body.GetVisualDescendants().OfType<HeartIcon>().Single();
            Assert.Same(rowForeground, heart.OnBrush);
            Assert.Same(rowForeground, heart.OffBrush);
            Assert.NotSame(body.Background, heart.OnBrush); // the reported red-on-red heart
        }

        // A row that is not playing keeps the card fill and its own heart red.
        var other = bodies.First(x => !x.Classes.Contains("playing"));
        Assert.Null(other.Background);
        Assert.NotSame(rowForeground, other.GetVisualDescendants().OfType<HeartIcon>().Single().OnBrush);
    }

}
