using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Edit Info → Animated Artwork → Search, the chooser grid, through the view model against
/// stubbed Apple responses shaped like the live ones (probed 2026-10-18). Before: the search
/// stopped at the first album with a loop and the pop-up showed that one album, labelled with
/// the edited track's own album/artist. "Short n' Sweet" has three editions with motion artwork
/// (1750307020 explicit, 1752214909 clean, 1795512297 deluxe); only the deluxe was offered.
/// </summary>
public class AnimatedArtworkChooserTests
{
    private const string Mvod = "https://mvod.itunes.apple.com/itunes-assets/HLSVideo211/v4";

    private static string Master(string name, bool tall = false)
    {
        var (s1, s2, s3) = tall ? ("486x648", "1080x1440", "2048x2732") : ("360x360", "1080x1080", "2160x2160");
        return "#EXTM3U\n" +
               $"#EXT-X-STREAM-INF:BANDWIDTH=304436,CODECS=\"avc1.64001f\",RESOLUTION={s1}\n{Mvod}/{name}_small.m3u8\n" +
               $"#EXT-X-STREAM-INF:BANDWIDTH=7712180,CODECS=\"avc1.640020\",RESOLUTION={s2}\n{Mvod}/{name}_1080.m3u8\n" +
               $"#EXT-X-STREAM-INF:BANDWIDTH=27902160,CODECS=\"hvc1.2.20000000.H150.B0\",RESOLUTION={s3}\n{Mvod}/{name}_2160.m3u8\n";
    }

    private static string AlbumPage(string title, string artist, string? square, string? tall = null)
    {
        string Motion(string kind, string? name) => name == null
            ? "null"
            : $"{{\"dictionary\":{{\"motionDetail{kind}\":{{\"previewFrame\":{{\"height\":3840,\"url\":\"https://is1-ssl.mzstatic.com/image/thumb/Video/{name}.png/{{w}}x{{h}}bb.{{f}}\",\"width\":3840}},\"video\":\"{Mvod}/{name}_default.m3u8\"}}}}}}";
        return $"<meta name=\"apple:title\" content=\"{WebUtility.HtmlEncode(title)}\">\n" +
               $"<script>{{\"title\":\"{title}\",\"subtitleLinks\":[{{\"title\":\"{artist}\"}}]," +
               $"\"videoArtwork\":{Motion("Square", square)},\"tallVideoArtwork\":{Motion("Tall", tall)}}}</script>";
    }

    private static string Lookup(params (long id, string title, string artist, string explicitness)[] albums)
        => "{\"resultCount\":" + albums.Length + ",\"results\":[" + string.Join(",", albums.Select(a =>
            $"{{\"wrapperType\":\"collection\",\"collectionId\":{a.id},\"collectionName\":\"{a.title}\",\"artistName\":\"{a.artist}\"," +
            $"\"collectionExplicitness\":\"{a.explicitness}\",\"releaseDate\":\"2024-08-23T07:00:00Z\"," +
            $"\"collectionViewUrl\":\"https://music.apple.com/us/album/x/{a.id}?uo=4\"," +
            $"\"artworkUrl100\":\"https://is1-ssl.mzstatic.com/image/thumb/Music/{a.id}.jpg/100x100bb.jpg\"}}")) + "]}";

    /// <summary>The Apple surfaces a "Short n' Sweet" search touches; anything else 404s.</summary>
    internal sealed class Apple
    {
        public readonly ConcurrentQueue<string> Requests = new();
        public Func<string, Task>? Gate;

        public async Task<HttpResponseMessage> Respond(HttpRequestMessage request)
        {
            var url = request.RequestUri!.ToString();
            Requests.Enqueue(url);
            if (Gate != null) await Gate(url);

            string? body = null;
            if (url.StartsWith("https://music.apple.com/us/search", StringComparison.Ordinal))
            {
                var term = Uri.UnescapeDataString(request.RequestUri.Query);
                if (term.Contains("Short n", StringComparison.OrdinalIgnoreCase))
                    body = """
                        <a href="https://music.apple.com/us/album/short-n-sweet-deluxe/1795512297">
                        <a href="https://music.apple.com/us/album/taste/1795512297?i=1795512308">
                        <a href="https://music.apple.com/us/album/short-n-sweet/1750307020">
                        <a href="https://music.apple.com/us/album/espresso-single/1740212432">
                        <a href="https://music.apple.com/us/album/short-n-sweet/1752214909">
                        """;
            }
            else if (url.StartsWith("https://itunes.apple.com/lookup", StringComparison.Ordinal))
            {
                if (url.Contains("1795512297,1750307020", StringComparison.Ordinal))
                    body = Lookup(
                        (1795512297, "Short n' Sweet (Deluxe)", "Sabrina Carpenter", "explicit"),
                        (1750307020, "Short n' Sweet", "Sabrina Carpenter", "explicit"),
                        (1740212432, "Espresso - Single", "Sabrina Carpenter", "notExplicit"),
                        (1752214909, "Short n' Sweet", "Sabrina Carpenter", "cleaned"));
                else if (url.Contains("id=1795512297", StringComparison.Ordinal))
                    body = Lookup((1795512297, "Short n' Sweet (Deluxe)", "Sabrina Carpenter", "explicit"));
            }
            else if (url.StartsWith("https://music.apple.com/us/album/x/1795512297", StringComparison.Ordinal) ||
                     url.StartsWith("https://music.apple.com/gb/album/short-n-sweet-deluxe/1795512297", StringComparison.Ordinal))
                body = AlbumPage("Short n' Sweet (Deluxe)", "Sabrina Carpenter", "deluxe", "deluxetall");
            else if (url.StartsWith("https://music.apple.com/us/album/x/1750307020", StringComparison.Ordinal))
                body = AlbumPage("Short n' Sweet", "Sabrina Carpenter", "explicit");
            else if (url.StartsWith("https://music.apple.com/us/album/x/1752214909", StringComparison.Ordinal))
                body = AlbumPage("Short n' Sweet", "Sabrina Carpenter", "clean");
            else if (url.StartsWith("https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb", StringComparison.Ordinal))
                body = AlbumPage("Today’s Hits", "Apple Music Hits", null, "playlisttall");
            else if (url.StartsWith(Mvod, StringComparison.Ordinal) && url.EndsWith("_default.m3u8", StringComparison.Ordinal))
            {
                var name = url[(Mvod.Length + 1)..^"_default.m3u8".Length];
                body = Master(name, tall: name.EndsWith("tall", StringComparison.Ordinal));
            }
            else if (url.StartsWith(Mvod, StringComparison.Ordinal) && url.EndsWith(".m3u8", StringComparison.Ordinal))
                body = $"#EXTM3U\n#EXT-X-MAP:URI=\"{url[..^5]}_init.mp4\"\n#EXTINF:10,\n{url[..^5]}_seg.mp4\n#EXT-X-ENDLIST\n";
            else if (url.StartsWith(Mvod, StringComparison.Ordinal) && url.EndsWith(".mp4", StringComparison.Ordinal))
                body = "fake-mp4-bytes";

            return body == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
        }
    }

    private static MetadataViewModel Vm(Apple apple, string album = "Short n' Sweet", string artist = "Sabrina Carpenter")
    {
        var http = new HttpClient(new StubHandler(apple.Respond));
        var track = new Track { Title = "Taste", Artist = artist, AlbumArtist = artist, Album = album, FilePath = "C:\\nope.mp3" };
        return new MetadataViewModel(track, new MetadataService(), new FakeLibraryService(), new TestPersistenceService(),
            new FakeAnimatedCoverService(), itunes: new ITunesArtworkService(http));
    }

    [Fact]
    public async Task TagSearch_OffersEveryEditionWithALoop_BestMatchFirst()
    {
        var apple = new Apple();
        var vm = Vm(apple);

        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);

        Assert.True(vm.HasAnimatedArtworkSearchResults, vm.AnimatedSearchStatus);
        var ids = vm.AnimatedArtworkSearchResults.Select(r => r.Album.CollectionId).ToList();
        // Both exact-title editions (in the store's order), then the deluxe. The single has no
        // loop and a different title: never opened, never offered.
        Assert.Equal(new long[] { 1750307020, 1752214909, 1795512297 }, ids);
        Assert.DoesNotContain(apple.Requests, u => u.Contains("/album/x/1740212432", StringComparison.Ordinal));

        // Each card names the album it found, not the edited track's tags.
        var deluxe = vm.AnimatedArtworkSearchResults[2];
        Assert.Equal("Short n' Sweet (Deluxe)", deluxe.CollectionName);
        Assert.Equal("Sabrina Carpenter", deluxe.ArtistName);
        // The clean edition says so: same title, different card.
        Assert.Contains("Clean", vm.AnimatedArtworkSearchResults[1].Detail);
        Assert.Contains("2024", vm.AnimatedArtworkSearchResults[1].Detail);

        // Quality buttons, largest last and accented; the still of the loop for the poster.
        Assert.All(vm.AnimatedArtworkSearchResults, r =>
        {
            Assert.Equal(new[] { "1080p", "2160p" }, r.Choices.Select(c => c.Label));
            Assert.True(r.Choices[^1].IsPrimary);
            Assert.False(r.Album.IsTallOnly);
            Assert.EndsWith("300x300bb.jpg", r.Album.PosterUrl);
            // Previews use the smallest variant: a few hundred kB, not the 2160p loop.
            Assert.EndsWith("_small.m3u8", r.Album.PreviewVariant!.Url);
        });
        Assert.True(deluxe.Album.HasTall);
        Assert.Equal(string.Empty, vm.AnimatedSearchStatus);
        Assert.Equal("Short n' Sweet — Sabrina Carpenter", vm.AnimatedSearchQueryHint);
    }

    [Fact]
    public async Task PickingAQuality_DownloadsThatVariantAndAppliesIt()
    {
        var apple = new Apple();
        var vm = Vm(apple);
        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);
        var choice = vm.AnimatedArtworkSearchResults[0].Choices.Single(c => c.Label == "2160p");

        await vm.SelectAnimatedArtworkResultCommand.ExecuteAsync(choice);

        Assert.True(vm.HasAnimatedCover, vm.AnimatedSearchStatus);
        Assert.False(vm.IsAnimatedArtworkSearchOpen);
        Assert.Equal("Applied. Click Save to keep.", vm.AnimatedSearchStatus);
        Assert.Equal("fake-mp4-bytesfake-mp4-bytes", File.ReadAllText(vm.AnimatedCoverPath!));
        Assert.Contains(apple.Requests, u => u.EndsWith("/explicit_2160.m3u8", StringComparison.Ordinal));
        File.Delete(vm.AnimatedCoverPath!);
    }

    [Fact]
    public async Task PreviewingACard_PlaysItsSmallestVariant_OneCardAtATime()
    {
        var apple = new Apple();
        var vm = Vm(apple);
        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);
        var first = vm.AnimatedArtworkSearchResults[0];
        var second = vm.AnimatedArtworkSearchResults[1];
        // The best match previews on its own once the search ends.
        Assert.True(SpinUntil(() => first.IsPreviewing), "the top card never previewed");

        await vm.PreviewAnimatedArtworkResultCommand.ExecuteAsync(second);

        Assert.True(second.IsPreviewing);
        Assert.False(first.IsPreviewing);
        Assert.Equal(second.PreviewPath, vm.AnimatedPreviewPath);
        Assert.Contains(apple.Requests, u => u.EndsWith("/clean_small.m3u8", StringComparison.Ordinal));

        // Closing the chooser stops the preview and drops its temp file.
        var path = second.PreviewPath!;
        vm.IsAnimatedArtworkSearchOpen = false;
        Assert.Null(second.PreviewPath);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ANewSearch_ReplacesOneStillRunning()
    {
        // The chooser's box allows searching again mid-flight. The old command ignored a second
        // search while one ran; the first one's late answers must not land in the new grid.
        var apple = new Apple();
        var release = new TaskCompletionSource();
        apple.Gate = url => url.Contains("short-n", StringComparison.Ordinal) || url.Contains("Short%20n", StringComparison.Ordinal)
            ? release.Task
            : Task.CompletedTask;
        var vm = Vm(apple);

        var first = vm.SearchAnimatedCoverCommand.ExecuteAsync(null);
        vm.AnimatedSearchInput = "https://music.apple.com/gb/album/short-n-sweet-deluxe/1795512297";
        apple.Gate = null;
        var second = vm.SearchAnimatedCoverCommand.ExecuteAsync(null);
        await second;
        release.SetResult();
        await first;

        var only = Assert.Single(vm.AnimatedArtworkSearchResults);
        Assert.Equal(1795512297, only.Album.CollectionId);
        Assert.False(vm.IsSearchingAnimatedCover);
    }

    [Fact]
    public async Task APastedLink_OpensThatAlbumInItsOwnStorefront()
    {
        var apple = new Apple();
        var vm = Vm(apple, album: "Unrelated", artist: "Someone Else");
        vm.AnimatedSearchInput = "https://music.apple.com/gb/album/short-n-sweet-deluxe/1795512297?i=1795512308";

        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);

        var only = Assert.Single(vm.AnimatedArtworkSearchResults);
        Assert.Equal("Short n' Sweet (Deluxe)", only.CollectionName);
        Assert.Contains(apple.Requests, u => u.StartsWith("https://itunes.apple.com/lookup?id=1795512297&country=gb", StringComparison.Ordinal));
        Assert.Contains(apple.Requests, u => u == "https://music.apple.com/gb/album/short-n-sweet-deluxe/1795512297");
        Assert.DoesNotContain(apple.Requests, u => u.Contains("/search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APastedPlaylistLink_OffersItsLoop_TallOnlyIsMarkedPortrait()
    {
        var apple = new Apple();
        var vm = Vm(apple);
        vm.AnimatedSearchInput = "https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb";

        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);

        var only = Assert.Single(vm.AnimatedArtworkSearchResults);
        Assert.Equal("Today’s Hits", only.CollectionName);
        Assert.Equal("Apple Music Hits", only.ArtistName);
        Assert.True(only.Album.IsTallOnly);
        Assert.Contains("Portrait", only.Detail);
        Assert.Equal(new[] { "1080p", "2160p" }, only.Choices.Select(c => c.Label));
    }

    [Fact]
    public async Task NothingRelevant_SaysWhy()
    {
        var apple = new Apple();
        var vm = Vm(apple, album: "No Such Album Anywhere", artist: "Nobody");

        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);

        Assert.False(vm.HasAnimatedArtworkSearchResults);
        Assert.Equal("No animated artwork found.", vm.AnimatedSearchStatus);
        Assert.Equal("No matching albums on Apple Music. Try another name, or paste an Apple Music link.", vm.AnimatedSearchDetail);
        Assert.True(vm.IsAnimatedArtworkSearchOpen);
        Assert.False(vm.IsSearchingAnimatedCover);
    }

    private static bool SpinUntil(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    internal sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }
}
