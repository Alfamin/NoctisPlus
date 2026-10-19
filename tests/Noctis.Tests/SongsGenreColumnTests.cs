using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Songs list's Genre column printed the stored "Hip-Hop; Rap" while the album page and
/// the lyrics line now show "Hip-Hop · Rap" (GitHub #136). The column shows every genre the
/// same way; the tag itself, sorting and search keep the stored text.
/// </summary>
public class SongsGenreColumnTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    [AvaloniaFact]
    public void MultiGenreSong_ShowsEachGenreDotted()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albumId = Guid.NewGuid();
        lib.TrackList.Add(new Track
        {
            Id = Guid.NewGuid(), Title = "Song", Artist = "Artist", AlbumArtist = "Artist", Album = "Album",
            AlbumId = albumId, Genre = "Hip-Hop; Rap", Duration = TimeSpan.FromSeconds(176), FilePath = "C:/m/song.mp3",
        });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        vm.Refresh();
        var view = new LibrarySongsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Pump(6);

        var cells = view.GetVisualDescendants().OfType<HighlightTextBlock>().Select(t => t.DisplayText).ToList();
        Assert.Contains("Hip-Hop · Rap", cells);
        Assert.DoesNotContain("Hip-Hop; Rap", cells);
        Assert.Equal("Hip-Hop; Rap", lib.TrackList[0].Genre);
        win.Close();
    }
}
