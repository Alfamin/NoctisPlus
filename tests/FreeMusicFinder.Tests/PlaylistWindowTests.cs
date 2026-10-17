using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class PlaylistWindowTests
{
    public static void Run(string? pictures)
    {
        Check.Section("playlist screen: batch paste, missing queue, no-login handling and narrow layout");
        var folder = Check.NewFolder("playlist-screen"); var library = new SpotifyPlaylistTests.Library();
        library.Add(folder, "Song 0"); library.Add(Path.Combine(folder, "second/sub"), "Song 2");
        var sources = new MusicSources(new TransferTests.Source("DeezLoad"), new TransferTests.Source("Music Hunters"), _ => { });
        using var downloads = new Downloads(_ => { });
        using var imports = new SpotifyPlaylists(Path.Combine(folder, "imports.json"), library, downloads, sources.Track, new PlaylistCatalog(new SpotifyPlaylistTests.Http()));
        var view = new PlaylistView(imports, downloads, () => folder, () => false);
        var window = new Window { Title = "Free Music Finder · Playlists", Content = view, Width = 760, Height = 680, MinWidth = 480 };
        FinderTheme.Apply(window);
        window.Show();
        Field<TextBox>(view, "_links").Text = SpotifyPlaylistTests.Url(1) + "\n\n" + SpotifyPlaylistTests.Url(2);
        Click(Field<Button>(view, "_create"));
        Check.Until(() => imports.Jobs.Count == 2 && imports.Jobs.All(j => j.Created) && !imports.Busy, "multi-playlist paste creates both playlists from screen");
        Check.Until(() => Field<Button>(view, "_queue").IsEnabled, "queue button appears after matching");
        Check.True(Field<TextBlock>(view, "_details").Text!.Contains("2/4"), "exact found/total summary visible");
        // One playlist's missing songs: picked with its "Missing songs" button, queued with "Queue these songs".
        Button Show(int playlist) => view.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, "Missing songs")).ElementAt(playlist);
        bool Marked(int playlist) => Show(playlist).GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("finder-card")).Classes.Contains("finder-selected");
        Check.True(Marked(0) && !Marked(1), "the playlist whose missing songs are listed is marked");
        Click(Show(1));
        Check.Until(() => Field<TextBlock>(view, "_details").Text!.Contains("1/3"), "Missing songs on another playlist lists that playlist's");
        Check.True(!Marked(0) && Marked(1), "and marks it instead");
        // A click anywhere on a playlist's card does the same, sent as a real press and release of the mouse.
        void Tap(Control control, double x, double y)
        {
            Dispatcher.UIThread.RunJobs();
            var at = control.TranslatePoint(new Point(x, y), window)!.Value;
            window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
        Border Card(int playlist) => Show(playlist).GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("finder-card"));
        Tap(Card(0), 24, 24);
        Check.Until(() => Field<TextBlock>(view, "_details").Text!.Contains("2/4") && Marked(0) && !Marked(1), "a click on a playlist's name, not only on its button, lists its missing songs");
        Tap(Show(1), 10, 10);
        Check.Until(() => Field<TextBlock>(view, "_details").Text!.Contains("1/3") && Marked(1), "the Missing songs button still does, pressed with the mouse");
        Tap(Card(0), Card(0).Bounds.Width / 2, Card(0).Bounds.Height - 6);
        Check.Until(() => Field<TextBlock>(view, "_details").Text!.Contains("2/4") && Field<Button>(view, "_queueOne") is { IsVisible: true, IsEnabled: true }, "a click on the card's empty part does too; its own queue button is ready");
        Check.Equal(2, imports.Jobs.Count, "clicking around the cards removed no import");
        Click(Field<Button>(view, "_queueOne"));
        Check.Until(() => downloads.Active.Count == 1 && !imports.Busy, "Queue these songs queues the picked playlist's missing song and no other playlist's");
        Check.Until(() => Field<TextBlock>(view, "_status").Text!.Contains("from \"Feeling Shit\""), "the status names the playlist that was queued");
        Click(Field<Button>(view, "_queue"));
        Check.Until(() => downloads.Active.Count == 2, "screen queues only unique missing songs");
        Check.Until(() => Field<Button>(view, "_start").IsEnabled, "Start queued downloads is visible and enabled");
        Check.True(downloads.Active.All(d => d.Held), "screen does not start staged downloads automatically");
        Click(Field<Button>(view, "_start"));
        Check.True(Field<TextBlock>(view, "_status").Text!.Contains("Log in to Telegram"), "start explains login requirement without disturbing import");
        Check.True(downloads.Active.All(d => d.Held), "logged-out start keeps staging hold");
        if (!string.IsNullOrWhiteSpace(pictures))
        {
            Save(window, pictures, "11 Spotify playlists"); window.Width = 480; Save(window, pictures, "12 narrow Spotify playlists");
            Check.True(Field<Button>(view, "_start").Bounds.Right <= view.Bounds.Width, "start button fits minimum width");
            window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Save(window, pictures, "13 light Spotify playlists");
        }
        var before = downloads.Active.Count;
        var remove = view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Remove import"));
        Click(remove);
        Check.Until(() => imports.Jobs.Count == 1, "remove import is available from screen");
        Check.Equal(2, library.Lists.Count, "removing an import keeps native playlists");
        Check.Equal(before, downloads.Active.Count, "removing an import keeps explicit queued requests");
        // Another tab open (the view out of the window): changes do not redraw it, showing it again does.
        window.Content = null; Dispatcher.UIThread.RunJobs();
        imports.RemoveAsync(imports.Jobs.Single()).GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
        Check.Equal(1, Field<StackPanel>(view, "_lists").Children.Count, "a hidden Playlists tab is not redrawn on every change");
        window.Content = view;
        Check.Until(() => Field<StackPanel>(view, "_lists").Children.Count == 0, "the Playlists tab is up to date when it is shown again");
        window.Close(); view.Dispose();
    }
    private static void Save(Window window, string folder, string name)
    {
        Directory.CreateDirectory(folder); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        window.CaptureRenderedFrame()?.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
