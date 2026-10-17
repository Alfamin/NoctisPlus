using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Choose Artwork — From Apple Music": Max gave a 300x300 cover while Standard gave 1000x1000
/// (aaron, 10-17). Max asked for the uncompressed original on a5.mzstatic.com, which Apple's CDN
/// now answers 403, and the pick then fell straight to the 300 px thumbnail. The CDN also
/// rejects the old 100000x100000-999 box with 400; 10000 is the largest it accepts.
/// </summary>
public class AppleArtworkMaxResolutionTests
{
    // Recorded 2026-10-17 from https://itunes.apple.com/lookup?id=1713845538&country=us (trimmed).
    private const string LookupJson = """
        {"resultCount":1,"results":[{"wrapperType":"collection","collectionType":"Album",
        "collectionId":1713845538,"artistName":"Taylor Swift","collectionName":"1989 (Taylor's Version) [Deluxe]",
        "collectionViewUrl":"https://music.apple.com/us/album/1989-taylors-version-deluxe/1713845538?uo=4",
        "artworkUrl100":"https://is1-ssl.mzstatic.com/image/thumb/Music116/v4/8e/35/6c/8e356cc2-0be4-b83b-d29e-b578623df2ac/23UM1IM34052.rgb.jpg/100x100bb.jpg"}]}
        """;

    private const string Asset =
        "https://is1-ssl.mzstatic.com/image/thumb/Music116/v4/8e/35/6c/8e356cc2-0be4-b83b-d29e-b578623df2ac/23UM1IM34052.rgb.jpg";

    private static async Task<ITunesArtworkService.ArtworkCandidate> LookupAsync(Func<string, HttpResponseMessage> cdn)
    {
        var service = new ITunesArtworkService(new HttpClient(new StubHandler(cdn)));
        var candidate = await service.LookupAlbumByIdAsync(1713845538);
        Assert.NotNull(candidate);
        return candidate!;
    }

    [Fact]
    public async Task MaxAsksForTheLargestBoxTheCdnAccepts()
    {
        var c = await LookupAsync(Cdn(_ => null));

        Assert.Equal(Asset + "/300x300bb.jpg", c.ThumbUrl);
        Assert.Equal(Asset + "/1000x1000bb.jpg", c.StandardUrl);
        Assert.Equal(Asset + "/10000x10000-999.jpg", c.HiResUrl);
        Assert.Equal(
            new[] { Asset + "/10000x10000-999.jpg", Asset + "/10000x10000bb.jpg", Asset + "/1000x1000bb.jpg", Asset + "/300x300bb.jpg" },
            c.DownloadUrls(highestResolution: true));
        Assert.Equal(new[] { Asset + "/1000x1000bb.jpg", Asset + "/300x300bb.jpg" }, c.DownloadUrls(highestResolution: false));
    }

    [AvaloniaFact]
    public async Task MaxPickGetsTheMasterNotTheThumbnail()
    {
        // The CDN as it answered on 2026-10-17; the image sizes stand in for 3000/1000/300.
        var vm = await PickAsync(highest: true, Cdn(path => path switch
        {
            "10000x10000-999.jpg" => 30,
            "1000x1000bb.jpg" => 10,
            "300x300bb.jpg" => 3,
            _ => null,
        }));

        Assert.Equal(30, PickedWidth(vm));
    }

    [AvaloniaFact]
    public async Task MaxThatCannotBeFetchedFallsToStandardNotTheThumbnail()
    {
        var vm = await PickAsync(highest: true, Cdn(path => path switch
        {
            "1000x1000bb.jpg" => 10,
            "300x300bb.jpg" => 3,
            _ => null,
        }));

        Assert.Equal(10, PickedWidth(vm));
    }

    [AvaloniaFact]
    public async Task StandardPickStillGetsStandard()
    {
        var vm = await PickAsync(highest: false, Cdn(path => path switch
        {
            "10000x10000-999.jpg" => 30,
            "1000x1000bb.jpg" => 10,
            "300x300bb.jpg" => 3,
            _ => null,
        }));

        Assert.Equal(10, PickedWidth(vm));
    }

    // The picked cover's bytes, decoded: the headless renderer's own bitmaps report 1x1.
    private static int PickedWidth(MetadataViewModel vm)
    {
        var data = (byte[]?)typeof(MetadataViewModel).GetField("_newArtworkData",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm);
        Assert.NotNull(data);
        using var bmp = SKBitmap.Decode(data);
        return bmp.Width;
    }

    private static async Task<MetadataViewModel> PickAsync(bool highest, Func<string, HttpResponseMessage> cdn)
    {
        var http = new HttpClient(new StubHandler(cdn));
        var service = new ITunesArtworkService(http);
        var candidate = await service.LookupAlbumByIdAsync(1713845538);
        var track = new Track { Title = "x", Artist = "Taylor Swift", Album = "1989", FilePath = "C:\\nope.mp3" };
        var vm = new MetadataViewModel(track, new MetadataService(), new FakeLibraryService(), new TestPersistenceService(),
            new FakeAnimatedCoverService(), itunes: service);
        var result = new ArtworkSearchResult(candidate!, null);
        if (highest) await vm.SelectHighestArtworkResultCommand.ExecuteAsync(result);
        else await vm.SelectStandardArtworkResultCommand.ExecuteAsync(result);
        return vm;
    }

    // Lookup answers the recorded JSON; on the artwork CDN, the uncompressed-original host answers
    // 403 and a box over 10000 answers 400 (both as recorded), and a size the map names answers a
    // PNG of that width.
    private static Func<string, HttpResponseMessage> Cdn(Func<string, int?> widthFor) => url =>
    {
        if (url.StartsWith("https://itunes.apple.com/lookup", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LookupJson, Encoding.UTF8, "application/json") };
        if (url.StartsWith("https://a5.mzstatic.com/", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        if (url.StartsWith(Asset + "/", StringComparison.Ordinal))
        {
            var path = url[(Asset.Length + 1)..];
            if (path.StartsWith("100000x100000", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            if (widthFor(path) is { } w)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png(w)) };
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    };

    private static byte[] Png(int size)
    {
        using var bmp = new SKBitmap(size, size);
        bmp.Erase(SKColors.Teal);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class StubHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request.RequestUri!.AbsoluteUri));
    }
}
