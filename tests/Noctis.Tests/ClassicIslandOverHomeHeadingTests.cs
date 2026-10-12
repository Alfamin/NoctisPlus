using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #133, Liquid Glass off (the owner's settings: Gray theme, Glass Opacity 0.398): the
/// real Home page under the real island, the window sized so the "Last Played" heading sits
/// in the pill. The classic island is a plain translucent fill (no blur), drawn ABOVE the
/// page: the heading's glyphs under the fill are dimmed to ~0.6 of white, never drawn at
/// full strength over it. Full strength only above the pill and in its 1.5px outline ring,
/// which the fill does not reach (it sits inside the Border's stroke) — the 255 a live
/// max-over-the-island measurement picked up. Real Skia only (NOCTIS_TEST_SKIA=1).
/// </summary>
public class ClassicIslandOverHomeHeadingTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static async Task Flush(int settleMs = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(settleMs);
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        } while (DateTime.UtcNow < until);
    }

    private static readonly Guid AlbumId = Guid.NewGuid();

    private static Track T(int i) => new()
    {
        Id = Guid.NewGuid(), Title = "Song " + i, Artist = "Artist " + i, AlbumArtist = "Artist " + i, Album = "Album",
        AlbumId = AlbumId, Duration = TimeSpan.FromSeconds(176), PlayCount = 50 - i,
    };

    [AvaloniaFact]
    public async Task LastPlayedHeading_UnderTheClassicIsland_IsDimmedByTheFill_NotDrawnOverIt()
    {
        if (!HeadlessTestApp.RealRendering) Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var tracks = Enumerable.Range(0, 6).Select(T).ToArray();
        lib.TrackList.AddRange(tracks);
        ((List<Album>)lib.Albums).Add(new Album { Id = AlbumId, Name = "Album", Artist = "Artist 0", Tracks = tracks.ToList() });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        foreach (var t in tracks) player.History.Add(t);
        player.IslandBackgroundOpacity = 0.398;
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        await vm.RefreshAsync();

        var home = new HomeView { DataContext = vm };
        // As MainWindow hosts them: the page in the content grid, the island over it (ZIndex 30).
        var bar = new PlaybackBarView
        {
            DataContext = player, CompactWhenLyricsPageActive = false, ClipToBounds = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 16), ZIndex = 30,
        };
        var pageFill = Application.Current!.FindResource(Avalonia.Styling.ThemeVariant.Dark, "AppMainBackground") as IBrush;
        var root = new Panel
        {
            Children = { new DockPanel { Margin = new Thickness(76, 0, 0, 0), Children = { new Grid { Background = pageFill, Children = { home } } } }, bar },
        };
        var win = new Window { Width = 1300, Height = 800, Content = root, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark };
        win.Show();
        try
        {
            await Flush();
            var pill = bar.FindControl<Border>("IslandBorder")!;
            var heading = home.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Last Played");
            var pillAt = pill.TranslatePoint(default, root)!.Value;
            var headingAt = heading.TranslatePoint(default, root)!.Value;
            win.Height -= pillAt.Y - headingAt.Y - 14; // the pill cuts the heading 14px down: ink above and under it
            await Flush();
            pillAt = pill.TranslatePoint(default, root)!.Value;
            headingAt = heading.TranslatePoint(default, root)!.Value;

            var frame = win.CaptureRenderedFrame()!;
            var stride = frame.PixelSize.Width * 4;
            var px = new byte[stride * frame.PixelSize.Height];
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { frame.CopyPixels(new PixelRect(frame.PixelSize), pin.AddrOfPinnedObject(), px.Length, stride); }
            finally { pin.Free(); }
            int Brightest(int y0, int y1)
            {
                var max = 0;
                for (var y = y0; y < y1; y++)
                    for (var x = (int)headingAt.X; x < (int)headingAt.X + 50; x++) // "La", clear of the island's buttons
                        max = Math.Max(max, px[y * stride + x * 4 + 1]);
                return max;
            }

            var glyphBottom = (int)(headingAt.Y + heading.Bounds.Height);
            Assert.Equal(255, Brightest((int)headingAt.Y, (int)pillAt.Y));         // above the pill: untouched
            var underFill = Brightest((int)pillAt.Y + 3, glyphBottom);               // inside the outline ring
            // 255 x (1 - 0.398) + 0x20 x 0.398 = 166: the fill is over the heading.
            Assert.InRange(underFill, 150, 185);
        }
        finally { win.Close(); }
    }
}
