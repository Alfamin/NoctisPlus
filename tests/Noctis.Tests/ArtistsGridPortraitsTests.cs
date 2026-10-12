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
/// GitHub #137 on the Artists grid: every portrait is on disk, but the first one in name
/// order is a day old, so the sweep re-asks Deezer for it and the request hangs. The tiles
/// must still paint every cached portrait, also across scan publishes (every 1.5 s each
/// one hands the grid fresh Artist objects and queues another sweep behind the first).
/// </summary>
public class ArtistsGridPortraitsTests
{
    private readonly ITestOutputHelper _out;
    public ArtistsGridPortraitsTests(ITestOutputHelper output) => _out = output;

    /// <summary>Every Deezer request waits until the test releases it.</summary>
    private sealed class HungNetwork : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await _release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "data": [] }""", Encoding.UTF8, "application/json")
            };
        }
    }

    private static byte[] Jpeg()
    {
        var bytes = new byte[6 * 1024];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        return bytes;
    }

    private static List<Artist> Instances(IEnumerable<string> names)
        => names.Select(n => new Artist { Id = LibraryService.ComputeArtistId(n), Name = n, TrackCount = 1, AlbumCount = 1 }).ToList();

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

    [AvaloniaFact]
    public async Task CachedPortraitsPaint_WhileTheSweepWaitsOnTheNetwork_AndAcrossScanPublishes()
    {
        using var persistence = new TestPersistenceService();
        var network = new HungNetwork();
        var images = new ArtistImageService(new HttpClient(network), persistence);
        var dir = Path.Combine(persistence.DataDirectory, "artwork", "artists");
        var names = Enumerable.Range(0, 300).Select(i => $"Artist {i:D3}").ToList();
        var jpeg = Jpeg();
        foreach (var a in Instances(names))
        {
            File.WriteAllBytes(images.GetCachedImagePath(a.Id), jpeg);
            var src = Path.Combine(dir, $"{a.Id}.src");
            File.WriteAllText(src, "v3\nhttps://images.example/a.jpg\n");
            if (a.Name == names[0]) // a day old: re-checked, and that request hangs
                File.SetLastWriteTimeUtc(src, DateTime.UtcNow - ArtistImageService.RefreshInterval - TimeSpan.FromHours(1));
        }

        var lib = new FakeLibraryService();
        lib.ArtistList.AddRange(Instances(names));
        var vm = new LibraryArtistsViewModel(lib);
        vm.SetArtistImageService(images);

        // What every Reset hands the tiles: how many shown artists have no portrait.
        var blanksPerReset = new List<int>();
        vm.ArtistRows.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                blanksPerReset.Add(vm.ArtistRows.SelectMany(r => r.Artists).Count(a => a.ImagePath == null));
        };

        try
        {
            vm.IsActive = true;
            await PumpUntil(() => blanksPerReset.Count > 0 && blanksPerReset[^1] == 0, 6000);
            _out.WriteLine("first sweep: " + string.Join(", ", blanksPerReset));
            Assert.Equal(0, blanksPerReset[^1]);

            // Three scan publishes, 1.5 s apart: fresh Artist objects for the same artists.
            var settled = blanksPerReset.Count;
            for (var publish = 0; publish < 3; publish++)
            {
                lib.ArtistList.Clear();
                lib.ArtistList.AddRange(Instances(names));
                lib.RaiseLibraryUpdated();
                await PumpUntil(() => false, 1500);
            }
            await PumpUntil(() => false, 2500);
            var afterPublishes = blanksPerReset.Skip(settled).ToList();
            _out.WriteLine("after publishes: " + string.Join(", ", afterPublishes));
            Assert.NotEmpty(afterPublishes);
            Assert.All(afterPublishes, blanks => Assert.Equal(0, blanks)); // no tile ever blanked
            Assert.All(vm.ArtistRows.SelectMany(r => r.Artists), a => Assert.NotNull(a.ImagePath));
        }
        finally
        {
            network.Release();
        }
    }

    /// <summary>Deezer with every artist on it: each search finds the asked-for name.</summary>
    private sealed class EveryoneFound : HttpMessageHandler
    {
        private readonly IReadOnlyList<string> _names;
        public EveryoneFound(IReadOnlyList<string> names) => _names = names;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://api.deezer.com/search/artist", StringComparison.Ordinal))
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var name = _names.FirstOrDefault(n => query.Contains(n, StringComparison.OrdinalIgnoreCase));
                var json = name == null
                    ? """{ "data": [] }"""
                    : $$"""{ "data": [ { "name": "{{name}}", "picture_xl": "https://images.example/{{Uri.EscapeDataString(name)}}.jpg" } ] }""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Jpeg()) { Headers = { ContentType = new("image/jpeg") } }
            });
        }
    }

    [AvaloniaFact]
    public async Task FirstDownloads_ReachTheGridWhileTheSweepIsStillRunning()
    {
        // A first sweep downloads a portrait every ~150 ms (paced). The grid's 2 s batch
        // was restarted by every one of them, so no tile repainted until the whole
        // sweep was over: minutes on a real library.
        using var persistence = new TestPersistenceService();
        var names = Enumerable.Range(0, 40).Select(i => $"Singer {i:D2}").ToList();
        var images = new ArtistImageService(new HttpClient(new EveryoneFound(names)), persistence);
        var lib = new FakeLibraryService();
        lib.ArtistList.AddRange(Instances(names));
        var vm = new LibraryArtistsViewModel(lib);
        vm.SetArtistImageService(images);

        // Per Reset: tiles painted with a portrait, and whether the sweep had finished.
        var resets = new List<(int Painted, bool SweepDone)>();
        vm.ArtistRows.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Reset) return;
            resets.Add((vm.ArtistRows.SelectMany(r => r.Artists).Count(a => a.ImagePath != null),
                lib.ArtistList.All(a => a.ImagePath != null)));
        };

        vm.IsActive = true;
        await PumpUntil(() => resets.Any(r => r.SweepDone && r.Painted == names.Count), 20000);
        _out.WriteLine("resets: " + string.Join(", ", resets.Select(r => $"{r.Painted}{(r.SweepDone ? " (done)" : "")}")));

        Assert.Contains(resets, r => r.SweepDone && r.Painted == names.Count);
        Assert.Contains(resets, r => !r.SweepDone && r.Painted > 0);
    }

    [AvaloniaFact]
    public async Task ScanPublish_RowsKeepThePortraitsAlreadyShown_BeforeAnySweepRuns()
    {
        // Without the carry-over the test above caught a race: a publish's rebuild landed
        // before its sweep had surfaced anything and every tile blanked (a Reset with 300
        // of 300 empty). With no image service at all, the rebuild alone must keep them.
        using var persistence = new TestPersistenceService();
        string PortraitOf(Artist a) => Path.Combine(persistence.DataDirectory, $"{a.Id}.jpg");
        var names = new[] { "Abba", "Coldplay", "The Beatles" };
        var lib = new FakeLibraryService();
        lib.ArtistList.AddRange(Instances(names));
        foreach (var a in lib.ArtistList)
        {
            a.ImagePath = PortraitOf(a);
            File.WriteAllBytes(a.ImagePath, Jpeg()); // on disk, so the missing-file sweep keeps it
        }
        var vm = new LibraryArtistsViewModel(lib) { IsActive = true };
        await PumpUntil(() => vm.ArtistRows.Count > 0);

        var fresh = Instances(names);
        lib.ArtistList.Clear();
        lib.ArtistList.AddRange(fresh);
        lib.RaiseLibraryUpdated();
        await PumpUntil(() => vm.ArtistRows.SelectMany(r => r.Artists).Any(a => fresh.Contains(a)));

        var shown = vm.ArtistRows.SelectMany(r => r.Artists).ToList();
        Assert.All(shown, a => Assert.Contains(a, fresh));
        Assert.All(shown, a => Assert.Equal(PortraitOf(a), a.ImagePath));
    }
}
