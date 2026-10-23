using System.Diagnostics;
using System.Text.Json;
using FreeMusicFinder;
using Noctis.Services;
using SimilarArtist = FreeMusicFinder.SimilarArtist;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Milestone C reliability (criterion P8): providers down or slow, Offline mode, cancellation, corrupt or partly written
/// files, an empty or tiny library and a huge imported playlist. In each case nothing escapes that the shelf cannot handle,
/// and the page saved before stays. Synthetic data and in-memory catalogs only; no network.
/// </summary>
public class DiscoverReliabilityTests : IDisposable
{
    private static readonly DateTimeOffset Now = DiscoverEvaluationTests.Now;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "noctis-discover-r-" + Guid.NewGuid().ToString("N"));
    public DiscoverReliabilityTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    private sealed class Library(IReadOnlyList<LocalSong> songs) : IPlaylistLibrary
    {
        public Task<IReadOnlyList<LocalSong>> SnapshotAsync() => Task.FromResult(songs);
        public Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null)
            => throw new InvalidOperationException("For you never changes playlists");
    }

    /// <summary>A catalog whose every call can be made to fail, wait or count.</summary>
    private sealed class Switchable(DiscoverEvaluationTests.World world) : IDiscoverCatalog
    {
        public Func<string, CancellationToken, Task>? Before;
        private async Task Go(string what, CancellationToken ct) { if (Before is { } before) await before(what, ct); }
        public async Task<CatalogArtist?> FindAsync(TasteSeed seed, CancellationToken ct) { await Go("find:" + seed.Name, ct); return await world.FindAsync(seed, ct); }
        public async Task<CatalogArtist?> FindByNameAsync(string name, CancellationToken ct) { await Go("find:" + name, ct); return await world.FindByNameAsync(name, ct); }
        public async Task<List<SimilarArtist>> SimilarAsync(TasteSeed seed, CatalogArtist? artist, CancellationToken ct) { await Go("similar:" + seed.Name, ct); return await world.SimilarAsync(seed, artist, ct); }
        public async Task<List<CatalogSearchSong>> TopSongsAsync(CatalogArtist artist, CancellationToken ct) { await Go("top:" + artist.Name, ct); return await world.TopSongsAsync(artist, ct); }
        public async Task<List<CatalogSearchSong>> SongsLikeAsync(TasteRecording liked, CancellationToken ct) { await Go("like:" + liked.Artist, ct); return await world.SongsLikeAsync(liked, ct); }
    }

    private (ForYouSource Source, Switchable Catalog, DiscoverEvaluationTests.Persona Persona) Source(string persona = "steady", Func<ArtistCatalog>? real = null)
    {
        var p = DiscoverEvaluationTests.Make(_folder, persona);
        var catalog = new Switchable(new DiscoverEvaluationTests.World());
        var plugin = Path.Combine(p.Folder, "plugin-data", "dev.moshi.freemusicfinder");
        Directory.CreateDirectory(plugin);
        var source = new ForYouSource(real ?? (() => throw new InvalidOperationException("no real catalog here")), null, _ => [], new Library(p.Library), p.Folder,
            Path.Combine(plugin, "for-you-hidden.json"), now: () => Now, discover: real is null ? () => catalog : null,
            unavailable: (songs, _) => Task.FromResult(new HashSet<string>()));
        return (source, catalog, p);
    }

    private static string PluginFile(DiscoverEvaluationTests.Persona p, string name) => Path.Combine(p.Folder, "plugin-data", "dev.moshi.freemusicfinder", name);

    [Fact]
    public async Task CatalogOutage_KeepsTheSavedPage_AndIsNoNewsForTheShelf()
    {
        var (source, catalog, _) = Source();
        using var _ = source;
        var first = await source.BuildAsync(true, null, CancellationToken.None);
        Assert.Equal(40, first.Songs.Count);
        int news = 0; source.PicksReady += _ => news++;
        catalog.Before = (_, _) => throw new HttpRequestException("Synthetic outage");
        var outage = await source.BuildAsync(true, null, CancellationToken.None);
        Assert.True(outage is { Songs.Count: 0, Unreachable: true }, "an outage is marked, so the shelf keeps its picks (regression: Refresh blanked it)");
        Assert.StartsWith("For you could not reach the music catalogs", outage.Notice);
        Assert.Equal(0, news); // regression: an empty failure lit the "new picks" dot
        Assert.Equal(first.Batch, (await source.SavedAsync(CancellationToken.None))!.Batch);
        Assert.Equal(first.Batch, source.LastSaved()!.Batch);
    }

    [Fact]
    public async Task SimilarArtistsDown_OwnArtistsStillFillTheShelf()
    {
        var (source, catalog, _) = Source();
        using var _ = source;
        // Similar artists and Deezer's radio both down: only the listener's own artists are left.
        catalog.Before = (what, _) => what.StartsWith("similar:", StringComparison.Ordinal) || what.StartsWith("like:", StringComparison.Ordinal)
            ? throw new HttpRequestException("Last.fm, ListenBrainz and Deezer radio down") : Task.CompletedTask;
        var page = await source.BuildAsync(true, null, CancellationToken.None);
        Assert.NotEmpty(page.Songs);
        Assert.False(page.Unreachable);
        Assert.All(page.Songs, s => Assert.True(s.Own));
    }

    [Fact]
    public async Task SlowOrTimedOutProvider_FailsOneArtist_NotTheBuild()
    {
        var (source, catalog, _) = Source();
        using var _ = source;
        // An HttpClient timeout is a TaskCanceledException nobody asked for (ListenBrainz's client does not convert it).
        catalog.Before = async (what, ct) =>
        {
            if (what == "similar:Rock 0") throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 25 seconds elapsing.");
            await Task.Delay(5, ct);
        };
        var watch = Stopwatch.StartNew();
        var page = await source.BuildAsync(true, null, CancellationToken.None);
        Assert.NotEmpty(page.Songs); // regression: the timeout aborted the whole build
        Assert.Contains("Some artists could not be loaded", page.Notice);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "slow lookups run six at a time, not one after another");
    }

    [Fact]
    public async Task OfflineMode_SendsNoRequest_AndKeepsTheSavedPage()
    {
        var access = new OnlineAccess();
        int requests = 0;
        var handler = new OnlineAccessHandler(new Counting(() => requests++), access);
        var catalogFolder = Path.Combine(_folder, "artist-catalog");
        using var real = new ArtistCatalog(catalogFolder, handler, () => Now, TimeSpan.Zero);
        var (source, catalog, p) = Source();
        using var _ = source;
        var first = await source.BuildAsync(true, null, CancellationToken.None);
        access.SetOffline(true);
        // The shelf's own catalog in Offline mode: the request is refused before it leaves, and the page saved before stays.
        using var offline = new ForYouSource(() => real, null, _ => [], new Library(p.Library), p.Folder, PluginFile(p, "for-you-hidden.json"), now: () => Now);
        var page = await offline.BuildAsync(true, null, CancellationToken.None);
        Assert.Equal(0, requests);
        Assert.True(page is { Songs.Count: 0, Unreachable: true });
        Assert.Equal(first.Batch, offline.LastSaved()!.Batch);
        // A provider that reports Offline mode itself stops the build at once; the shelf's error path keeps its picks.
        catalog.Before = (_, _) => throw new OfflineModeException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.BuildAsync(true, null, CancellationToken.None));
        Assert.Equal(first.Batch, source.LastSaved()!.Batch);
    }

    private sealed class Counting(Action count) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { count(); throw new HttpRequestException("no network in tests"); }
    }

    [Fact]
    public async Task CancellationMidBuild_EndsWithinASecond_AndTheNextBuildWorks()
    {
        var (source, catalog, p) = Source();
        using var _ = source;
        var first = await source.BuildAsync(true, null, CancellationToken.None);
        var started = new TaskCompletionSource();
        catalog.Before = async (what, ct) => { if (what.StartsWith("top:", StringComparison.Ordinal)) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        using var stop = new CancellationTokenSource();
        var build = source.BuildAsync(true, null, stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var watch = Stopwatch.StartNew();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"cancelled in {watch.ElapsedMilliseconds} ms");
        Assert.Equal(first.Batch, source.LastSaved()!.Batch);
        Assert.Equal(first.Batch, new ForYouCache(PluginFile(p, "for-you-last.json")).Read(Now)!.Result.Batch);
        catalog.Before = null;
        Assert.NotEmpty((await source.BuildAsync(true, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))).Songs);
    }

    [Fact]
    public async Task CorruptOrPartlyWrittenFiles_AreSkipped_AndTheShelfStillBuilds()
    {
        var (source0, _, p) = Source();
        source0.Dispose();
        var listens = Path.Combine(p.Folder, "plugin-data", "dev.moshi.trueshuffle", "listens.jsonl");
        int whole = TasteSources.Load(p.Folder).Count;
        // A crash mid-line, a line that is no object, a binary tail.
        File.AppendAllText(listens, "[1,2]\n\"text\"\n{\"artist\":\"Rock 9\",\"startUtc\":\"2026-10-19T10:00:00Z\",\"end\":\"Finished\",\"cov");
        File.AppendAllText(listens, "\0\0\u00ff\u00fe");
        Assert.Equal(whole, TasteSources.Load(p.Folder).Count);
        Assert.NotEmpty(DiscoverBaseline.Plays(p.Folder));
        File.WriteAllText(PluginFile(p, "for-you-last.json"), "{\"Version\":1,\"At\":\"2026-10-20T11:00:00+00:00\",\"Until\":\"2026-10-27T11:00:00+00:00\",\"Result\":{\"Songs\":[{\"Track\":");
        File.WriteAllText(PluginFile(p, "for-you-hidden.json"), "{\"Songs\":[\"deezer:1\"],\"Identities\":{\"deezer:1\":null},\"Recordings\":{\"x\":");
        // Whole JSON, broken entries: a card with no key or artist, a page with null places.
        File.WriteAllText(PluginFile(p, "for-you-feedback.json"),
            "{\"Version\":1,\"Batches\":[{\"Id\":\"old\",\"At\":\"2026-10-19T10:00:00+00:00\",\"Shown\":[null,{\"Key\":null,\"SongId\":\"x\",\"Artist\":\"a\",\"Seed\":\"\",\"Own\":false,\"Position\":0},"
            + "{\"Key\":\"rock 3 song 1|rock 3\",\"SongId\":\"y\",\"Artist\":null,\"Seed\":\"\",\"Own\":false,\"Position\":1}],\"Seen\":null}],\"Interactions\":[null]}");
        var (source, _, _) = Source();
        using var _ = source;
        Assert.Null(await source.SavedAsync(CancellationToken.None));
        var page = await source.BuildAsync(true, null, CancellationToken.None); // regression: a null card made every Learn throw
        Assert.NotEmpty(page.Songs);
        Assert.Single(source.Feedback.Batches);
    }

    [Fact]
    public async Task EmptyAndTinyLibraries_GiveAPageOrTheAdvice_NeverAnError()
    {
        var empty = Path.Combine(_folder, "empty");
        Directory.CreateDirectory(empty);
        var catalog = new DiscoverEvaluationTests.World();
        using var none = new ForYouSource(() => throw new InvalidOperationException(), null, _ => [], new Library([]), empty, Path.Combine(empty, "for-you-hidden.json"),
            now: () => Now, discover: () => catalog, unavailable: (_, _) => Task.FromResult(new HashSet<string>()));
        var advice = await none.BuildAsync(true, null, CancellationToken.None);
        Assert.True(advice is { Songs.Count: 0, Unreachable: false });
        Assert.StartsWith("Play some music first", advice.Notice);
        using var noData = new ForYouSource(() => throw new InvalidOperationException(), null, _ => [], new Library([]), null, Path.Combine(empty, "h2.json"),
            now: () => Now, discover: () => catalog, unavailable: (_, _) => Task.FromResult(new HashSet<string>()));
        Assert.Empty((await noData.BuildAsync(true, null, CancellationToken.None)).Songs);
        var (tiny, _, _) = Source("tiny");
        using var _ = tiny;
        var page = await tiny.BuildAsync(true, null, CancellationToken.None);
        Assert.Equal(40, page.Songs.Count);
        Assert.Empty(page.Songs.Where(s => s.Track.Song.Title == "Folk 7 Song 1"));
    }

    [Fact]
    public void HugeImportedPlaylist_IsOneDecision_AndCheap()
    {
        var library = Enumerable.Range(0, 50_000).Select(i => new LocalSong(Guid.NewGuid(), $"Imported {i % 2_000}", $"Track {i}", $"C:/m/{i}.flac", TimeSpan.FromMinutes(3))).ToList();
        library.AddRange(Enumerable.Range(0, 5).Select(i => new LocalSong(Guid.NewGuid(), "Chosen", $"Mine {i}", $"C:/m/c{i}.flac", TimeSpan.FromMinutes(3))));
        var imported = new TastePlaylist(true, library.Take(50_000).Select(s => s.Id).ToList());
        var mine = new TastePlaylist(false, library.Skip(50_000).Select(s => s.Id).ToList(), library.Skip(50_000).ToDictionary(s => s.Id, _ => Now.UtcDateTime.AddDays(-2)));
        var watch = Stopwatch.StartNew();
        var seeds = Taste.Seeds([], library, Now.UtcDateTime, playlists: [imported, mine]);
        var learning = new DiscoverFeedback(Path.Combine(_folder, "f.json"), () => Now).Learn([], library, [imported, mine]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal("Chosen", seeds[0].Name); // 25 songs in a 50,000-song import do not outweigh five chosen ones
        Assert.Empty(learning.Outcomes);
    }

    /// <summary>The shelf starts a build on the UI thread; reading, learning, ranking and lookups must all run elsewhere
    /// (regression: every step after an await came back to the UI thread, about a second of a large profile's work).</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Build_StartedOnTheUiThread_DoesItsWorkElsewhere()
    {
        var (source, catalog, _) = Source();
        using var _ = source;
        int onUi = 0, calls = 0;
        catalog.Before = (_, _) => { Interlocked.Increment(ref calls); if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Interlocked.Increment(ref onUi); return Task.CompletedTask; };
        Assert.True(Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
        var page = await source.BuildAsync(true, null, CancellationToken.None);
        Assert.NotEmpty(page.Songs);
        Assert.True(calls > 0 && onUi == 0, $"{onUi} of {calls} lookups ran on the UI thread");
    }
}
