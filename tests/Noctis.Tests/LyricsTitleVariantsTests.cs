using System.Net;
using System.Text;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #230 (BIA "LONDON (feat. J. Cole)") and #231 (Drake "What’s Next"): word timing a source has is found.</summary>
public class LyricsTitleVariantsTests
{
    [Theory]
    [InlineData("LONDON (feat. J. Cole)", false, "LONDON")]
    [InlineData("What’s Next", true, "What s Next")]
    [InlineData("What's Next", true, "What s Next")]
    [InlineData("What’s Next", false, null)]
    [InlineData("LONDON", true, null)]
    public void PlainerTitle(string title, bool apostropheAsSpace, string? expected) =>
        Assert.Equal(expected, LyricsSearchSelector.PlainerTitle(title, apostropheAsSpace));

    [Fact]
    public void ACurlyApostropheMatchesAStraightOne() =>
        Assert.True(LyricsSearchSelector.MatchesTrack(
            new Noctis.Models.LrcLibResult { TrackName = "What's Next (Explicit)", ArtistName = "Drake", Duration = 178 }, "Drake", "What’s Next", 178));

    sealed class Router(Func<Uri, string> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(Uri.UnescapeDataString(request.RequestUri!.Query));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(answer(request.RequestUri!), Encoding.UTF8, "application/json") });
        }
    }

    const string Miss = "{\"results\":[],\"source\":\"MISS-LRC-RED\",\"total\":0}";

    static string Hit(params (string Title, string Artist, int Seconds, string Timing, string Isrc)[] songs) =>
        "{\"results\":[" + string.Join(",", songs.Select(s =>
            $"{{\"artist_name\":\"{s.Artist}\",\"duration\":{s.Seconds},\"id\":\"{s.Isrc}\",\"isrc\":\"{s.Isrc}\",\"timing_type\":\"{s.Timing}\",\"track_name\":\"{s.Title}\"}}")) + "]}";

    [Fact]
    public async Task LrcRed_AsksAgainWithoutTheFeaturedArtist()
    {
        // Real answers (2026-10-30): the full title misses; "LONDON" has the word-timed one.
        var router = new Router(u => Uri.UnescapeDataString(u.Query).Contains("feat") ? Miss
            : Hit(("LONDON", "BIA, J. Cole", 251, "line", "USSM12200194"), ("LONDON", "BIA, J. Cole", 251, "word", "USSM12200193")));
        var match = await new LrcRedLyricsService(new HttpClient(router)).FindMatchAsync("BIA", "LONDON (feat. J. Cole)", 250.4);
        Assert.Equal("USSM12200193", match?.Isrc);
        Assert.Equal(2, router.Asked.Count);
    }

    [Fact]
    public async Task LrcRed_AsksAgainWithASpaceForTheApostrophe()
    {
        // lrc.red's search finds Drake "What’s Next" only as "What s Next".
        var router = new Router(u => Uri.UnescapeDataString(u.Query).Contains("What s Next")
            ? Hit(("What’s Next", "Drake", 178, "word", "USUG12100342"), ("What's Next", "Drake", 178, "line", "USUG12100343")) : Miss);
        var match = await new LrcRedLyricsService(new HttpClient(router)).FindMatchAsync("Drake", "What’s Next", 178);
        Assert.Equal("USUG12100342", match?.Isrc);
    }

    [Fact]
    public async Task LrcRed_AFoundTitleIsAskedOnce()
    {
        var router = new Router(_ => Hit(("LONDON", "BIA, J. Cole", 251, "word", "USSM12200193")));
        await new LrcRedLyricsService(new HttpClient(router)).FindMatchAsync("BIA", "LONDON", 250);
        Assert.Single(router.Asked);
    }

    [Fact]
    public void Kugou_TheStraightApostrophesSongIsPicked()
    {
        var json = "{\"data\":{\"info\":[{\"hash\":\"AB12\",\"songname\":\"What's Next (Explicit)\",\"singername\":\"Drake\",\"duration\":178}]}}";
        Assert.Equal("AB12", Noctis.Services.KugouLyricsService.PickSong(json, "Drake", "What’s Next", 178)?.Hash);
    }
}
