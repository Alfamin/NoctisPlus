using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-13, Apple Music's bar for an offline player: a fifth tab, Home, first and the start
/// tab, holding the listening rails (Pinned, Recently Played, On Repeat), while Library becomes
/// Apple's Library (the list rows and Recently Added). The mini player's text and buttons are
/// Apple's sizes, and its progress fill and line are Settings switches, both off by default.
/// </summary>
public class MobileHomeTabTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static Func<PersistenceService, Task> WithFolder(Action<AppSettings>? edit = null) => async p =>
    {
        var s = await p.LoadSettingsAsync();
        s.MusicFolders.Add("content://tree/music");
        edit?.Invoke(s);
        await p.SaveSettingsAsync(s);
    };

    /// <summary>A library with a play log: every rail has something on it.</summary>
    private static MobileFixtures.Rig MakeRig(Action<AppSettings>? edit = null)
    {
        var a = MobileFixtures.Song("A1");
        var b = MobileFixtures.Song("B1");
        return MobileFixtures.MakeRig(new[] { a, b }, new[] { MobileFixtures.MakeAlbum("Alpha", "X", a, b) }, WithFolder(edit),
            log: h =>
            {
                foreach (var d in new[] { 1, 2, 3 }) h.Seed(a, Now.AddDays(-d));
                foreach (var d in new[] { 1, 2, 3, 4 }) h.Seed(b, Now.AddDays(-d));
            });
    }

    private static bool Holds(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name);

    [AvaloniaFact]
    public void Home_IsTheStartTab_WithTheListeningRails_AndLibraryKeepsItsListsAndRecentlyAdded()
    {
        using var rig = MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        Assert.Equal(MobileTab.Home, rig.Shell.SelectedTab);
        var home = MobileFixtures.Find<HomePage>(view);
        var library = MobileFixtures.Find<LibraryPage>(view);
        Assert.True(home.IsEffectivelyVisible);
        Assert.False(library.IsEffectivelyVisible);

        foreach (var section in new[] { "PinnedSection", "RecentlyPlayedSection", "OnRepeatSection" })
        {
            Assert.True(MobileFixtures.Named<StackPanel>(home, section).IsEffectivelyVisible, section);
            Assert.False(Holds(library, section), $"{section} is still on Library");
        }
        // Apple's Home order: Pinned, then Recently Played, then On Repeat.
        var ys = new[] { "PinnedSection", "RecentlyPlayedSection", "OnRepeatSection" }
            .Select(n => MobileFixtures.Named<StackPanel>(home, n).TranslatePoint(default, home)!.Value.Y).ToArray();
        Assert.Equal(ys.OrderBy(y => y), ys);
        Assert.Equal("Home", MobileFixtures.Named<TextBlock>(home, "HomeTitle").Text);
        Assert.True(MobileFixtures.Named<ProfileButton>(home, "HomeProfile").IsEffectivelyVisible);

        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        window.UpdateLayout();
        Assert.False(home.IsEffectivelyVisible);
        Assert.True(MobileFixtures.Named<ItemsControl>(library, "LibraryRowList").IsEffectivelyVisible);
        Assert.True(MobileFixtures.Named<StackPanel>(library, "RecentlyAddedSection").IsEffectivelyVisible);
        Assert.False(Holds(home, "RecentlyAddedSection"));
        window.Close();
    }

    /// <summary>Device 10-13: with no notice to show, the shared notices control still stood in
    /// the page's stack and took its 22 dp Spacing, so Library's rows (and Home's Pinned) sat
    /// lower than before. The first section starts right at the top of the content.</summary>
    [AvaloniaFact]
    public void NoNotice_LeavesNoGap_AboveTheFirstSection()
    {
        using var rig = MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var home = MobileFixtures.Find<HomePage>(view);
        Assert.False(rig.Shell.Library.HasNotice);
        Assert.Equal(0, MobileFixtures.Named<StackPanel>(home, "PinnedSection")
            .TranslatePoint(default, MobileFixtures.Named<StackPanel>(home, "HomeContent"))!.Value.Y, 1);

        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        window.UpdateLayout();
        var library = MobileFixtures.Find<LibraryPage>(view);
        Assert.Equal(0, MobileFixtures.Named<ItemsControl>(library, "LibraryRowList")
            .TranslatePoint(default, MobileFixtures.Named<StackPanel>(library, "LibraryContent"))!.Value.Y, 1);
        window.Close();
    }

    /// <summary>The start tab is where a new phone lands: the first-run folder card shows there.</summary>
    [AvaloniaFact]
    public void Home_ShowsTheConnectCard_OnAnEmptyPhone()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var home = MobileFixtures.Find<HomePage>(view);
        Assert.True(MobileFixtures.Named<Border>(home, "ConnectCard").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<StackPanel>(home, "PinnedSection").IsVisible);
        window.Close();
    }

    /// <summary>
    /// Five tabs on a 1080 px phone (411 dp): every label fits its fifth of the bar whole. The
    /// headless text stubs have no real glyph widths, so each label is measured in Inter (the
    /// phone's font) SemiBold, a little wider than the tabs' Medium: an upper bound.
    /// </summary>
    [AvaloniaFact]
    public void FiveTabs_FitA1080pxPhone_WithTheirLabelsWhole()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view, width: 1080 / 2.625);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))) dir = dir.Parent;
        using var inter = SKTypeface.FromFile(Path.Combine(dir!.FullName, "src", "Noctis.UI", "Assets", "Fonts", "Inter-SemiBold.ttf"));
        var bar = view.FindControl<Grid>("TabBar")!.Bounds.Width;
        foreach (var name in new[] { "HomeTab", "FavoritesTab", "PlaylistsTab", "LibraryTab", "SearchTab" })
        {
            var tab = view.FindControl<Button>(name)!;
            var label = tab.GetVisualDescendants().OfType<TextBlock>().Single();
            Assert.Equal(bar / 5, tab.Bounds.Width, 1);
            using var font = new SKFont(inter, (float)label.FontSize);
            var width = font.MeasureText(label.Text);
            Assert.True(width <= tab.Bounds.Width - 12, $"{name}: \"{label.Text}\" is {width:0.#} dp in a {tab.Bounds.Width:0.#} dp tab");
        }
        window.Close();
    }

    /// <summary>Folded, the bar's round button shows the current tab's own glyph, Home included.</summary>
    [AvaloniaFact]
    public void TheFoldedTabCircle_ShowsTheCurrentTabsIcon()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var circle = view.FindControl<Button>("CollapsedTabButton")!;
        foreach (var (tab, key) in new[]
                 {
                     (MobileTab.Home, "TabHomeFilledIcon"), (MobileTab.Favorites, "TabFavoritesFilledIcon"),
                     (MobileTab.Playlists, "TabPlaylistsFilledIcon"), (MobileTab.Library, "TabLibraryFilledIcon"),
                     (MobileTab.Search, "TabSearchFilledIcon"),
                 })
        {
            rig.Shell.SelectTabCommand.Execute(tab);
            window.UpdateLayout();
            var shown = circle.GetVisualDescendants().OfType<PathIcon>().Where(i => i.IsVisible).ToList();
            Assert.Single(shown);
            Assert.True(view.TryFindResource(key, out var geometry));
            Assert.Same(geometry, shown[0].Data);
        }
        window.Close();
    }

    /// <summary>Library is no longer the start tab: Back on it first ends the rows' Edit mode,
    /// then returns to Home, and only Home's root lets the system finish the app.</summary>
    [AvaloniaFact]
    public void Back_OnLibrary_EndsEditing_ThenReturnsHome()
    {
        using var rig = MakeRig();
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        rig.Shell.LibraryRows.EditCommand.Execute(null);

        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(rig.Shell.LibraryRows.IsEditing);
        Assert.Equal(MobileTab.Library, rig.Shell.SelectedTab);
        Assert.True(rig.Shell.TryHandleBack());
        Assert.Equal(MobileTab.Home, rig.Shell.SelectedTab);
        Assert.False(rig.Shell.TryHandleBack());
    }
}
