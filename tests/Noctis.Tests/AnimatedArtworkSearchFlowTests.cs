using System.Net;
using System.Text;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Edit Info → Animated Artwork → Search, end to end through the view model, against Apple
/// responses saved on 2026-10-13 (Fixtures/AppleMusic, trimmed, no tokens — none are used).
///
/// The reported miss: typing "HIT ME HARD AND SOFT Billie Eilish" and "Die With A Smile Lady
/// Gaga" said "No animated artwork found." Both are right — Apple has no motion artwork for
/// either (the album page carries "videoArtwork":null, and the catalog API returns no
/// editorialVideo in us/gb/jp) — but the same query shape failed for albums that DO have one:
/// "Happier Than Ever Billie Eilish", "YHLQMDLG Bad Bunny". The box was read as an album title,
/// so "<album> <artist>" never equalled the store's album name and every candidate was rejected
/// before its page was even opened.
/// </summary>
public class AnimatedArtworkSearchFlowTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AppleMusic", name));

    // Routes each Apple endpoint the search touches to its saved response; anything else 404s.
    private static HttpResponseMessage Route(HttpRequestMessage request)
    {
        var url = request.RequestUri!.ToString();
        string? body = null;
        if (url.StartsWith("https://music.apple.com/us/search", StringComparison.Ordinal))
        {
            var term = Uri.UnescapeDataString(request.RequestUri.Query);
            if (term.Contains("Happier Than Ever", StringComparison.OrdinalIgnoreCase))
                body = Fixture("search-happier-than-ever-billie-eilish.html");
            else if (term.Contains("HIT ME HARD AND SOFT", StringComparison.OrdinalIgnoreCase))
                body = Fixture("search-hit-me-hard-and-soft-billie-eilish.html");
        }
        else if (url.StartsWith("https://itunes.apple.com/lookup", StringComparison.Ordinal))
        {
            if (url.Contains("1564530719", StringComparison.Ordinal))
                body = Fixture("lookup-happier-than-ever.json");
            else if (url.Contains("1739659134", StringComparison.Ordinal))
                body = Fixture("lookup-hit-me-hard-and-soft.json");
        }
        else if (url.StartsWith("https://music.apple.com/us/album/happier-than-ever/1564530719", StringComparison.Ordinal))
            body = Fixture("album-1564530719-happier-than-ever.html");
        else if (url.StartsWith("https://music.apple.com/us/album/hit-me-hard-and-soft/1739659134", StringComparison.Ordinal))
            body = Fixture("album-1739659134-hit-me-hard-and-soft.html");
        else if (url.EndsWith("/P359424388_default.m3u8", StringComparison.Ordinal))
            body = Fixture("master-P359424388.m3u8");

        return body == null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
    }

    private static async Task<MetadataViewModel> SearchAsync(string typed)
    {
        var http = new HttpClient(new StubHandler(Route));
        var p = new TestPersistenceService();
        // The loaded track is someone else's: a typed query is about the album typed, not the tags.
        var track = new Track { Title = "x", Artist = "Someone Else", Album = "Unrelated", FilePath = "C:\\nope.mp3" };
        var vm = new MetadataViewModel(track, new MetadataService(), new FakeLibraryService(), p,
            new FakeAnimatedCoverService(), itunes: new ITunesArtworkService(http));
        vm.AnimatedSearchInput = typed;
        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);
        return vm;
    }

    [Theory]
    [InlineData("Happier Than Ever")]
    [InlineData("Happier Than Ever Billie Eilish")]
    [InlineData("Billie Eilish Happier Than Ever")]
    [InlineData("Happier Than Ever - Billie Eilish")]
    [InlineData("Happier Than Ever by Billie Eilish")]
    [InlineData("https://music.apple.com/us/album/happier-than-ever/1564530719")]
    [InlineData("1564530719")]
    public async Task FindsTheLoopForAnAlbumThatHasOne(string typed)
    {
        var vm = await SearchAsync(typed);

        Assert.True(vm.HasAnimatedArtworkSearchResults, vm.AnimatedSearchStatus);
        var labels = vm.AnimatedArtworkSearchResults.Select(r => r.Variant.Label).ToList();
        Assert.Contains("1080p", labels);
        Assert.All(vm.AnimatedArtworkSearchResults,
            r => Assert.StartsWith("https://mvod.itunes.apple.com/", r.Variant.Url));
    }

    [Fact]
    public async Task AnAlbumWithoutMotionArtworkSaysSoAndLogsWhy()
    {
        var vm = await SearchAsync("HIT ME HARD AND SOFT Billie Eilish");

        Assert.False(vm.HasAnimatedArtworkSearchResults);
        Assert.Equal("No animated artwork found.", vm.AnimatedSearchStatus);
        // Never silent again: the log says it reached the right album and that Apple has no
        // loop for it — not that the page format moved.
        var log = DebugLog.Snapshot();
        Assert.Contains("1739659134", log);
        Assert.Contains("videoArtwork is null", log);
    }

    [Fact]
    public void FixturePagesParseTheWayTheLiveOnesDid()
    {
        // The saved page carries the loop twice (inline JSON and the rendered element); it is
        // one stream. The no-motion page carries nothing at all — no fallback hit either.
        var urls = ITunesArtworkService.ExtractAnimatedMediaUrls(Fixture("album-1564530719-happier-than-ever.html"));
        Assert.Equal(new[]
        {
            "https://mvod.itunes.apple.com/itunes-assets/HLSMusic126/v4/96/ac/ec/" +
            "96acec0b-6568-4f81-9b0f-adf227ca7c94/P359424388_default.m3u8",
        }, urls);

        Assert.Empty(ITunesArtworkService.ExtractAnimatedMediaUrls(Fixture("album-1739659134-hit-me-hard-and-soft.html")));
        Assert.Equal(1564530719, ITunesArtworkService.ExtractAppleMusicAlbumIds(
            Fixture("search-happier-than-ever-billie-eilish.html"))[0]);
    }

    [Fact]
    public async Task ALatePreviewFromAnEarlierSearchDoesNotTouchTheNextOne()
    {
        // Each search with results starts a background preview download. Nothing tied it to its
        // search, so when it finished after the next search had begun it still wrote to the
        // dialog: its failure replaced the new "No animated artwork found." (and a success
        // would have put the old album's loop in the new card).
        var previewRequested = new TaskCompletionSource();
        var releasePreview = new TaskCompletionSource();
        var previewAnswered = new TaskCompletionSource();
        var http = new HttpClient(new StubHandler(async request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith(".m3u8", StringComparison.Ordinal) && !url.EndsWith("_default.m3u8", StringComparison.Ordinal))
            {
                previewRequested.TrySetResult();
                await releasePreview.Task;
                previewAnswered.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return Route(request);
        }));
        var track = new Track { Title = "x", Artist = "Someone Else", Album = "Unrelated", FilePath = "C:\\nope.mp3" };
        var vm = new MetadataViewModel(track, new MetadataService(), new FakeLibraryService(), new TestPersistenceService(),
            new FakeAnimatedCoverService(), itunes: new ITunesArtworkService(http));

        vm.AnimatedSearchInput = "Happier Than Ever";
        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);
        Assert.True(vm.HasAnimatedArtworkSearchResults);
        await previewRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.AnimatedSearchInput = "HIT ME HARD AND SOFT Billie Eilish";
        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);
        Assert.Equal("No animated artwork found.", vm.AnimatedSearchStatus);

        releasePreview.SetResult();
        await previewAnswered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300); // let the stale preview's continuation run, if it is going to

        Assert.Equal("No animated artwork found.", vm.AnimatedSearchStatus);
        Assert.Null(vm.AnimatedPreviewPath);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this(request => Task.FromResult(respond(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }
}
