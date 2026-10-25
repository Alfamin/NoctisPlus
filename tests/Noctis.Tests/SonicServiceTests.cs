using System.Net;
using FreeMusicFinder;
using Xunit;

namespace Noctis.Tests;

public sealed class SonicServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sonic-service-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
    private static CatalogSong Song(int n) => new("deezer:" + n, "Song " + n, [new("deezer:999", "Artist")], 200, true);
    private sealed class Encoder : ISonicEncoder
    {
        public string Model => "test-v1";
        public bool Ready { get; set; } = true;
        public int Calls;
        public Task<SonicVector?> EncodeAsync(byte[] preview, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(SonicVector.Create(Model, "preview", new float[] { 1, preview[0] / 10f, 0, 0, 0, 0, 0, 0 }));
        }
        public void Dispose() { }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            string id = request.RequestUri!.AbsolutePath.Split('/').Last();
            if (request.RequestUri.Host == "api.deezer.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($$"""{"id":{{id}},"preview":"https://p.dzcdn.net/{{id}}"}""") });
            var bytes = new byte[2048]; bytes[0] = byte.Parse(id);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    private SonicService Make(Handler http, Encoder encoder, Func<TasteRecording, CancellationToken, Task<CatalogSong?>>? resolve = null) =>
        new(new SonicCache(_folder, encoder.Model), encoder, new SonicPreviews(http), resolve ?? ((l, _) => Task.FromResult<CatalogSong?>(Song(int.Parse(l.Title[5..])))), () => false, _ => { });
    private static readonly TasteRecording[] Liked = [new("Artist", "Song 1", 2), new("Artist", "Song 2", 1)];

    [Fact] public async Task DisabledAndMissingModelNeverFetchAndNeverStartAnAnalysisQueue()
    {
        var http = new Handler(); var encoder = new Encoder();
        using var service = Make(http, encoder);
        Assert.Empty(await service.RankAsync([Song(3)], Liked, [], TestContext.Current.CancellationToken));
        service.Enabled = true; encoder.Ready = false;
        Assert.Empty(await service.RankAsync([Song(3)], Liked, [], TestContext.Current.CancellationToken));
        Assert.Equal(0, http.Calls); Assert.Equal(0, encoder.Calls);
    }

    [Fact] public async Task WarmedVectorsAreReusedAndOnlyFutureBuildsGetTheNewScores()
    {
        var http = new Handler(); var encoder = new Encoder();
        using var service = Make(http, encoder); service.Enabled = true;
        var candidates = new[] { Song(3), Song(4), Song(5) };
        await service.RankAsync(candidates, Liked, [], TestContext.Current.CancellationToken);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        int calls = http.Calls;
        var scores = await service.RankAsync(candidates, Liked, [], TestContext.Current.CancellationToken);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(3, scores.Count); Assert.Equal(5, encoder.Calls); Assert.Equal(calls, http.Calls);
        Assert.Equal(5, Directory.GetFiles(_folder, "*.json").Length);
        Assert.DoesNotContain(Directory.GetFiles(_folder), f => Path.GetExtension(f) == ".mp3");
    }

    [Fact] public async Task LatestWaitingPageReplacesEarlierWaitingWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolved = new List<string>();
        async Task<CatalogSong?> Resolve(TasteRecording l, CancellationToken ct)
        {
            resolved.Add(l.Title);
            if (l.Title == "Song 1") { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            return Song(int.Parse(l.Title[5..]));
        }
        using var service = Make(new Handler(), new Encoder(), Resolve); service.Enabled = true;
        await service.RankAsync([Song(3)], [new("Artist", "Song 1", 1), new("Artist", "Song 2", 1)], [], TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.RankAsync([Song(4)], [new("Artist", "Song 7", 1), new("Artist", "Song 2", 1)], [], TestContext.Current.CancellationToken);
        await service.RankAsync([Song(5)], [new("Artist", "Song 8", 1), new("Artist", "Song 2", 1)], [], TestContext.Current.CancellationToken);
        release.TrySetResult();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("Song 8", resolved); Assert.DoesNotContain("Song 7", resolved);
    }
}
