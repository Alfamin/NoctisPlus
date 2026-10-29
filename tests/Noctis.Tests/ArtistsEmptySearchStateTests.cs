using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// An Artists search with no results showed the empty-library text ("No artists found /
/// Add music folders in Settings to get started"), as if the library had no music. It now
/// says the search matched nothing, as the Albums page does (seen live 10-12).
/// </summary>
public class ArtistsEmptySearchStateTests
{
    private static List<string> VisibleTexts(Control view)
        => view.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.Text!)
            .ToList();

    private static async Task<(LibraryArtistsViewModel vm, LibraryArtistsView view)> MountAsync()
    {
        var lib = new FakeLibraryService();
        lib.ArtistList.Add(new Noctis.Models.Artist { Id = Guid.NewGuid(), Name = "Taylor Swift" });
        var vm = new LibraryArtistsViewModel(lib) { IsActive = true };
        var view = new LibraryArtistsView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 800, Content = view };
        window.Show();
        await Pump(() => vm.ArtistRows.Count > 0);
        return (vm, view);
    }

    private static async Task Pump(Func<bool> done, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !done())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task SearchWithNoMatches_SaysSo_NotAddMusicFolders()
    {
        var (vm, view) = await MountAsync();

        vm.ApplyFilter("zzzz no such artist");
        await Pump(() => vm.ArtistRows.Count == 0);
        view.UpdateLayout();

        var texts = VisibleTexts(view);
        Assert.Contains(Loc.Instance["LibraryArtists.NoFilterMatches"], texts);
        Assert.DoesNotContain(Loc.Instance["LibraryArtists.AddMusicFoldersSettings"], texts);
        Assert.DoesNotContain(Loc.Instance["LibraryArtists.NoArtistsFound"], texts);
    }

    [AvaloniaFact]
    public async Task EmptyLibraryWithoutSearch_StillPointsAtSettings()
    {
        var vm = new LibraryArtistsViewModel(new FakeLibraryService()) { IsActive = true };
        var view = new LibraryArtistsView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 800, Content = view };
        window.Show();
        await Pump(() => false, budgetMs: 200);
        view.UpdateLayout();

        var texts = VisibleTexts(view);
        Assert.Contains(Loc.Instance["LibraryArtists.NoArtistsFound"], texts);
        Assert.Contains(Loc.Instance["LibraryArtists.AddMusicFoldersSettings"], texts);
        Assert.DoesNotContain(Loc.Instance["LibraryArtists.NoFilterMatches"], texts);
    }
}
