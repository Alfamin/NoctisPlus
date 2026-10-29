using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The lyrics page's tap-sync editor (Edit lyrics timing) on a word-timed song with a
/// background vocal and a second singer: what it is seeded with, and what its save leaves in
/// the sidecar the page reads back. Made-up lyrics.
/// </summary>
public class LyricsEditorRoundTripTests : IDisposable
{
    // Line 1 word-timed with a "[bg: …]" vocal under it, line 2 the second singer, line 3 plain.
    private const string Elrc =
        "[00:01.00]<00:01.00>Paper <00:01.50>lanterns<00:02.00>\n" +
        "[bg: <00:02.00>(oh <00:02.40>oh)<00:03.00>]\n" +
        "[00:04.00]v2:<00:04.00>Silver <00:04.60>moon<00:05.00>\n" +
        "[00:06.00]Quiet harbor";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-editor-rt-" + Guid.NewGuid().ToString("N"));

    public LyricsEditorRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

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

    private string SongPath => Path.Combine(_dir, "song.mp3");
    private string SidecarPath => Path.Combine(_dir, "song.lrc");

    private static PlayerViewModel Player() =>
        new(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());

    /// <summary>The lyrics page with <see cref="Elrc"/> loaded from the .lrc beside the song.</summary>
    private async Task<(LyricsViewModel Vm, PlayerViewModel Player)> LoadPage()
    {
        await File.WriteAllTextAsync(SidecarPath, Elrc);
        var player = Player();
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
        player.Duration = TimeSpan.FromSeconds(10);
        player.CurrentTrack = new Track { Title = "Song", Artist = "Test", FilePath = SongPath };
        vm.SetLyricsSurfaceVisible(true);
        vm.EnsureLyricsForCurrentTrack();
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && !(vm.IsSynced && vm.LyricLines.Count > 0))
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(vm.IsSynced, "the sidecar should load as synced lyrics");
        return (vm, player);
    }

    /// <summary>Word timing on line 1, its background vocal, and the second singer on line 2.</summary>
    private static void AssertKeepsWordsBackgroundAndVoices(string lrc)
    {
        var lines = LrcParser.Parse(lrc).Where(l => !l.IsIntroPlaceholder).ToList();
        Assert.Equal(new[] { "Paper lanterns", "Silver moon", "Quiet harbor" }, lines.Select(l => l.Text));
        Assert.Equal(new[] { "Paper", "lanterns" }, lines[0].Words?.Select(w => w.Text.Trim()));
        Assert.Equal(TimeSpan.FromSeconds(1.5), lines[0].Words![1].Start);
        Assert.Equal(new[] { "(oh", "oh)" }, lines[0].BackgroundWords?.Select(w => w.Text.Trim()));
        Assert.Equal(TimeSpan.FromSeconds(2.4), lines[0].BackgroundWords![1].Start);
        Assert.Equal(new[] { LyricVoice.Default, LyricVoice.Voice2, LyricVoice.Default }, lines.Select(l => l.Voice));
        Assert.Equal(new[] { "Silver", "moon" }, lines[1].Words?.Select(w => w.Text.Trim()));
    }

    [AvaloniaFact]
    public async Task Seed_KeepsWordTiming_BackgroundVocals_AndVoices()
    {
        // The editor is seeded from the lines on screen. They were written back as
        // "[mm:ss.xx]text" only, so saving from the editor — even without touching a line —
        // replaced a word-timed sidecar with line timing and lost the "[bg: …]" vocal and the
        // "v2:" singer.
        var (vm, _) = await LoadPage();

        var seed = vm.BuildSeedFromLoadedLines();

        Assert.NotNull(seed);
        AssertKeepsWordsBackgroundAndVoices(seed!);
    }

    [AvaloniaFact]
    public async Task Save_AfterRetimingALine_KeepsTheBackgroundVocals()
    {
        // The editor's rows come from ParseLrc, which takes a "[bg: …]" line for a header tag
        // ([ar:], [ti:]) and leaves it out — so the save wrote the song without its
        // background vocal.
        var editor = new LrcEditorViewModel(new Track { Title = "Song", Artist = "Test", FilePath = SongPath },
            Player(), new StubMetadata(), Elrc, null) { SaveTarget = () => LyricsSaveTarget.Sidecar };

        editor.NudgeForwardCommand.Execute(editor.Lines[^1]);
        await editor.SaveCommand.ExecuteAsync(null);

        var saved = await File.ReadAllTextAsync(SidecarPath);
        Assert.Contains("[00:06.10]Quiet harbor", saved);
        AssertKeepsWordsBackgroundAndVoices(saved);
    }

    [AvaloniaFact]
    public async Task OpenedFromTheLyricsPage_SavedUntouched_TheSidecarLoadsTheSame()
    {
        var (vm, player) = await LoadPage();
        var editor = new LrcEditorViewModel(player.CurrentTrack!, player, new StubMetadata(),
            vm.BuildSeedFromLoadedLines(), null) { SaveTarget = () => LyricsSaveTarget.Sidecar };

        await editor.SaveCommand.ExecuteAsync(null);

        AssertKeepsWordsBackgroundAndVoices(await File.ReadAllTextAsync(SidecarPath));
    }
}
