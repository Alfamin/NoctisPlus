using System.Net;
using System.Text;
using FreeMusicFinder;
using Xunit;

namespace Noctis.Tests;

public sealed class SonicPreviewTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(reply(request)); }
    }
    private static CatalogSong Song => new("deezer:1", "Title", [new("deezer:2", "Artist")], 200, true);
    private static HttpResponseMessage Json(string preview, int id = 1) => new(HttpStatusCode.OK)
    { Content = new StringContent($$"""{"id":{{id}},"preview":"{{preview}}"}""", Encoding.UTF8, "application/json") };

    [Fact] public async Task ExpiredUrlIsRefetchedOnlyOnceAndAudioNeverGoesToDisk()
    {
        int details = 0, audio = 0;
        var http = new Handler(r =>
        {
            if (r.RequestUri!.Host == "api.deezer.com") { details++; return Json("https://p.dzcdn.net/song?expiry=" + details); }
            audio++;
            return audio == 1 ? new(HttpStatusCode.Forbidden) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) };
        });
        using var previews = new SonicPreviews(http);
        Assert.Equal(4096, (await previews.FetchAsync(Song, TestContext.Current.CancellationToken))!.Length);
        Assert.Equal(2, details); Assert.Equal(2, audio);
    }

    [Theory]
    [InlineData("http://p.dzcdn.net/song")]
    [InlineData("https://dzcdn.net.evil.example/song")]
    [InlineData("https://localhost/song")]
    [InlineData("https://user:secret@p.dzcdn.net/song")]
    [InlineData("https://p.dzcdn.net:8443/song")]
    public async Task UntrustedPreviewIsNeverRequested(string uri)
    {
        var handler = new Handler(_ => Json(uri));
        using var previews = new SonicPreviews(handler);
        Assert.Null(await previews.FetchAsync(Song, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    [Fact] public async Task WrongIdentityAndOversizedAudioAreMisses()
    {
        var wrong = new Handler(_ => Json("https://p.dzcdn.net/song", 99));
        using (var previews = new SonicPreviews(wrong)) Assert.Null(await previews.FetchAsync(Song, TestContext.Current.CancellationToken));
        Assert.Equal(1, wrong.Calls);
        var huge = new Handler(r => r.RequestUri!.Host == "api.deezer.com" ? Json("https://p.dzcdn.net/song")
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]) });
        using var client = new SonicPreviews(huge);
        Assert.Null(await client.FetchAsync(Song, TestContext.Current.CancellationToken));
    }

    [Fact] public async Task ApplePreviewUsesItsOwnLookupAndOfficialAudioHost()
    {
        var http = new Handler(r => r.RequestUri!.Host == "itunes.apple.com"
            ? new(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[{\"trackId\":1,\"previewUrl\":\"https://audio-ssl.itunes.apple.com/itunes-assets/preview.m4a\"}]}") }
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) });
        using var client = new SonicPreviews(http);
        Assert.Equal(4096, (await client.FetchAsync(Song with { Id = "apple:1" }, TestContext.Current.CancellationToken))!.Length);
        Assert.Equal(2, http.Calls);
    }
}
