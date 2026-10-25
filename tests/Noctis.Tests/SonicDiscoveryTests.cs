using FreeMusicFinder;
using Xunit;

namespace Noctis.Tests;

public sealed class SonicDiscoveryTests : IDisposable
{
    private sealed class Capture : IDiscoverSonic
    {
        public IReadOnlyList<TastePlay> Plays = [];
        public Task<IReadOnlyDictionary<string, double>> RankAsync(IReadOnlyList<CatalogSong> candidates, IReadOnlyList<TasteRecording> liked, IReadOnlyList<TastePlay> plays, CancellationToken ct)
        { Plays = plays; return Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>()); }
    }
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sonic-discover-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
    private Task<DiscoverResult> Build(Func<IReadOnlyList<CatalogSong>, CancellationToken, Task<IReadOnlyDictionary<string, double>>>? sonic = null) =>
        Discover.BuildAsync([new("Rock 0", 8, 2), new("Hip 0", 5, 1)], new DiscoverEvaluationTests.World(),
            (_, _) => Task.FromResult(new HashSet<string>()), new DiscoverHidden(Path.Combine(_folder, "hidden.json")), null, TestContext.Current.CancellationToken,
            context: new(new HashSet<string>(), DiscoverLearning.None, DiscoverEvaluationTests.Now, Sonic: sonic));

    [Fact] public async Task MissingModelErrorAndNoopKeepExactlyTheSameRanking()
    {
        var baseline = await Build();
        Assert.NotEmpty(baseline.Songs);
        var empty = await Build((_, _) => Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>()));
        var failed = await Build((_, _) => throw new IOException("model unavailable"));
        var noop = await Build((songs, _) => Task.FromResult<IReadOnlyDictionary<string, double>>(songs.ToDictionary(DiscoverIdentity.Key, _ => 1d)));
        var expected = baseline.Songs.Select(s => (s.Track.Song.Id, s.Score));
        Assert.Equal(expected, empty.Songs.Select(s => (s.Track.Song.Id, s.Score)));
        Assert.Equal(expected, failed.Songs.Select(s => (s.Track.Song.Id, s.Score)));
        Assert.Equal(expected, noop.Songs.Select(s => (s.Track.Song.Id, s.Score)));
    }

    [Fact] public async Task SonicSeesOnlyEligibleRecordingsAndStillKeepsDiversity()
    {
        var hidden = new DiscoverHidden(Path.Combine(_folder, "hidden.json"));
        hidden.Hide(new("deezer:x", "Rock 1 Song 1", [new("deezer:Rock-1", "Rock 1")], 181, true));
        int poolCount = 0;
        var result = await Discover.BuildAsync([new("Rock 0", 8, 2), new("Hip 0", 5, 1)], new DiscoverEvaluationTests.World(),
            (songs, _) => Task.FromResult(songs.Where(s => s.Title.EndsWith("Song 2")).Select(s => s.Id).ToHashSet()), hidden, null, TestContext.Current.CancellationToken,
            context: new(new HashSet<string>(), DiscoverLearning.None, DiscoverEvaluationTests.Now, Sonic: (songs, _) =>
            {
                poolCount = songs.Count;
                Assert.DoesNotContain(songs, s => s.Title.EndsWith("Song 2") || hidden.HidesSong(s));
                return Task.FromResult<IReadOnlyDictionary<string, double>>(songs.ToDictionary(DiscoverIdentity.Key, s => s.Title.EndsWith("Song 6") ? 1.6 : .7));
            }));
        Assert.True(poolCount > Discover.Shelf);
        Assert.NotEmpty(result.Songs);
        Assert.True(result.Songs.Take(20).Select(s => s.Track.Song.Artists[0].Name).Distinct().Count() >= 5);
        Assert.DoesNotContain(result.Songs, s => hidden.HidesSong(s.Track.Song) || s.Track.Song.Title.EndsWith("Song 2"));
    }

    [Fact] public async Task DismissUndoRemovesTheSonicNegativeWithoutBanningTheArtist()
    {
        var now = DiscoverEvaluationTests.Now;
        var feedback = new DiscoverFeedback(Path.Combine(_folder, "feedback.json"), () => now);
        string key = DiscoverIdentity.Key("Rock 0 Song 5", "Rock 0");
        feedback.Shown("page", [new(key, "deezer:5", "rock0", "Rock 0", true, 0)]);
        feedback.Record("page", key, "Rock 0", "Rock 0 Song 5", DiscoverAction.Dismiss);
        var capture = new Capture();
        Task<DiscoverResult> Run() => Discover.ForListenerAsync([new("Rock 0", now.AddDays(-1).UtcDateTime, 1.5, "Rock 0 Song 1", true)], [], [],
            feedback, new DiscoverHidden(Path.Combine(_folder, "hidden.json")), new DiscoverEvaluationTests.World(), (_, _) => Task.FromResult(new HashSet<string>()),
            null, now, TestContext.Current.CancellationToken, sonic: capture);
        var first = await Run();
        Assert.Contains(capture.Plays, p => p.Title == "Rock 0 Song 5" && p.Weight < 0);
        Assert.Contains(first.Songs, s => s.Track.Song.Artists[0].Name == "Rock 0");
        feedback.Retract("page", key, DiscoverAction.Dismiss);
        await Run();
        Assert.DoesNotContain(capture.Plays, p => p.Title == "Rock 0 Song 5" && p.Weight < 0);
        await feedback.Flushed;
    }
}
