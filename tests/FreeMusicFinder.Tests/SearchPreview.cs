using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>Explicit isolated design preview: public metadata/artwork and synthetic empty
/// local library. No real Noctis instance, settings, session, bot or music files.</summary>
internal static class SearchPreview
{
    public static void Run(string queries)
    {
        var cache = Environment.GetEnvironmentVariable("FMF_DISCOVERY_CACHE") ?? throw new ArgumentException("Use an isolated metadata cache.");
        var output = Environment.GetEnvironmentVariable("FMF_PICTURES") ?? throw new ArgumentException("Set a preview output folder."); Directory.CreateDirectory(output);
        using var catalog = new ArtistCatalog(cache, PlaylistCatalog.MetadataHandler(""));
        var folder = Check.NewFolder("search-preview"); var host = new WindowTests.FakeHost(folder, folder);
        using var account = new TelegramAccount(Path.Combine(folder, "unused.dat"), () => ""); using var downloads = new Downloads(_ => { }); downloads.SetPaused(true);
        var library = new SpotifyPlaylistTests.Library();
        var actions = new DiscoveryActions(library, downloads, _ => throw new InvalidOperationException("No downloads in design preview."), () => folder, () => false);
        using var view = new DiscoveryView(() => catalog, _ => { }, null, actions, (_, _) => Task.CompletedTask);
        var window = new SearchWindow(host, account, downloads, (_, _) => throw new InvalidOperationException("No bot requests in design preview."), discovery: view);
        window.Show(); int index = 0;
        foreach (var query in queries.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            window.Search(query);
            Wait(() => !view.GetVisualDescendants().OfType<ProgressBar>().Any(p => p.IsVisible), TimeSpan.FromSeconds(95));
            Wait(() => view.GetVisualDescendants().OfType<Image>().Count(i => i.Source is not null) >= 4, TimeSpan.FromSeconds(18));
            if (index == 0) Wait(() => view.GetVisualDescendants().OfType<ArtistPicture>().FirstOrDefault()?.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null) == true, TimeSpan.FromSeconds(12));
            Save($"{++index:00}-search-dark");
            if (index > 1)
            {
                view.SelectFilter("Artists");
                Wait(() => view.GetVisualDescendants().OfType<ArtistPicture>().FirstOrDefault()?.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null) == true, TimeSpan.FromSeconds(12));
                Save($"{index:00}-artists-context"); view.SelectFilter("All");
            }
            if (index == 1)
            {
                window.Width = 480; Save("02-search-narrow"); window.Width = 1020;
                window.RequestedThemeVariant = ThemeVariant.Light; Save("03-search-light"); window.RequestedThemeVariant = ThemeVariant.Dark;
            }
        }
        Check.Equal(0, downloads.History.Count, "design previews start no downloads"); window.Close();
        void Save(string name)
        {
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var file = File.Create(Path.Combine(output, name + ".png")); window.CaptureRenderedFrame()?.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Console.WriteLine("PREVIEW " + name);
        }
    }
    private static void Wait(Func<bool> condition, TimeSpan limit)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew(); while (!condition() && timer.Elapsed < limit) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        Dispatcher.UIThread.RunJobs();
    }
}
