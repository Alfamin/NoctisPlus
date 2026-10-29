using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Overlapping lines (a duet's second voice starting before the first ends) light
/// together on the desktop lyrics page: both at full opacity and no blur, the ramp
/// measured from the nearer lit line, while ActiveLineIndex stays the newest line so
/// the scroll anchor does not move differently than before.
/// </summary>
public class LyricsOverlapLinesTests
{
    // ── Harness ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    /// <summary>Word-timed ELRC: line 0 sings 1–4 s, line 1 starts at 3 s (a second of
    /// overlap), then two lines on their own.</summary>
    private const string OverlapElrc =
        "[00:01.00]<00:01.00>one <00:02.00>two <00:04.00>\n" +
        "[00:03.00]<00:03.00>three <00:05.00>\n" +
        "[00:08.00]<00:08.00>four <00:09.00>\n" +
        "[00:10.00]<00:10.00>five <00:11.00>";

    private static async Task<(LyricsViewModel Vm, PlayerViewModel Player)> MountSyncedLyrics()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());

        player.Duration = TimeSpan.FromSeconds(20);
        player.CurrentTrack = new Track
        {
            Title = "Overlap",
            Artist = "Test",
            FilePath = Path.Combine(Path.GetTempPath(), "noctis-overlap-lines-no-such-file.mp3"),
            SyncedLyrics = OverlapElrc,
        };
        vm.SetLyricsSurfaceVisible(true);
        vm.EnsureLyricsForCurrentTrack();

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && !vm.IsSynced)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.True(vm.IsSynced, "harness failed to load synced lyrics");
        return (vm, player);
    }

    // ── Tests ──

    [AvaloniaFact]
    public async Task OverlappingLines_AreBothLitCrisp_AndTheRampRunsFromTheNearer()
    {
        var (vm, player) = await MountSyncedLyrics();

        player.Position = TimeSpan.FromSeconds(3.5);

        Assert.Equal(1, vm.ActiveLineIndex);                  // the scroll anchor: the newest line
        var lines = vm.LyricLines;
        Assert.True(lines[0].IsActive);
        Assert.True(lines[1].IsActive);
        Assert.Equal(1.0, lines[0].LineOpacity, 6);
        Assert.Equal(0.0, lines[0].BlurRadius, 6);
        Assert.Equal(1.0, lines[1].LineOpacity, 6);
        Assert.Equal(0.0, lines[1].BlurRadius, 6);
        Assert.Equal(0.55, lines[2].LineOpacity, 6);
        Assert.Equal(4.0, lines[2].BlurRadius, 6);
    }

    [AvaloniaFact]
    public async Task WhenTheEarlierVoiceEnds_ItDimsWhileTheActiveLineStays()
    {
        var (vm, player) = await MountSyncedLyrics();
        player.Position = TimeSpan.FromSeconds(3.5);
        Assert.Equal(1.0, vm.LyricLines[0].LineOpacity, 6);

        player.Position = TimeSpan.FromSeconds(4.3);

        Assert.Equal(1, vm.ActiveLineIndex);
        Assert.False(vm.LyricLines[0].IsActive);
        Assert.Equal(0.55, vm.LyricLines[0].LineOpacity, 6);
        Assert.Equal(4.0, vm.LyricLines[0].BlurRadius, 6);
        Assert.Equal(1.0, vm.LyricLines[1].LineOpacity, 6);
    }
}
