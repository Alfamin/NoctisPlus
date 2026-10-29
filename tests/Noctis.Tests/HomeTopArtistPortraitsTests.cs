using System.Collections.Specialized;
using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #137: Home's artist row showed placeholders for artists whose portrait was
/// already on disk, while the library was being scanned. The row must be built with the
/// cached portrait in hand, must show it on the instances it actually displays, and must
/// repaint when another sweep (the Artists tab's) lands a portrait for a shown artist.
/// </summary>
public class HomeTopArtistPortraitsTests
{
    /// <summary>Deezer stand-in: requests for the hang name wait until released; "Xavi" is
    /// found and his photo downloads; nobody else is on Deezer.</summary>
    private sealed class DeezerStub : HttpMessageHandler
    {
        private readonly string _hangName;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DeezerStub(string hangName) => _hangName = hangName;

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                if (query.Contains(_hangName, StringComparison.OrdinalIgnoreCase))
                {
                    Reached.TrySetResult();
                    await _release.Task;
                    return Json("""{ "data": [] }""");
                }
                return query.Contains("Xavi", StringComparison.OrdinalIgnoreCase)
                    ? Json("""{ "data": [ { "name": "Xavi", "picture_xl": "https://images.example/Xavi.jpg" } ] }""")
                    : Json("""{ "data": [] }""");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Jpeg(0x44)) { Headers = { ContentType = new("image/jpeg") } }
            };
        }

        private static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static byte[] Jpeg(byte fill)
    {
        // Over the 5 KB Last.fm-placeholder purge, with JPEG magic bytes.
        var bytes = new byte[6 * 1024];
        Array.Fill(bytes, fill);
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        return bytes;
    }

    private static Artist LibraryArtist(string name)
        => new() { Id = LibraryService.ComputeArtistId(name), Name = name, TrackCount = 1, AlbumCount = 1 };

    private static Track PlayedTrack(string artist) => new()
    {
        Id = Guid.NewGuid(), Title = artist + " song", Artist = artist, AlbumArtist = artist, Album = artist + " album",
        AlbumId = Guid.NewGuid(), Duration = TimeSpan.FromSeconds(180), FilePath = $"C:/m/{artist}.mp3", PlayCount = 5,
    };

    private sealed record Setup(HomeViewModel Vm, FakeLibraryService Lib, ArtistImageService Images, DeezerStub Deezer,
        TestPersistenceService Persistence, Task BusySweep) : IDisposable
    {
        public string ArtworkDir => Path.Combine(Persistence.DataDirectory, "artwork", "artists");
        public void Dispose() => Persistence.Dispose();
    }

    /// <summary>Home over a library that played <paramref name="played"/>, while the Artists
    /// tab's sweep is stuck on the network (a day-old portrait of "Busy" being re-checked)
    /// and so holds the service's fetch gate.</summary>
    private static async Task<Setup> Build(string played, params Artist[] sweepAlsoCovers)
    {
        var persistence = new TestPersistenceService();
        var deezer = new DeezerStub("Busy");
        var images = new ArtistImageService(new HttpClient(deezer), persistence);
        var dir = Path.Combine(persistence.DataDirectory, "artwork", "artists");

        var busy = LibraryArtist("Busy");
        File.WriteAllBytes(images.GetCachedImagePath(busy.Id), Jpeg(0x11));
        var src = Path.Combine(dir, $"{busy.Id}.src");
        File.WriteAllText(src, "v3\nhttps://images.example/busy.jpg\n");
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow - ArtistImageService.RefreshInterval - TimeSpan.FromHours(1));
        var busySweep = images.FetchAndCacheAsync(new[] { busy }.Concat(sweepAlsoCovers).ToList());
        await deezer.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var lib = new FakeLibraryService();
        lib.TrackList.Add(PlayedTrack(played));
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib), artistImages: images);
        return new Setup(vm, lib, images, deezer, persistence, busySweep);
    }

    private static void FreshPortrait(Setup s, Guid id)
    {
        File.WriteAllBytes(s.Images.GetCachedImagePath(id), Jpeg(0x22));
        File.WriteAllText(Path.Combine(s.ArtworkDir, $"{id}.src"), "v3\nhttps://images.example/fresh.jpg\n");
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Finish(Setup s)
    {
        s.Deezer.Release();
        await s.BusySweep;
        s.Dispose();
    }

    [AvaloniaFact]
    public async Task Row_IsBuiltWithTheCachedPortrait_WhileAnotherSweepHoldsTheGate()
    {
        var s = await Build("Andre");
        try
        {
            var andre = LibraryArtist("Andre");
            s.Lib.ArtistList.Add(andre);
            FreshPortrait(s, andre.Id);
            var expected = s.Images.GetCachedImagePath(andre.Id);
            // What each Reset hands the item containers is what the tiles paint.
            var paintedWith = new List<string?>();
            s.Vm.TopArtists.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                    paintedWith.Add(s.Vm.TopArtists.FirstOrDefault()?.ImagePath);
            };

            await s.Vm.RefreshAsync();

            Assert.Same(andre, Assert.Single(s.Vm.TopArtists));
            Assert.Equal(expected, Assert.Single(paintedWith));
        }
        finally { await Finish(s); }
    }

    [AvaloniaFact]
    public async Task ScanRebuild_NewArtistInstance_ShowsThePortraitOnTheTileItReplaces()
    {
        // A scan publish rebuilds every Artist object (same Id, ImagePath null). The row
        // used to keep the old instances (same Ids) while the fetch filled in the new
        // ones, so the shown tiles never got the portrait.
        var s = await Build("Andre");
        try
        {
            var before = LibraryArtist("Andre");
            s.Lib.ArtistList.Add(before);
            await s.Vm.RefreshAsync();
            Assert.Null(Assert.Single(s.Vm.TopArtists).ImagePath); // nothing on disk yet
            // Let that fetch finish empty-handed (Deezer has no "Andre"). The gate is
            // first come, first served, so an empty sweep queued now ends after it.
            s.Deezer.Release();
            await s.BusySweep;
            await s.Images.FetchAndCacheAsync(Array.Empty<Artist>());
            Assert.Null(Assert.Single(s.Vm.TopArtists).ImagePath);

            // The Artists tab's sweep then caches the portrait, and a scan publish
            // hands Home a new instance of the same artist.
            var after = LibraryArtist("Andre");
            FreshPortrait(s, after.Id);
            s.Lib.ArtistList.Clear();
            s.Lib.ArtistList.Add(after);
            s.Vm.MarkDirty();
            await s.Vm.RefreshAsync();
            await s.Images.FetchAndCacheAsync(Array.Empty<Artist>());

            var expected = s.Images.GetCachedImagePath(after.Id);
            Assert.Equal(expected, after.ImagePath); // the fetch did find it
            await PumpUntil(() => s.Vm.TopArtists.FirstOrDefault()?.ImagePath == expected, 3000);
            Assert.Equal(expected, Assert.Single(s.Vm.TopArtists).ImagePath);
        }
        finally { await Finish(s); }
    }

    [AvaloniaFact]
    public async Task PortraitLandedByAnotherSweep_RepaintsTheRow()
    {
        // The Artists tab's sweep downloads the portrait of an artist Home shows (the same
        // library instance). Home's own fetch then finds the path already set, so it is
        // never told: the row has to repaint once its fetch is done.
        var xavi = LibraryArtist("Xavi");
        var s = await Build("Xavi", sweepAlsoCovers: xavi);
        try
        {
            s.Lib.ArtistList.Add(xavi);
            await s.Vm.RefreshAsync();
            Assert.Null(Assert.Single(s.Vm.TopArtists).ImagePath);

            var expected = s.Images.GetCachedImagePath(xavi.Id);
            var resets = 0;
            string? repaintedWith = null;
            s.Vm.TopArtists.CollectionChanged += (_, e) =>
            {
                if (e.Action != NotifyCollectionChangedAction.Reset) return;
                resets++;
                repaintedWith = s.Vm.TopArtists.FirstOrDefault()?.ImagePath;
            };

            s.Deezer.Release(); // the Artists sweep moves on and downloads Xavi
            await s.BusySweep;
            Assert.Equal(expected, xavi.ImagePath);

            await PumpUntil(() => repaintedWith == expected, 5000);
            Assert.True(resets > 0, "the row never re-materialized");
            Assert.Equal(expected, repaintedWith);
        }
        finally { await Finish(s); }
    }
}
