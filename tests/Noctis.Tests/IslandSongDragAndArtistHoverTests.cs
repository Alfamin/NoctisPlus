using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Luwi, 2026-10-17): (1) drag the playing song from the Player Island onto a
/// playlist without opening its album — the island's art and title/artist block start the
/// same track drag the library rows do; (2) the island's artist line boxes the ONE name
/// under the pointer, like the rows' ArtistCreditLink, instead of the whole credit.
/// </summary>
public class IslandSongDragAndArtistHoverTests
{
    public IslandSongDragAndArtistHoverTests() => ArtistCredit.ResetToDefaults();

    private static PlayerViewModel MakePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService());

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static (PlaybackBarView Bar, Window Win, PlayerViewModel Player) ShowBar(Track? track)
    {
        EnsureAppStyles();
        var player = MakePlayer();
        player.CurrentTrack = track;
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 1400, Height = 200, Content = bar };
        win.Show();
        bar.FindControl<Grid>("TrackInfoPanel")!.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (bar, win, player);
    }

    // ── Drag payload ──

    [Fact]
    public void DragItem_is_the_payload_instead_of_the_data_context()
    {
        var track = new Track { Title = "Kerala", Artist = "Bonobo", FilePath = @"C:\Music\Kerala.flac" };
        var host = new Border { DataContext = new object() };
        DragFileBehavior.SetDragItem(host, track);

        var payload = DragFileBehavior.PayloadOf(host);
        Assert.Same(track, payload);
        var tracks = DragFileBehavior.GetTracks(payload);
        Assert.Same(track, Assert.Single(tracks!));
        var preview = DragFileBehavior.BuildPreview(payload, tracks!);
        Assert.Equal(new DragFileBehavior.DragPreview("Kerala", "Bonobo", null, 1), preview);

        // The very transfer a library row builds, so the sidebar playlists and the queue
        // (both read GetDraggedTracks) take it with no drop code of their own.
        using var transfer = DragFileBehavior.BuildTracksTransfer(tracks!, Array.Empty<IStorageItem>());
        Assert.Same(track, Assert.Single(DragFileBehavior.GetDraggedTracks(transfer)!));
    }

    [Fact]
    public void Without_a_DragItem_the_data_context_is_still_the_payload()
    {
        var track = new Track { Title = "Row", FilePath = @"C:\Music\Row.mp3" };
        var row = new Border { DataContext = track };

        Assert.Same(track, DragFileBehavior.PayloadOf(row));
    }

    [AvaloniaFact]
    public void Island_art_and_title_block_drag_the_playing_song()
    {
        var track = new Track { Title = "Kerala", Artist = "Bonobo", FilePath = @"C:\Music\Kerala.flac" };
        var (bar, win, player) = ShowBar(track);
        try
        {
            foreach (var name in new[] { "AlbumArtThumb", "TrackInfoContent" })
            {
                var source = bar.FindControl<Control>(name)!;
                Assert.True(DragFileBehavior.GetEnableFileDrag(source), name);
                Assert.Same(track, DragFileBehavior.PayloadOf(source));
            }

            // The seek strip must never start a song drag: no drag source above the slider.
            var slider = bar.FindControl<Slider>("SeekSlider")!;
            Assert.DoesNotContain(slider.GetVisualAncestors().OfType<Control>().Prepend(slider),
                c => DragFileBehavior.GetEnableFileDrag(c));

            // Nothing playing: no payload, so a drag never starts.
            player.CurrentTrack = null;
            Dispatcher.UIThread.RunJobs();
            var art = bar.FindControl<Control>("AlbumArtThumb")!;
            Assert.Null(DragFileBehavior.GetTracks(DragFileBehavior.PayloadOf(art)));
        }
        finally
        {
            win.Close();
        }
    }

    // ── Per-artist hover box ──

    private static Rect? PaintedBox(TextBlock run)
    {
        if (run.Background is not DrawingBrush { Drawing: DrawingGroup group }) return null;
        return (group.Children[1] as GeometryDrawing)?.Geometry is RectangleGeometry r ? r.Rect : null;
    }

    private static Point CharCentre(TextBlock run, int index, Window win)
    {
        var a = run.TextLayout.HitTestTextPosition(index);
        var b = run.TextLayout.HitTestTextPosition(index + 1);
        return run.TranslatePoint(new Point((a.X + b.X) / 2, run.Bounds.Height / 2), win)!.Value;
    }

    [AvaloniaFact]
    public void Island_artist_line_boxes_only_the_name_under_the_pointer()
    {
        const string credit = "Bonobo, Jordan Rakei";
        var (bar, win, _) = ShowBar(new Track { Title = "Kerala", Artist = credit, FilePath = "k.flac" });
        try
        {
            var run = bar.FindControl<TextBlock>("ArtistNameTextBlock")!;
            var button = bar.FindControl<Button>("ArtistNameButton")!;
            var wrapper = (Border)run.GetVisualParent()!;
            Assert.Equal(credit, run.Text);

            var jordan = credit.IndexOf("Jordan", StringComparison.Ordinal);
            win.MouseMove(CharCentre(run, jordan + 2, win));
            Dispatcher.UIThread.RunJobs();

            var box = PaintedBox(run);
            Assert.NotNull(box);
            Assert.Equal(run.TextLayout.HitTestTextPosition(jordan).X, box!.Value.Left, 1);
            Assert.Equal(run.TextLayout.HitTestTextPosition(credit.Length).X, box.Value.Right, 1);
            // No whole-credit fill any more (the old artist-text-hover rule painted the wrapper).
            Assert.True(button.IsPointerOver);
            Assert.Null(wrapper.Background);
            Assert.Equal(Loc.T("PlaybackBar.ViewNamedArtistTip", "Jordan Rakei"), ToolTip.GetTip(button));

            win.MouseMove(CharCentre(run, 2, win));
            Dispatcher.UIThread.RunJobs();
            box = PaintedBox(run);
            Assert.NotNull(box);
            Assert.Equal(0, box!.Value.Left, 1);
            Assert.Equal(run.TextLayout.HitTestTextPosition("Bonobo".Length).X, box.Value.Right, 1);
            Assert.Equal(Loc.T("PlaybackBar.ViewNamedArtistTip", "Bonobo"), ToolTip.GetTip(button));

            // Over the ", " separator: no box at all, and the click falls back to the primary artist.
            win.MouseMove(CharCentre(run, "Bonobo".Length, win));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(run.Background);
            Assert.Equal(Loc.T("PlaybackBar.ViewArtistTip"), ToolTip.GetTip(button));
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public void Island_artist_box_follows_the_ticker_under_a_still_pointer()
    {
        const string credit = "Bonobo, Jordan Rakei";
        var (bar, win, _) = ShowBar(new Track { Title = "Kerala", Artist = credit, FilePath = "k.flac" });
        try
        {
            var run = bar.FindControl<TextBlock>("ArtistNameTextBlock")!;
            var button = bar.FindControl<Button>("ArtistNameButton")!;
            win.MouseMove(CharCentre(run, 2, win)); // over "Bonobo"
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, PaintedBox(run)!.Value.Left, 1);

            // The marquee slides the text left until "Jordan" sits under the pointer.
            var jordan = credit.IndexOf("Jordan", StringComparison.Ordinal);
            var shift = run.TextLayout.HitTestTextPosition(jordan + 2).X - run.TextLayout.HitTestTextPosition(2).X;
            typeof(PlaybackBarView).GetMethod("SetArtistNameOffset", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(bar, new object[] { -shift });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(run.TextLayout.HitTestTextPosition(jordan).X, PaintedBox(run)!.Value.Left, 1);
            Assert.Equal(Loc.T("PlaybackBar.ViewNamedArtistTip", "Jordan Rakei"), ToolTip.GetTip(button));
        }
        finally
        {
            win.Close();
        }
    }
}
