using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Animation;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>Now Playing's ⋯ menu (owner 10-13): Apple's glass card popping out of the button with
/// the desktop menu's motion, the song's actions in it, and every way out of it.</summary>
public class MobileNowPlayingMenuTests(ITestOutputHelper output)
{
    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);   // the frame clock is the wall clock
        }
    }

    private static (MobileFixtures.Rig rig, Window window, NowPlayingPage page) Open()
    {
        var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone", artist: "Band") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        window.UpdateLayout();
        return (rig, window, view.FindControl<NowPlayingPage>("NowPlaying")!);
    }

    private static double ScaleOf(Visual v) => v.RenderTransform?.Value.M11 ?? 1;

    [AvaloniaFact]
    public void TheMenu_PopsOutOfTheMoreButton_OverThePage_WithTheSongsActions()
    {
        var (rig, window, page) = Open();
        using var _ = rig;
        var more = MobileFixtures.Named<Button>(page, "NowPlayingMore");
        more.Command!.Execute(null);
        Pump(30);

        var menu = MobileFixtures.Named<NowPlayingMenu>(page, "Menu");
        var card = menu.FindControl<Panel>("Card")!;
        Assert.True(menu.IsVisible);
        Assert.Null(rig.Shell.Sheet);                                   // not the bottom sheet
        Assert.Equal(1, ScaleOf(card), 3);
        Assert.Equal(1, MobileFixtures.Named<GlassPanel>(menu, "Glass").Fade, 3);

        // The card's bottom-right corner sits on the ⋯ button's.
        var cardBox = new Rect(card.TranslatePoint(default, page)!.Value, card.Bounds.Size);
        var moreBox = new Rect(more.TranslatePoint(default, page)!.Value, more.Bounds.Size);
        Assert.Equal(moreBox.Bottom, cardBox.Bottom, 1);
        Assert.Equal(moreBox.Right, cardBox.Right, 1);
        Assert.True(cardBox.Top > 0);

        foreach (var name in new[] { "MenuFavourite", "MenuAddToPlaylist", "MenuPlayNext", "MenuAddToQueue", "MenuPin",
                     "MenuGoToAlbum", "MenuGoToArtist", "MenuLyrics" })
            Assert.True(MobileFixtures.Named<Control>(menu, name).IsEffectivelyVisible, name);
        var texts = menu.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Band", texts);                                 // Go to Artist's second line
        window.Close();
    }

    /// <summary>The desktop menu's pop (MenuOpenAnimation.Pop, the same numbers): the card starts
    /// at 0.9 with its frost faded out, grows from the ⋯ button on the desktop's soft decelerate,
    /// rising steadily to rest with no overshoot, and the close runs it back down to 0.94, inert
    /// at once, hiding the card after it.</summary>
    [AvaloniaFact]
    public void TheMenu_GrowsInAndShrinksOut_Smoothly()
    {
        var (rig, window, page) = Open();
        using var _ = rig;
        var menu = MobileFixtures.Named<NowPlayingMenu>(page, "Menu");
        var card = menu.FindControl<Panel>("Card")!;
        var glass = menu.FindControl<GlassPanel>("Glass")!;
        var more = MobileFixtures.Named<Button>(page, "NowPlayingMore");

        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        Assert.Equal(0.9, ScaleOf(card), 3);                             // the start, before any frame
        Assert.Equal(0, glass.Fade);
        Dispatcher.UIThread.RunJobs();                                   // the frame that sets the targets

        var grow = Assert.IsType<TransformOperationsTransition>(Assert.Single(card.Transitions!));
        Assert.Equal(TimeSpan.FromMilliseconds(280), grow.Duration);
        Assert.IsType<CubicBezierEase>(grow.Easing);
        Assert.Equal(TimeSpan.FromMilliseconds(160), Assert.IsType<DoubleTransition>(Assert.Single(glass.Transitions!)).Duration);
        // It grows out of the button: the origin is the ⋯ button's centre inside the card.
        var origin = card.RenderTransformOrigin.ToPixels(card.Bounds.Size);
        var centre = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), menu)!.Value
            - card.Bounds.Position;
        Assert.Equal(centre.X, origin.X, 1);
        Assert.Equal(centre.Y, origin.Y, 1);

        var scales = new List<double> { 0.9 };
        var fades = new List<double> { 0 };
        for (var i = 0; i < 30; i++)
        {
            Pump(1);
            scales.Add(ScaleOf(card));
            fades.Add(glass.Fade);
        }
        output.WriteLine("scale: " + string.Join(" ", scales.Select(v => v.ToString("0.000"))));
        output.WriteLine("fade:  " + string.Join(" ", fades.Select(v => v.ToString("0.000"))));
        Assert.Contains(scales, s => s > 0.92 && s < 0.99);              // frames in between: a glide, not a snap
        Assert.Equal(1, scales[^1], 3);
        Assert.Equal(1, fades[^1], 3);
        for (var i = 1; i < scales.Count; i++)
        {
            Assert.True(scales[i] >= scales[i - 1] - 1e-6, $"scale fell back at frame {i}: {string.Join(", ", scales)}");
            Assert.True(scales[i] <= 1 + 1e-6, "overshoot");
        }

        rig.Shell.CloseSheet();
        Assert.False(menu.IsHitTestVisible);                             // inert while it fades
        Assert.True(menu.IsVisible);
        Assert.Equal(TimeSpan.FromMilliseconds(NowPlayingMenu.PopCloseMs),
            Assert.IsType<TransformOperationsTransition>(Assert.Single(card.Transitions!)).Duration);
        Pump((int)(NowPlayingMenu.PopCloseMs / 16) + 8);
        Assert.False(menu.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void BackAndATapOutside_CloseTheMenu_NotThePlayer()
    {
        var (rig, window, page) = Open();
        using var _ = rig;
        var menu = MobileFixtures.Named<NowPlayingMenu>(page, "Menu");

        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        Pump(20);
        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(rig.Shell.IsNowPlayingMenuOpen);
        Assert.True(rig.Shell.IsNowPlayingOpen);

        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        Pump(20);
        var play = MobileFixtures.Named<Button>(page, "PlayPauseButton");
        var wasPlaying = rig.Shell.Player.IsPlaying;
        var point = play.TranslatePoint(new Point(play.Bounds.Width / 2, play.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.False(rig.Shell.IsNowPlayingMenuOpen);
        Assert.Equal(wasPlaying, rig.Shell.Player.IsPlaying);            // the tap only closed the menu
        Assert.True(rig.Shell.IsNowPlayingOpen);

        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        rig.Shell.CloseNowPlayingCommand.Execute(null);                  // closing the player takes it too
        Assert.False(rig.Shell.IsNowPlayingMenuOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheMenusActions_ActOnTheSong_AndCloseIt()
    {
        var (rig, window, page) = Open();
        using var _ = rig;
        var track = rig.Shell.Player.CurrentTrack!;

        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        await rig.Shell.NowPlayingMenu!.ToggleFavouriteCommand.ExecuteAsync(null);
        Assert.True(track.IsFavorite);
        Assert.False(rig.Shell.IsNowPlayingMenuOpen);

        // Add to Playlist: the card gives way to the bottom sheet's playlist page.
        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        rig.Shell.NowPlayingMenu!.PickPlaylistCommand.Execute(null);
        Assert.False(rig.Shell.IsNowPlayingMenuOpen);
        Assert.True(rig.Shell.Sheet!.IsPickingPlaylist);
        Assert.Same(track, rig.Shell.Sheet.Track);
        rig.Shell.CloseSheet();

        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        rig.Shell.NowPlayingMenu!.ShowLyricsCommand.Execute(null);
        Assert.False(rig.Shell.IsNowPlayingMenuOpen);
        Assert.True(rig.Shell.IsLyricsOpen);
        window.Close();
    }
}
