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
    [InlineData("https://music.apple.com/us/song/la-dificil/1500776327")]
    [InlineData("https://music.apple.com/us/album/1000000-ep/1628626875")]
    [InlineData("HIT ME HARD AND SOFT Billie Eilish")]
    [InlineData("Die With A Smile Lady Gaga")]
    [InlineData("Short n' Sweet")]
    [InlineData("Short n' Sweet Sabrina Carpenter")]
    [InlineData("https://music.apple.com/gb/album/short-n-sweet-deluxe/1795512297")]
    [InlineData("https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb")]
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
            _out.WriteLine($"   [{r.Score:0.#}] \"{r.CollectionName}\" by {r.ArtistName} ({r.Detail}) " +
                           string.Join(", ", r.Choices.Select(c => $"{c.Label} {c.Variant.Width}x{c.Variant.Height} {c.Variant.Codec}")) +
                           (r.Album.HasTall ? " +tall" : string.Empty));
        foreach (var line in DebugLog.Snapshot().Split('\n')
                     .Where(l => l.Contains("[AnimatedArt]") && !before.Contains(l)))
            _out.WriteLine("   log: " + line);
    }
}
