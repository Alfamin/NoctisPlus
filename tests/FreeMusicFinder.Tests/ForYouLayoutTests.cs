using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FreeMusicFinder.Tests;

/// <summary>The complete Finder window, not a standalone control: buttons must be inside the viewport and hit-testable.</summary>
internal static class ForYouLayoutTests
{
    public static void Run(string? pictures)
    {
        Check.Section("For you: actual Finder viewport and mouse targets");
        var folder = Check.NewFolder("for-you-layout");
        var songs = Enumerable.Range(1, 40).Select(i => new DiscoverSong(new(new("apple:" + (100 + i),
            i == 1 ? "A long song title (with a featured artist and a deluxe recording version)" : "Suggested song " + i,
            [new("apple:1", "Synthetic Recommendation Artist")], 200, true), null), 1, "Because you play Your Favourite Artist With A Long Name")).ToList();
        var now = DateTimeOffset.UtcNow;
        new ForYouCache(Path.Combine(folder, "for-you-last.json")).Write(new(1, now, now.AddDays(1), new(songs, [], "", true)));
        bool stall = false; int cancelled = 0; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new DiscoveryTests.Http { Override = async (_, ct) =>
        {
            if (stall)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            }
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        } };
        using var catalog = DiscoveryTests.Catalog(Path.Combine(folder, "catalog"), http);
        File.WriteAllText(Path.Combine(folder, "play_history.json"), System.Text.Json.JsonSerializer.Serialize(new[]
            { new { Artist = "Synthetic Recommendation Artist", Title = "A play", PlayedAtUtc = now.UtcDateTime, Skipped = false } }));
        var library = new SpotifyPlaylistTests.Library(); int fetched = 0;
        BotTrack Track(MusicRequest r) => new(r.Title, r.Artist, r.Duration, "test-" + r.Title, async (output, _, ct) =>
        { fetched++; await output.WriteAsync(new byte[] { 1 }, ct); return "mp3"; }) { Request = r };
        using var downloads = new Downloads(_ => { }); downloads.SetPaused(true);
        var actions = new DiscoveryActions(library, downloads, Track, () => Path.Combine(folder, "downloads"), () => true);
        using var source = new ForYouSource(() => catalog, actions, _ => [], library, folder, Path.Combine(folder, "hidden.json"));
        using var view = new DiscoveryView(() => catalog, _ => { }, actions: actions, forYou: source, undoDuration: TimeSpan.FromSeconds(2));
        var host = new WindowTests.FakeHost(folder, folder);
        using var telegram = new TelegramAccount(Path.Combine(folder, "unused.dat"), () => "");
        var window = new SearchWindow(host, telegram, downloads, (_, _) => throw new Exception("No bot in layout tests"), discovery: view);
        window.Show(); Check.Until(() => Text(view).Contains("Songs for you") && Text(view).Any(t => t.StartsWith("40 songs")), "saved For you loads inside the complete Finder window");
        var first = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("finder-song"));
        Button Find(string label) => first.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, label));
        foreach (var width in new[] { 1020, 700, 480, 1020 })
        {
            window.Width = width; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            foreach (var label in new[] { "Download now", "Queue download", "Not for me" })
            {
                var button = Find(label); var p = button.TranslatePoint(default, window);
                Check.True(OnScreen(button, window), $"{width}px: {label} is visible inside the window (x={p?.X:0}, width={button.Bounds.Width:0})");
            }
            var refresh = view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Refresh"));
            Check.True(OnScreen(refresh, window), $"{width}px: Refresh is visible inside the window");
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            Check.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"{width}px: the main page has no horizontal overflow");
            if (pictures is not null && width is 480 or 1020)
            {
                Directory.CreateDirectory(pictures); using var output = File.Create(Path.Combine(pictures, $"60-for-you-{width}.png"));
                window.CaptureRenderedFrame()?.Save(output, PngBitmapEncoderOptions.Default);
            }
        }
        var queue = Find("Queue download"); var at = queue.TranslatePoint(new Point(queue.Bounds.Width / 2, queue.Bounds.Height / 2), window)!.Value;
        var target = window.InputHitTest(at) as Visual;
        Check.True(ReferenceEquals(target, queue) || target?.GetVisualAncestors().Contains(queue) == true, "the visible Queue download position hits the button, not a clipped/off-screen area");
        if (OnScreen(queue, window)) window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        if (OnScreen(queue, window)) window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Check.Until(() => downloads.Active.Count == 1, "a real pointer click on For you queues exactly the selected song");
        Check.Equal(0, fetched, "queueing does not download while the user's queue is paused");
        window.RequestedThemeVariant = ThemeVariant.Light; Dispatcher.UIThread.RunJobs();
        Check.True(OnScreen(Find("Download now"), window), "download controls also remain in view in the light theme");
        first.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Not for me")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var undo = view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Undo"));
        Check.True(OnScreen(undo, window) && !Text(view).Contains(songs[0].Track.Song.Title), "dismissal hides only the chosen song and places Undo visibly in the fixed header");
        Check.True(Text(view).Contains("Suggested song 2") && !source.Hidden.HidesSong(songs[1].Track.Song), "other songs by the same artist remain available");
        stall = true;
        view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Refresh")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => entered.Task.IsCompleted, "a refresh can be in flight while Undo is offered");
        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => Text(view).Contains(songs[0].Track.Song.Title), "Undo restores the dismissed song at once");
        Check.Until(() => cancelled > 0, "Undo cancels an older refresh so it cannot erase the restored row"); stall = false;
        Check.False(new DiscoverHidden(Path.Combine(folder, "hidden.json")).HidesSong(songs[0].Track.Song), "Undo removes the persistent song preference as well");
        view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Not for me")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => !undo.IsEffectivelyVisible, "Undo disappears when its short window expires");
        Check.True(source.Hidden.HidesSong(songs[0].Track.Song), "expiry keeps the song dismissed");
        window.Close();
    }

    private static bool OnScreen(Control button, Window window) => button.IsEffectivelyVisible && button.Bounds.Width > 0 && button.Bounds.Height > 0 &&
        button.TranslatePoint(default, window) is { } p && p.X >= 0 && p.Y >= 0 &&
        p.X + button.Bounds.Width <= window.ClientSize.Width + 1 && p.Y + button.Bounds.Height <= window.ClientSize.Height + 1;
    private static List<string> Text(Control view) => view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
}
