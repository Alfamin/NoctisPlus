using System.Net;
using System.Text;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ArtistImageServiceTests
{
    [Fact]
    public async Task FetchAndCacheAsync_PrefersExactDeezerArtistMatch()
    {
        using var persistence = new TestPersistenceService();
        var wrongImageUrl = "https://images.example/wrong.jpg";
        var futureImageUrl = "https://images.example/future.jpg";
        var handler = new StubHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
            {
                return JsonResponse($$"""
                {
                  "data": [
                    { "name": "Future Islands", "picture_big": "{{wrongImageUrl}}" },
                    { "name": "Future", "picture_xl": "{{futureImageUrl}}" }
                  ]
                }
                """);
            }

            return ImageResponse();
        });
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Future" };

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Equal(futureImageUrl, handler.RequestedImageUrls.Single());
        Assert.False(string.IsNullOrWhiteSpace(artist.ImagePath));
        Assert.True(File.Exists(artist.ImagePath));
    }

    /// <summary>09-15: the artist page shows Deezer's fan count. The portrait search
    /// records the chosen account's nb_fan beside the portrait (no extra request), the
    /// real account wins over a same-name impostor, and a later ask reads the sidecar.</summary>
    [Fact]
    public async Task FetchAndCacheAsync_RecordsTheChosenAccountsFanCount_AndGetFanCountReadsIt()
    {
        using var persistence = new TestPersistenceService();
        var searches = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
            {
                searches++;
                return JsonResponse("""
                {
                  "data": [
                    { "name": "Bad Bunny", "nb_fan": 6, "nb_album": 1, "picture_xl": "https://images.example/fake.jpg" },
                    { "name": "Bad Bunny", "nb_fan": 7994069, "nb_album": 88, "picture_xl": "https://images.example/real.jpg" }
                  ]
                }
                """);
            }
            return ImageResponse();
        });
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Bad Bunny" };

        Assert.Null(service.TryGetCachedFanCount(artist.Id));
        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Equal("https://images.example/real.jpg", handler.RequestedImageUrls.Single());
        Assert.Equal(7994069, service.TryGetCachedFanCount(artist.Id));
        Assert.Equal(1, searches);

        // Fresh sidecar: served from disk, no second search.
        Assert.Equal(7994069, await service.GetFanCountAsync(artist.Id, artist.Name));
        Assert.Equal(1, searches);
    }

    [Fact]
    public async Task GetFanCountAsync_LooksTheArtistUpWhenNothingIsRecorded()
    {
        using var persistence = new TestPersistenceService();
        var handler = new StubHttpMessageHandler(request => JsonResponse("""
            { "data": [ { "name": "Aventura", "nb_fan": 1234567, "picture_xl": "https://images.example/a.jpg" } ] }
            """));
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var id = Guid.NewGuid();

        Assert.Equal(1234567, await service.GetFanCountAsync(id, "Aventura"));
        Assert.Equal(1234567, service.TryGetCachedFanCount(id));
        Assert.Empty(handler.RequestedImageUrls); // the count alone never downloads a photo
    }

    [Fact]
    public async Task FetchAndCacheAsync_QueuesConcurrentRequests()
    {
        using var persistence = new TestPersistenceService();
        var firstImageRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstImage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
            {
                var artistName = Uri.UnescapeDataString(request.RequestUri.Query)
                    .Contains("Second", StringComparison.OrdinalIgnoreCase)
                    ? "Second"
                    : "First";
                return JsonResponse($$"""
                { "data": [ { "name": "{{artistName}}", "picture_big": "https://images.example/{{artistName}}.jpg" } ] }
                """);
            }

            if (url.Contains("/First.jpg", StringComparison.Ordinal))
            {
                firstImageRequested.SetResult();
                await releaseFirstImage.Task;
            }

            return ImageResponse();
        });
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var first = new Artist { Id = Guid.NewGuid(), Name = "First" };
        var second = new Artist { Id = Guid.NewGuid(), Name = "Second" };

        var firstFetch = service.FetchAndCacheAsync(new[] { first });
        await firstImageRequested.Task;
        var secondFetch = service.FetchAndCacheAsync(new[] { second });
        releaseFirstImage.SetResult();
        await Task.WhenAll(firstFetch, secondFetch);

        Assert.True(File.Exists(first.ImagePath));
        Assert.True(File.Exists(second.ImagePath));
    }

    // ── Refresh sweep: cached portraits follow the service ──

    private static StubHttpMessageHandler DeezerHandler(string name, string imageUrl, byte fill = 0x11)
        => new(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
                return JsonResponse($$"""{ "data": [ { "name": "{{name}}", "picture_xl": "{{imageUrl}}" } ] }""");
            return ImageResponse(fill);
        });

    private static string Sidecar(string url) => "v3\n" + url + "\n";

    private static string SidecarUrl(string artworkDir, Guid id)
        => File.ReadAllLines(Path.Combine(artworkDir, $"{id}.src"))[1].Trim();

    private static void AgeSidecar(string artworkDir, Guid id, string url)
    {
        var src = Path.Combine(artworkDir, $"{id}.src");
        File.WriteAllText(src, Sidecar(url));
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow - ArtistImageService.RefreshInterval - TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task FetchAndCacheAsync_PrefersTheExactMatchWithMostFans()
    {
        // Real Deezer response shape for "Bad Bunny": an impostor with the same name is
        // listed FIRST with 6 fans; the real artist follows with millions.
        using var persistence = new TestPersistenceService();
        var handler = new StubHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
                return JsonResponse("""
                { "data": [
                    { "name": "Bad Bunny", "nb_fan": 6, "nb_album": 1, "picture_xl": "https://images.example/fake.jpg" },
                    { "name": "Bad Bunny", "nb_fan": 7988527, "nb_album": 88, "picture_xl": "https://images.example/real.jpg" },
                    { "name": "Bad Bunny Chapin", "nb_fan": 24, "nb_album": 1, "picture_xl": "https://images.example/chapin.jpg" }
                ] }
                """);
            return ImageResponse();
        });
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Bad Bunny" };

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Equal("https://images.example/real.jpg", handler.RequestedImageUrls.Single());
        Assert.Equal("https://images.example/real.jpg", SidecarUrl(ArtworkDir(persistence), artist.Id));
    }

    /// <summary>Real Deezer response for "Arcángel" (09-17): the real account (1.7M fans,
    /// id 5536564) is spelled "Arcangel", while an accent-exact duplicate has 1,683 fans.
    /// An accent-exact tier picked the duplicate; names fold diacritics now.</summary>
    [Fact]
    public async Task FetchAndCacheAsync_MatchesNamesWithoutDiacritics()
    {
        using var persistence = new TestPersistenceService();
        var handler = new StubHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
                return JsonResponse("""
                { "data": [
                    { "id": 411188252, "name": "Arcangel", "nb_fan": 1, "nb_album": 15, "picture_xl": "https://images.example/one-fan.jpg" },
                    { "id": 14033577, "name": "Arcángel", "nb_fan": 1683, "nb_album": 22, "picture_xl": "https://images.example/duplicate.jpg" },
                    { "id": 9216716, "name": "Arcángel & DJ Luian", "nb_fan": 3589, "nb_album": 0, "picture_xl": "https://images.example/duo.jpg" },
                    { "id": 5536564, "name": "Arcangel", "nb_fan": 1704853, "nb_album": 183, "picture_xl": "https://images.example/real.jpg" }
                ] }
                """);
            return ImageResponse();
        });
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Arcángel" };

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Equal("https://images.example/real.jpg", handler.RequestedImageUrls.Single());
        Assert.Equal(1704853, await service.GetFanCountAsync(artist.Id, artist.Name));
    }

    /// <summary>Same-name accounts are verified against the library: the account whose
    /// Deezer top tracks contain the titles the library holds for the artist wins even
    /// when another account of that name has far more fans.</summary>
    [Fact]
    public async Task FetchAndCacheAsync_SameNameAccounts_PicksTheOneWhoseTopTracksMatchTheLibrary()
    {
        using var persistence = new TestPersistenceService();
        var requestedTops = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
                return JsonResponse("""
                { "data": [
                    { "id": 1, "name": "Nova", "nb_fan": 900000, "nb_album": 12, "picture_xl": "https://images.example/popstar.jpg" },
                    { "id": 2, "name": "Nova", "nb_fan": 4000, "nb_album": 3, "picture_xl": "https://images.example/indie.jpg" },
                    { "id": 3, "name": "Nova", "nb_fan": 7, "nb_album": 1, "picture_xl": "https://images.example/nobody.jpg" }
                ] }
                """);
            if (url.StartsWith("https://api.deezer.com/artist/", StringComparison.Ordinal))
            {
                requestedTops.Add(url);
                if (url.Contains("/artist/1/"))
                    return JsonResponse("""{ "data": [ { "title_short": "Neon Nights", "title": "Neon Nights" }, { "title_short": "Runaway", "title": "Runaway" } ] }""");
                if (url.Contains("/artist/2/"))
                    return JsonResponse("""{ "data": [ { "title_short": "Café Con Leche", "title": "Café Con Leche (feat. Someone)" }, { "title_short": "Sin Ti", "title": "Sin Ti" } ] }""");
                return JsonResponse("""{ "data": [] }""");
            }
            return ImageResponse();
        });
        var library = new FakeLibraryService();
        library.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = "Cafe con Leche (feat. Someone)", Artist = "Nova", AlbumArtist = "Nova" });
        library.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = "Sin Ti - Remix", Artist = "Nova & Friend", AlbumArtist = "Nova" });
        library.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = "Runaway", Artist = "Somebody Else", AlbumArtist = "Somebody Else" });
        var service = new ArtistImageService(new HttpClient(handler), persistence, library);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Nova" };

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Equal("https://images.example/indie.jpg", handler.RequestedImageUrls.Single());
        Assert.Equal(4000, await service.GetFanCountAsync(artist.Id, artist.Name));
        // Every same-name account was checked (fan order), nothing beyond the tier.
        Assert.Equal(3, requestedTops.Count);
    }

    [Theory]
    [InlineData("Me Acostumbré (feat. Bad Bunny)", "meacostumbre")]
    [InlineData("Sin Ti - Remix", "sinti")]
    [InlineData("Pa' Que La Pases Bien", "paquelapasesbien")]
    [InlineData("  ", "")]
    public void TitleKey_FoldsAccentsPunctuationAndSuffixes(string title, string expected)
        => Assert.Equal(expected, ArtistImageService.TitleKey(title));

    [Fact]
    public async Task FetchAndCacheAsync_OldFormatSidecarIsRecheckedImmediately()
    {
        using var persistence = new TestPersistenceService();
        var handler = DeezerHandler("Future", "https://images.example/real.jpg", fill: 0x22);
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Future" };
        var cachedPath = service.GetCachedImagePath(artist.Id);
        File.WriteAllBytes(cachedPath, ImageBytes(0x11));
        // Pre-v2 sidecar: bare URL, written a minute ago — would be "fresh" by time alone.
        File.WriteAllText(Path.Combine(ArtworkDir(persistence), $"{artist.Id}.src"), "https://images.example/fake.jpg");

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Equal("https://images.example/real.jpg", handler.RequestedImageUrls.Single());
        Assert.Equal(0x22, File.ReadAllBytes(cachedPath)[8]);
        Assert.Equal("https://images.example/real.jpg", SidecarUrl(ArtworkDir(persistence), artist.Id));
    }

    private static string ArtworkDir(TestPersistenceService p) => Path.Combine(p.DataDirectory, "artwork", "artists");

    [Fact]
    public async Task FetchAndCacheAsync_ReplacesCachedPortraitWhenServiceUrlChanged()
    {
        using var persistence = new TestPersistenceService();
        var handler = DeezerHandler("Future", "https://images.example/new.jpg", fill: 0x22);
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Future" };
        var cachedPath = service.GetCachedImagePath(artist.Id);
        File.WriteAllBytes(cachedPath, ImageBytes(0x11));
        AgeSidecar(ArtworkDir(persistence), artist.Id, "https://images.example/old.jpg");
        var ready = new List<string>();

        await service.FetchAndCacheAsync(new[] { artist }, (_, p) => ready.Add(p));

        Assert.Equal("https://images.example/new.jpg", handler.RequestedImageUrls.Single());
        Assert.Equal(0x22, File.ReadAllBytes(cachedPath)[8]);
        Assert.Equal("https://images.example/new.jpg", SidecarUrl(ArtworkDir(persistence), artist.Id));
        Assert.Contains(cachedPath, ready);
    }

    [Fact]
    public async Task FetchAndCacheAsync_UnchangedUrlOnlyStampsCheckTime()
    {
        using var persistence = new TestPersistenceService();
        var handler = DeezerHandler("Future", "https://images.example/same.jpg", fill: 0x22);
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Future", ImagePath = null };
        var cachedPath = service.GetCachedImagePath(artist.Id);
        File.WriteAllBytes(cachedPath, ImageBytes(0x11));
        AgeSidecar(ArtworkDir(persistence), artist.Id, "https://images.example/same.jpg");
        var src = Path.Combine(ArtworkDir(persistence), $"{artist.Id}.src");
        var before = File.GetLastWriteTimeUtc(src);

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Empty(handler.RequestedImageUrls);
        Assert.Equal(0x11, File.ReadAllBytes(cachedPath)[8]);
        Assert.True(File.GetLastWriteTimeUtc(src) > before);
    }

    [Fact]
    public async Task FetchAndCacheAsync_FreshSidecarMakesNoRequests()
    {
        using var persistence = new TestPersistenceService();
        var searches = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            searches++;
            return JsonResponse("""{ "data": [] }""");
        });
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Future" };
        File.WriteAllBytes(service.GetCachedImagePath(artist.Id), ImageBytes(0x11));
        File.WriteAllText(Path.Combine(ArtworkDir(persistence), $"{artist.Id}.src"), Sidecar("https://images.example/same.jpg"));

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Equal(0, searches);
    }

    [Fact]
    public async Task FetchAndCacheAsync_NeverTouchesCustomPortrait()
    {
        using var persistence = new TestPersistenceService();
        var searches = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            searches++;
            return JsonResponse("""{ "data": [ { "name": "Future", "picture_xl": "https://images.example/new.jpg" } ] }""");
        });
        var service = new ArtistImageService(new HttpClient(handler), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Future" };
        await service.SetCustomImageAsync(artist, ImageBytes(0x11));
        // Even a stale sidecar (should not exist for custom, but be defensive) must not trigger.
        AgeSidecar(ArtworkDir(persistence), artist.Id, "https://images.example/old.jpg");

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Equal(0, searches);
        Assert.Equal(0x11, File.ReadAllBytes(service.GetCachedImagePath(artist.Id))[8]);
    }

    // ── GitHub #137: a portrait already on disk never waits behind network work ──

    /// <summary>A service whose every Deezer request hangs until <c>Release</c>, so a sweep
    /// that reaches the network holds the fetch gate for as long as the test wants.</summary>
    private static (ArtistImageService Service, Task NetworkReached, Action Release) HungNetworkService(
        TestPersistenceService persistence)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async _ =>
        {
            reached.TrySetResult();
            await release.Task;
            return JsonResponse("""{ "data": [] }""");
        });
        return (new ArtistImageService(new HttpClient(handler), persistence), reached.Task, () => release.TrySetResult());
    }

    /// <summary>A cached portrait whose last check is a day old: the sweep re-asks Deezer.</summary>
    private static string CachedRefreshDue(ArtistImageService service, TestPersistenceService persistence, Artist artist)
    {
        var path = service.GetCachedImagePath(artist.Id);
        File.WriteAllBytes(path, ImageBytes(0x11));
        AgeSidecar(ArtworkDir(persistence), artist.Id, $"https://images.example/{artist.Name}.jpg");
        return path;
    }

    [Fact]
    public async Task FetchAndCacheAsync_CachedPortraitSurfacesWhileAnotherSweepWaitsOnTheNetwork()
    {
        // The Artists tab's full-library sweep holds the gate while it re-checks a
        // day-old portrait; Home's own call for an artist already on disk must not wait.
        using var persistence = new TestPersistenceService();
        var (service, networkReached, release) = HungNetworkService(persistence);
        var busy = new Artist { Id = Guid.NewGuid(), Name = "Busy" };
        CachedRefreshDue(service, persistence, busy);
        var sweep = service.FetchAndCacheAsync(new[] { busy });
        await networkReached.WaitAsync(TimeSpan.FromSeconds(10));

        var home = new Artist { Id = Guid.NewGuid(), Name = "Home" };
        var homePath = service.GetCachedImagePath(home.Id);
        File.WriteAllBytes(homePath, ImageBytes(0x22));
        File.WriteAllText(Path.Combine(ArtworkDir(persistence), $"{home.Id}.src"), Sidecar("https://images.example/home.jpg"));
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var homeFetch = service.FetchAndCacheAsync(new[] { home }, (_, p) => ready.TrySetResult(p));

        try
        {
            var first = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(ready.Task, first);
            Assert.Equal(homePath, await ready.Task);
            Assert.Equal(homePath, home.ImagePath);
            Assert.False(sweep.IsCompleted);
        }
        finally
        {
            release();
            await Task.WhenAll(sweep, homeFetch);
        }
    }

    [Fact]
    public async Task FetchAndCacheAsync_EveryCachedPortraitSurfacesBeforeTheFirstRefresh()
    {
        // One sweep, name order: "Zed" is on disk too, and used to stay blank until
        // every earlier artist's paced refresh had come back.
        using var persistence = new TestPersistenceService();
        var (service, networkReached, release) = HungNetworkService(persistence);
        var abba = new Artist { Id = Guid.NewGuid(), Name = "Abba" };
        var zed = new Artist { Id = Guid.NewGuid(), Name = "Zed" };
        var abbaPath = CachedRefreshDue(service, persistence, abba);
        var zedPath = CachedRefreshDue(service, persistence, zed);

        var sweep = service.FetchAndCacheAsync(new[] { abba, zed });
        try
        {
            await networkReached.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(abbaPath, abba.ImagePath);
            Assert.Equal(zedPath, zed.ImagePath);
        }
        finally
        {
            release();
            await sweep;
        }
    }

    [Fact]
    public async Task FetchAndCacheAsync_RemovedPortraitStaysBlankWhileAnotherSweepWaits()
    {
        using var persistence = new TestPersistenceService();
        var (service, networkReached, release) = HungNetworkService(persistence);
        var busy = new Artist { Id = Guid.NewGuid(), Name = "Busy" };
        CachedRefreshDue(service, persistence, busy);
        var sweep = service.FetchAndCacheAsync(new[] { busy });
        await networkReached.WaitAsync(TimeSpan.FromSeconds(10));

        // A file left behind by a fetch that raced the removal must not resurface.
        var removed = new Artist { Id = Guid.NewGuid(), Name = "Removed" };
        var removedPath = service.GetCachedImagePath(removed.Id);
        File.WriteAllBytes(removedPath, ImageBytes(0x33));
        File.WriteAllText(Path.Combine(ArtworkDir(persistence), $"{removed.Id}.removed"), string.Empty);
        removed.ImagePath = removedPath;
        var reported = false;
        var fetch = service.FetchAndCacheAsync(new[] { removed }, (_, _) => reported = true);

        try
        {
            var deadline = Environment.TickCount64 + 5000;
            while (removed.ImagePath != null && Environment.TickCount64 < deadline)
                await Task.Delay(10);
            Assert.Null(removed.ImagePath);
            Assert.False(reported);
        }
        finally
        {
            release();
            await Task.WhenAll(sweep, fetch);
        }
    }

    // ── Deezer's "no photo" silhouette (10-12) ──

    /// <summary>Deezer's grey silhouette, as its CDN served it on 10-12 for every photo hash
    /// it no longer has: 1800 px (44,631 bytes) and 1000 px (32,022 bytes).</summary>
    private static byte[] NoPhotoBytes(int size)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Deezer", $"no-photo-{size}.jpg"));

    /// <summary>"Xavier Mayne" on 10-12: the API lists hash f0bb52af… in every picture_* field,
    /// and the CDN answers each size with a 302 to the empty hash (the MD5 of ""), which serves
    /// the silhouette.</summary>
    private const string DeadHashPhoto = "https://cdn-images.dzcdn.net/images/artist/f0bb52af04768d99c366fd295f2cc156/1000x1000-000000-80-0-0.jpg";

    /// <summary>The search answers with <paramref name="imageUrl"/>; any CDN request is answered
    /// the way HttpClient reports a followed redirect to the silhouette (the request's URI
    /// rewritten to the empty hash), unless <paramref name="reportRedirect"/> is false.</summary>
    private static StubHttpMessageHandler NoPhotoHandler(string name, string imageUrl, List<string> cdnRequests, bool reportRedirect = true)
        => new(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
                return JsonResponse($$"""{ "data": [ { "id": 14335143, "name": "{{name}}", "nb_fan": 660, "picture_xl": "{{imageUrl}}" } ] }""");
            cdnRequests.Add(url);
            var size = url.Contains("/1800x1800-", StringComparison.Ordinal) ? 1800 : 1000;
            if (reportRedirect)
                request.RequestUri = new Uri($"https://cdn-images.dzcdn.net/images/artist/d41d8cd98f00b204e9800998ecf8427e/{size}x{size}-000000-80-0-0.jpg");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(NoPhotoBytes(size)) { Headers = { ContentType = new("image/jpeg") } }
            };
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // a handler that does not report the final URL: the bytes still tell
    public async Task FetchAndCacheAsync_DeezersSilhouetteIsNoPhoto(bool reportRedirect)
    {
        using var persistence = new TestPersistenceService();
        var cdn = new List<string>();
        var service = new ArtistImageService(new HttpClient(NoPhotoHandler("Xavier Mayne", DeadHashPhoto, cdn, reportRedirect)), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Xavier Mayne" };

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Null(artist.ImagePath);
        Assert.False(File.Exists(service.GetCachedImagePath(artist.Id)));
        // Asked once at 1800 px; the silhouette is the answer at every size, so no 1000 px retry.
        Assert.Equal(DeezerArtistPhotos.UpgradeSize(DeadHashPhoto), cdn.Single());
    }

    [Fact]
    public async Task FetchAndCacheAsync_SilhouetteFallsBackToThePrimaryArtistsPortrait()
    {
        using var persistence = new TestPersistenceService();
        var cdn = new List<string>();
        var service = new ArtistImageService(new HttpClient(NoPhotoHandler("Yesir feat. Xavier Mayne", DeadHashPhoto, cdn)), persistence);
        var primary = new Artist { Id = Guid.NewGuid(), Name = "Yesir" };
        var primaryPath = service.GetCachedImagePath(primary.Id);
        File.WriteAllBytes(primaryPath, ImageBytes(0x11));
        File.WriteAllText(Path.Combine(ArtworkDir(persistence), $"{primary.Id}.src"), Sidecar("https://images.example/yesir.jpg"));
        var collab = new Artist { Id = Guid.NewGuid(), Name = "Yesir feat. Xavier Mayne" };

        await service.FetchAndCacheAsync(new[] { primary, collab });

        Assert.Equal(primaryPath, collab.ImagePath);
        Assert.False(File.Exists(service.GetCachedImagePath(collab.Id)));
    }

    [Fact]
    public async Task FetchAndCacheAsync_SilhouetteKeepsTheCachedPortrait_AndIsNotDownloadedAgain()
    {
        using var persistence = new TestPersistenceService();
        var cdn = new List<string>();
        var service = new ArtistImageService(new HttpClient(NoPhotoHandler("Xavier Mayne", DeadHashPhoto, cdn)), persistence);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Xavier Mayne" };
        var cachedPath = service.GetCachedImagePath(artist.Id);
        File.WriteAllBytes(cachedPath, ImageBytes(0x11));
        AgeSidecar(ArtworkDir(persistence), artist.Id, "https://images.example/old.jpg");

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Single(cdn);
        Assert.Equal(0x11, File.ReadAllBytes(cachedPath)[8]);
        // The check is stamped, so the next due sweep finds the URL unchanged.
        var src = Path.Combine(ArtworkDir(persistence), $"{artist.Id}.src");
        Assert.Equal(DeezerArtistPhotos.UpgradeSize(DeadHashPhoto), SidecarUrl(ArtworkDir(persistence), artist.Id));
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow - ArtistImageService.RefreshInterval - TimeSpan.FromHours(1));

        await service.FetchAndCacheAsync(new[] { artist });

        Assert.Single(cdn);
        Assert.Equal(0x11, File.ReadAllBytes(cachedPath)[8]);
    }

    [Fact]
    public async Task Startup_PurgesCachedSilhouettes_ButNotCustomPortraitsOrLookalikes()
    {
        using var persistence = new TestPersistenceService();
        var dir = ArtworkDir(persistence);
        Directory.CreateDirectory(dir);
        var silhouette = Guid.NewGuid();
        var silhouette1000 = Guid.NewGuid();
        var custom = Guid.NewGuid();
        var sameLength = Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(dir, $"{silhouette}.jpg"), NoPhotoBytes(1800));
        File.WriteAllText(Path.Combine(dir, $"{silhouette}.src"), Sidecar(DeezerArtistPhotos.UpgradeSize(DeadHashPhoto)));
        File.WriteAllBytes(Path.Combine(dir, $"{silhouette1000}.jpg"), NoPhotoBytes(1000));
        File.WriteAllBytes(Path.Combine(dir, $"{custom}.jpg"), NoPhotoBytes(1800));
        File.WriteAllText(Path.Combine(dir, $"{custom}.custom"), string.Empty);
        var lookalike = NoPhotoBytes(1800);
        lookalike[^3] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(dir, $"{sameLength}.jpg"), lookalike);

        var service = new ArtistImageService(new HttpClient(new StubHttpMessageHandler(_ => JsonResponse("""{ "data": [] }"""))), persistence);
        await service.PlaceholderPurge;

        Assert.False(File.Exists(Path.Combine(dir, $"{silhouette}.jpg")));
        Assert.False(File.Exists(Path.Combine(dir, $"{silhouette}.src")));
        Assert.False(File.Exists(Path.Combine(dir, $"{silhouette1000}.jpg")));
        Assert.True(File.Exists(Path.Combine(dir, $"{custom}.jpg")));
        Assert.True(File.Exists(Path.Combine(dir, $"{sameLength}.jpg")));
    }

    [Fact]
    public void IsPlaceholderUrl_KnowsTheEmptyHash()
    {
        Assert.True(DeezerArtistPhotos.IsPlaceholderUrl("https://cdn-images.dzcdn.net/images/artist//1000x1000-000000-80-0-0.jpg"));
        Assert.True(DeezerArtistPhotos.IsPlaceholderUrl("https://cdn-images.dzcdn.net/images/artist/d41d8cd98f00b204e9800998ecf8427e/1800x1800-000000-80-0-0.jpg"));
        Assert.False(DeezerArtistPhotos.IsPlaceholderUrl(DeadHashPhoto));
    }

    private static byte[] ImageBytes(byte fill)
    {
        var bytes = new byte[6 * 1024];
        Array.Fill(bytes, fill);
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        return bytes;
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage ImageResponse(byte fill = 0)
    {
        // Must exceed ArtistImageService's 5120-byte Last.fm-placeholder purge
        // threshold, otherwise the background purge task races the cache write
        // and deletes the file before the test asserts File.Exists. Starts with
        // JPEG magic bytes so it passes the HttpSafety.LooksLikeImage gate.
        var bytes = ImageBytes(fill);
        return new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new("image/jpeg") }
            }
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public List<string> RequestedImageUrls { get; } = new();

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this(request => Task.FromResult(handler(request)))
        {
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "images.example")
                RequestedImageUrls.Add(request.RequestUri.ToString());

            return await _handler(request);
        }
    }
}
