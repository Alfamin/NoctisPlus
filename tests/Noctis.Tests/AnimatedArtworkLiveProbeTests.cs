using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Runs the Edit Info → Animated Artwork search against Apple's live endpoints, exactly as the
/// dialog does. Off by default (network); set NOCTIS_LIVE_ANIMATED=1 to run it, and read the
/// per-query outcome in the test output. The offline fixtures in AnimatedArtworkSearchFlowTests
/// are the regression cover; this is the tool for re-checking Apple when they change something.
/// </summary>
public class AnimatedArtworkLiveProbeTests
{
    private readonly ITestOutputHelper _out;

    public AnimatedArtworkLiveProbeTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("YHLQMDLG")]
    [InlineData("YHLQMDLG Bad Bunny")]
    [InlineData("Happier Than Ever Billie Eilish")]
    [InlineData("MAYHEM Lady Gaga")]
    [InlineData("https://music.apple.com/us/album/yhlqmdlg/1500776322")]
    [InlineData("1500776322")]
    [InlineData("HIT ME HARD AND SOFT Billie Eilish")]
    [InlineData("Die With A Smile Lady Gaga")]
    public async Task TypedQuery(string typed)
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_LIVE_ANIMATED") != "1")
            return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var p = new TestPersistenceService();
        var track = new Track { Title = "x", Artist = "Someone Else", Album = "Unrelated", FilePath = "C:\\nope.mp3" };
        var vm = new MetadataViewModel(track, new MetadataService(), new FakeLibraryService(), p,
            new FakeAnimatedCoverService(), itunes: new ITunesArtworkService(http));

        vm.AnimatedSearchInput = typed;
        var before = new HashSet<string>(DebugLog.Snapshot().Split('\n'));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);

        _out.WriteLine($"'{typed}' -> {vm.AnimatedArtworkSearchResults.Count} result(s) in {sw.ElapsedMilliseconds} ms, " +
                       $"status '{vm.AnimatedSearchStatus}'");
        foreach (var r in vm.AnimatedArtworkSearchResults)
            _out.WriteLine($"   {r.Variant.Label} {r.Variant.Width}x{r.Variant.Height} {r.Variant.Codec}");
        foreach (var line in DebugLog.Snapshot().Split('\n')
                     .Where(l => l.Contains("[AnimatedArt]") && !before.Contains(l)))
            _out.WriteLine("   log: " + line);
    }
}
