using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Re-timing one line in the lyrics editors — the lyrics page's tap-sync editor and Edit Info's
/// Timestamp Lyrics — when that line is word-timed or carries a background vocal. Made-up lyrics.
/// </summary>
public class LyricsRestampTests : IDisposable
{
    // Line 1 word-timed with a "[bg: …]" vocal under it, line 2 the second singer, line 3 plain.
    private const string Elrc =
        "[00:01.00]<00:01.00>Paper <00:01.50>lanterns<00:02.00>\n" +
        "[bg: <00:02.00>(oh <00:02.40>oh)<00:03.00>]\n" +
        "[00:04.00]v2:<00:04.00>Silver <00:04.60>moon<00:05.00>\n" +
        "[00:06.00]Quiet harbor";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-restamp-" + Guid.NewGuid().ToString("N"));

    public LyricsRestampTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
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

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private (LrcEditorViewModel Editor, PlayerViewModel Player) TapSync(string lrc)
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var editor = new LrcEditorViewModel(new Track { Title = "Song", Artist = "Test", FilePath = SongPath },
            player, new StubMetadata(), lrc, null) { SaveTarget = () => LyricsSaveTarget.Sidecar };
        return (editor, player);
    }

    private async Task<List<LyricLine>> SaveAndRead(LrcEditorViewModel editor)
    {
        await editor.SaveCommand.ExecuteAsync(null);
        return LrcParser.Parse(await File.ReadAllTextAsync(SidecarPath)).Where(l => !l.IsIntroPlaceholder).ToList();
    }

    private static async Task<MetadataViewModel> EditInfo(string lrc)
    {
        var track = new Track { Id = Guid.NewGuid(), Title = "T", Artist = "A", Album = "X", FilePath = "C:/nowhere/t.flac" };
        var library = new FakeLibraryService();
        library.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new StubMetadata(), library, new TestPersistenceService(), new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);
        await vm.InitializeAsync();
        vm.ImportLyricsText(lrc, "song.lrc");
        return vm;
    }

    private static List<LyricLine> Parsed(string lrc) =>
        LrcParser.Parse(lrc).Where(l => !l.IsIntroPlaceholder).ToList();

    private static void AssertLineOneAt(LyricLine line, double start)
    {
        Assert.Equal(S(start), line.Timestamp);
        Assert.Equal(new[] { S(start), S(start + 0.5) }, line.Words!.Select(w => w.Start));
        Assert.Equal(new[] { S(start + 1.0), S(start + 1.4) }, line.BackgroundWords!.Select(w => w.Start));
    }

    // ── Bug: a re-stamped word-timed line left its word tags at the old times ──

    [AvaloniaFact]
    public async Task TapSync_NudgingAWordTimedLine_MovesItsWordsAndBackgroundVocal()
    {
        // Nudging moved only "[mm:ss.xx]"; the "<mm:ss.xx>" word tags and the "[bg: …]" tags
        // stayed put, so after save the words swept before (or after) the line came up.
        var (editor, _) = TapSync(Elrc);
        for (var i = 0; i < 5; i++) editor.NudgeForwardCommand.Execute(editor.Lines[0]);

        var lines = await SaveAndRead(editor);

        AssertLineOneAt(lines[0], 1.5);
    }

    [AvaloniaFact]
    public async Task TapSync_StampingAWordTimedLine_MovesItsWords()
    {
        var (editor, player) = TapSync(Elrc);
        player.Position = S(3.5);
        editor.StampLineCommand.Execute(editor.Lines[1]);

        var lines = await SaveAndRead(editor);

        Assert.Equal(S(3.5), lines[1].Timestamp);
        Assert.Equal(LyricVoice.Voice2, lines[1].Voice);
        Assert.Equal(new[] { S(3.5), S(4.1) }, lines[1].Words!.Select(w => w.Start));
    }

    [AvaloniaFact]
    public async Task TapSync_NudgingBackPastZero_ClampsTheWordsAndKeepsTheirOrder()
    {
        var (editor, _) = TapSync("[00:00.10]<00:00.10>Tiny <00:00.15>start<00:00.40>\n[00:02.00]Next");
        editor.NudgeBackCommand.Execute(editor.Lines[0]);
        editor.NudgeBackCommand.Execute(editor.Lines[0]);

        var lines = await SaveAndRead(editor);

        Assert.Equal(TimeSpan.Zero, lines[0].Timestamp);
        Assert.Equal(new[] { S(0), S(0.05) }, lines[0].Words!.Select(w => w.Start));
    }

    [Fact]
    public async Task EditInfo_NudgingAWordTimedLine_MovesItsWordsAndBackgroundVocal()
    {
        var vm = await EditInfo(Elrc);
        for (var i = 0; i < 5; i++) vm.SyncedLyricLines[0].Nudge(forward: true);

        Assert.StartsWith("[00:01.50]<00:01.50>Paper <00:02.00>lanterns<00:02.50>\n[bg: <00:02.50>(oh <00:02.90>oh)<00:03.50>]\n", vm.SyncedLyrics);
        AssertLineOneAt(Parsed(vm.SyncedLyrics)[0], 1.5);
    }

    [Fact]
    public async Task EditInfo_TypingATimestampOnAWordTimedLine_MovesItsWords()
    {
        var vm = await EditInfo(Elrc);
        var row = vm.SyncedLyricLines.Single(l => l.DisplayText == "Silver moon");

        row.TimestampText = "0:03.50";

        Assert.Contains("[00:03.50]v2:<00:03.50>Silver <00:04.10>moon<00:04.50>", vm.SyncedLyrics);
    }

    [Fact]
    public async Task EditInfo_ShiftAll_MovesWordAndBackgroundTags()
    {
        // Guard: "Shift all" rewrites the text with every tag moved — words and [bg: …] too.
        var vm = await EditInfo(Elrc);
        vm.ShiftAllSeconds = "0.5";
        vm.ShiftAllLaterCommand.Execute(null);

        AssertLineOneAt(Parsed(vm.SyncedLyrics)[0], 1.5);
    }

    [Fact]
    public void ShiftWordTimestamps_MovesOnlyWordTags_ClampedAtZero()
    {
        Assert.Equal("[00:01.00]<00:00.00>a <00:00.10>b",
            LyricsTextHelper.ShiftWordTimestamps("[00:01.00]<00:00.05>a <00:00.30>b", S(-0.2)));
        Assert.Equal("[bg: <00:02.50>(oh)<00:03.00>]", LyricsTextHelper.ShiftWordTimestamps("[bg: <00:02.00>(oh)<00:02.50>]", S(0.5)));
        Assert.Equal("<00:01.234>x", LyricsTextHelper.ShiftWordTimestamps("<00:01.234>x", TimeSpan.Zero));
    }

    // ── Bug: re-stamping a line left its romanization / translation rows behind ──

    // A sung line with its romanization and translation at its stamp (GitHub #116), twice.
    private const string WithCompanions =
        "[00:01.00]Hoshi no michi\n" +
        "[00:01.00]hoshi no michi\n" +
        "[00:01.00]Path of stars\n" +
        "[00:04.00]Tsuki akari\n" +
        "[00:04.00]Moonlight";

    private static void AssertFirstLineWithItsCompanionsAt(List<LyricLine> lines, double start)
    {
        Assert.Equal(new[] { "Hoshi no michi", "Tsuki akari" }, lines.Select(l => l.Text));
        Assert.Equal(S(start), lines[0].Timestamp);
        Assert.Equal("hoshi no michi", lines[0].Transliteration);
        Assert.Equal("Path of stars", lines[0].Translation);
        Assert.Equal("Moonlight", lines[1].Translation);
    }

    [AvaloniaFact]
    public async Task TapSync_NudgingALine_ItsCompanionRowsMoveWithIt()
    {
        // The romaji and translation rows kept the old stamp, so after save they came back as
        // separate sung lines and the moved line lost them.
        var (editor, _) = TapSync(WithCompanions);
        editor.NudgeForwardCommand.Execute(editor.Lines[0]);

        Assert.Equal(new TimeSpan?[] { S(1.1), S(1.1), S(1.1), S(4), S(4) }, editor.Lines.Select(l => l.Timestamp));
        AssertFirstLineWithItsCompanionsAt(await SaveAndRead(editor), 1.1);
    }

    [AvaloniaFact]
    public async Task TapSync_StampingALine_TakesItsCompanionsAndSkipsTheirRows()
    {
        // Tap-to-sync went on to the romaji row, so the next tap stamped the romaji apart
        // from its line.
        var (editor, player) = TapSync(WithCompanions);
        editor.SelectedIndex = 0;
        player.Position = S(1.5);

        editor.StampCurrentCommand.Execute(null);

        Assert.Equal(3, editor.SelectedIndex);
        Assert.Equal(new TimeSpan?[] { S(1.5), S(1.5), S(1.5), S(4), S(4) }, editor.Lines.Select(l => l.Timestamp));
        AssertFirstLineWithItsCompanionsAt(await SaveAndRead(editor), 1.5);
    }

    [Fact]
    public async Task EditInfo_NudgingALine_ItsCompanionRowsMoveWithIt()
    {
        var vm = await EditInfo(WithCompanions);
        vm.SyncedLyricLines[0].Nudge(forward: true);

        Assert.Equal(WithCompanions.Replace("[00:01.00]", "[00:01.10]"), vm.SyncedLyrics);
        AssertFirstLineWithItsCompanionsAt(Parsed(vm.SyncedLyrics), 1.1);
    }

    [Fact]
    public async Task EditInfo_TypingATimestamp_ItsCompanionRowsMoveWithIt()
    {
        var vm = await EditInfo(WithCompanions);
        vm.SyncedLyricLines[3].TimestampText = "0:03.20";

        Assert.Equal(WithCompanions.Replace("[00:04.00]", "[00:03.20]"), vm.SyncedLyrics);
    }

    [Fact]
    public async Task EditInfo_ACompanionRowMovedOnItsOwn_StaysWhereItWasPut()
    {
        var vm = await EditInfo(WithCompanions);
        vm.SyncedLyricLines[2].TimestampText = "0:02.00";
        vm.SyncedLyricLines[0].Nudge(forward: true);

        Assert.Equal(new TimeSpan?[] { S(1.1), S(1.1), S(2), S(4), S(4) }, vm.SyncedLyricLines.Select(l => l.Timestamp));
    }

    [Fact]
    public async Task EditInfo_SungLinesAtOneStamp_AreNotCompanions()
    {
        // Guard: another singer, or a word-timed line, at the same instant is sung, not a layer.
        const string Lrc = "[00:01.00]v1:Call\n[00:01.00]v2:Answer\n[00:01.00]<00:01.00>Echo<00:01.50>\n[00:03.00]End";
        var vm = await EditInfo(Lrc);
        vm.SyncedLyricLines[0].Nudge(forward: true);

        Assert.Equal(new TimeSpan?[] { S(1.1), S(1), S(1), S(3) }, vm.SyncedLyricLines.Select(l => l.Timestamp));
    }
}
