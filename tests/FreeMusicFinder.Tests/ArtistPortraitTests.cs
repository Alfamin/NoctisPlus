using System.Net;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class ArtistPortraitTests
{
    private const string Photo = "https://is1-ssl.mzstatic.com/image/thumb/AMCArtistImages/test/1200x630cw.png";
    private static string Page(string id, string photo = Photo) => $"<meta content='https://music.apple.com/us/artist/drake/{id}' property='og:url'><meta property=\"og:image\" content=\"{photo}\">";
    private static HttpResponseMessage Image()
    {
        var data = new ByteArrayContent(DiscoveryTests.Cover); data.Headers.ContentType = new("image/png"); return new(HttpStatusCode.OK) { Content = data };
    }
    public static async Task RunAsync()
    {
        Check.Section("artist portraits: provider identity, safe redirects, cache and unavailable pictures");
        Check.Equal("https://is1-ssl.mzstatic.com/image/thumb/AMCArtistImages/test/160x160bb.jpg", ArtistCatalog.ApplePortrait(Page("271256"), "271256"), "artist portrait becomes a small square thumbnail");
        Check.True(ArtistCatalog.ApplePortrait(Page("1147300457"), "271256") is null, "same-name artist page with a different ID cannot supply the portrait");
        Check.True(ArtistCatalog.ApplePortrait(Page("271256", "https://untrusted.invalid/photo.png"), "271256") is null, "untrusted portrait image URL is never accepted");
        Check.True(ArtistCatalog.ApplePortrait(Page("271256").Replace("music.apple.com", "music.apple.com.untrusted.invalid"), "271256") is null, "artist identity cannot be supplied by a lookalike page host");
        var actual = new CatalogArtist("apple:271256", "Drake");
        var http = new DiscoveryTests.Http { Override = (r, _) =>
        {
            if (ArtistCatalog.ArtworkUrl(r.RequestUri!.AbsoluteUri)) return Task.FromResult(Image());
            if (r.RequestUri.AbsolutePath == "/us/artist/271256") { var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently); redirect.Headers.Location = new("/us/artist/drake/271256", UriKind.Relative); return Task.FromResult(redirect); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page("271256")) });
        } };
        using var catalog = DiscoveryTests.Catalog(Check.NewFolder("portraits"), http);
        var path = await catalog.PortraitAsync(actual, default);
        Check.True(path is not null && File.Exists(path), "Apple portrait is read from its exact artist page through the shared HTTP route");
        Check.Equal(3, http.Calls.Count, "portrait follows one safe artist redirect then requests only its thumbnail");
        Check.Equal(path, await catalog.PortraitAsync(actual, default), "artist portrait reuses cached page metadata and image");
        Check.Equal(3, http.Calls.Count, "repeat portrait performs zero network requests");
        var unsafeHttp = new DiscoveryTests.Http { Override = (_, _) => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://music.apple.com/us/artist/drake/1147300457"); return Task.FromResult(response); } };
        using var unsafeCatalog = DiscoveryTests.Catalog(Check.NewFolder("portrait-redirect"), unsafeHttp);
        Check.True(await unsafeCatalog.PortraitAsync(actual, default) is null && unsafeHttp.Calls.Count == 1, "redirect to a namesake's different ID is refused before following it");
        var deezerHttp = new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(Image()) };
        using var deezer = DiscoveryTests.Catalog(Check.NewFolder("deezer-portrait"), deezerHttp);
        Check.True(await deezer.PortraitAsync(new("deezer:42", "North Star", Picture: DiscoveryTests.CoverUrl), default) is not null && deezerHttp.Calls.Count == 1, "Deezer search-provided portrait needs no extra artist page lookup");
        var missingHttp = new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>No portrait</html>") }) };
        using var missing = DiscoveryTests.Catalog(Check.NewFolder("no-portrait"), missingHttp);
        Check.True(await missing.PortraitAsync(actual, default) is null, "artist with no portrait falls back without failing the artist result");
        await missing.PortraitAsync(actual, default); Check.Equal(1, missingHttp.Calls.Count, "missing portrait is cached instead of repeatedly requesting the page");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => catalog.PortraitAsync(actual, cancelled.Token), "cancelled search stops portrait requests");
    }

    public static void RunUi(string? pictures)
    {
        Check.Section("artist search rows: portraits, library match and namesake labels");
        var actual = new CatalogArtist("apple:271256", "Drake", "Hip-Hop/Rap", DiscoveryTests.CoverUrl);
        var other = new CatalogArtist("apple:1147300457", "Drake", "Dance", DiscoveryTests.CoverUrl);
        var folder = Check.NewFolder("portrait-search-ui");
        ArtistIdentityTests.Seed(folder, "search:DRAKE", new[] { other, actual });
        ArtistIdentityTests.Seed(folder, "artist:" + actual.Id, new CatalogProfile(actual, [], [new("apple:111", "Thank Me Later", actual, "2010", "album", 14)]));
        using var catalog = DiscoveryTests.Catalog(folder, new() { Override = (_, _) => Task.FromResult(Image()) });
        CatalogArtist? opened = null;
        using var view = new DiscoveryView(() => catalog, artist => opened = artist, _ => new[] { "Thank Me Later" });
        var window = new Window { Content = view, Width = 860, Height = 500 }; window.Show(); view.Search("Drake");
        Check.Until(() => view.GetVisualDescendants().OfType<Border>().Any(b => ToolTip.GetTip(b)?.ToString()?.StartsWith("Matches your library") == true), "artist matching uses library evidence and keeps its explanation available on hover");
        view.SelectFilter("Artists");
        Check.Until(() => view.GetVisualDescendants().OfType<Avalonia.Controls.Image>().Count(i => i.Source is not null && i.Clip is Avalonia.Media.EllipseGeometry) == 2, "both artist results show decoded round portraits");
        var labels = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
        Check.True(labels.Contains("Hip-Hop/Rap") && labels.Contains("Dance"), "namesake tiles use their actual genres and pictures instead of explanatory match badges");
        Check.True(!labels.Any(t => t.Contains("apple:") || t.Contains("271256") || t.Contains("1147300457")), "raw provider IDs are absent from visible search rows");
        var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, "Open in Noctis →") || Avalonia.Automation.AutomationProperties.GetName(b)?.Contains("artist in Noctis") == true).ToArray();
        buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check.Equal(actual.Id, opened?.Id, "library-matched artist is the first action, even when provider namesake was first");
        buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check.Equal(other.Id, opened?.Id, "separate namesake remains deliberately selectable");
        if (!string.IsNullOrEmpty(pictures))
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var output = File.Create(Path.Combine(pictures, "40 artist identity search.png")); window.CaptureRenderedFrame()?.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        window.Close();
    }
}
