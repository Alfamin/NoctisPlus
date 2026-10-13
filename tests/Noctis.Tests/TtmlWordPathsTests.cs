using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #132 follow-up ("the player still shows normal lines rather than highlighting
/// words"): one word-timed Apple TTML (itunes:timing="Word", syllable spans, background
/// vocals, two singers, overlapping lines) taken through every way it can reach the lyrics
/// page — a .ttml beside the song, pasted into Edit Info, imported in Edit Info, Lyrics
/// Studio, the audio file's own lyrics tag — and what the page's lines carry afterwards.
/// A line-timed TTML keeps line sync and never gains made-up words.
/// </summary>
public class TtmlWordPathsTests : IDisposable
{
    // Made-up lyrics. Line 1 (singer v1) runs 1–5 s, its background vocal to 5 s; line 2
    // (singer v2) starts at 3.5 s while line 1 is still sung; line 3 stands alone.
    internal const string WordTtml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Word" xml:lang="en">
          <head><metadata>
            <ttm:agent type="person" xml:id="v1"/>
            <ttm:agent type="person" xml:id="v2"/>
          </metadata></head>
          <body dur="0:30.000">
            <div begin="1.000" end="10.000">
              <p begin="1.000" end="5.000" itunes:key="L1" ttm:agent="v1"><span begin="1.000" end="1.400">Paper</span> <span begin="1.400" end="1.800">lan</span><span begin="1.800" end="2.300">terns</span> <span begin="2.300" end="4.000">drifting</span><span ttm:role="x-bg"><span begin="4.000" end="4.400">(oh</span> <span begin="4.400" end="5.000">oh)</span></span></p>
              <p begin="3.500" end="7.000" itunes:key="L2" ttm:agent="v2"><span begin="3.500" end="4.200">Silver</span> <span begin="4.200" end="5.000">mar</span><span begin="5.000" end="5.600">ma</span><span begin="5.600" end="7.000">lade</span></p>
              <p begin="8.000" end="10.000" itunes:key="L3" ttm:agent="v1"><span begin="8.000" end="9.000">Quiet</span> <span begin="9.000" end="10.000">harbor</span></p>
            </div>
          </body>
        </tt>
        """;

    // The same song timed by line only, as in the issue's files.
    internal const string LineTtml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Line" xml:lang="en">
          <head><metadata>
            <ttm:agent type="person" xml:id="v1"/>
            <ttm:agent type="person" xml:id="v2"/>
          </metadata></head>
          <body dur="0:30.000">
            <div begin="1.000" end="10.000">
              <p begin="1.000" end="5.000" itunes:key="L1" ttm:agent="v1">Paper lanterns drifting</p>
              <p begin="3.500" end="7.000" itunes:key="L2" ttm:agent="v2">Silver marmalade</p>
              <p begin="8.000" end="10.000" itunes:key="L3" ttm:agent="v1">Quiet harbor</p>
            </div>
          </body>
        </tt>
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "ttml-paths-" + Guid.NewGuid().ToString("N"));

    public TtmlWordPathsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── What the lyrics page must end up with ──

    private static List<LyricLine> Sung(IEnumerable<LyricLine>? lines)
    {
        Assert.NotNull(lines);
        return lines!.Where(l => !l.IsIntroPlaceholder && l.Text != "...").ToList();
    }

    /// <summary>Karaoke: words with each syllable's clock, the background row, both singers,
    /// and lines 1 and 2 lit together while both are sung.</summary>
    private static void AssertKaraoke(IEnumerable<LyricLine>? loaded)
    {
        var lines = Sung(loaded);
        Assert.Equal(new[] { "Paper lanterns drifting", "Silver marmalade", "Quiet harbor" }, lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.True(l.HasWords, $"no word timing on \"{l.Text}\""));

        Assert.Equal(new[] { "Paper", "lanterns", "drifting" }, lines[0].Words!.Select(w => w.Text.Trim()));
        var lanterns = lines[0].Words![1];
        Assert.Equal(TimeSpan.FromSeconds(1.4), lanterns.Start);
        Assert.Equal(new[] { 1.4, 1.8 }, lanterns.Syllables!.Select(s => s.Start.TotalSeconds));
        Assert.Equal(new[] { "Silver", "marmalade" }, lines[1].Words!.Select(w => w.Text.Trim()));
        Assert.Equal(3, lines[1].Words![1].Syllables!.Count);

        Assert.Equal(new[] { "(oh", "oh)" }, lines[0].BackgroundWords!.Select(w => w.Text.Trim()));
        Assert.Equal(TimeSpan.FromSeconds(5), lines[0].BackgroundWords![^1].End);

        Assert.Equal(new[] { LyricVoice.Default, LyricVoice.Voice2, LyricVoice.Default }, lines.Select(l => l.Voice));
        Assert.True(LitTogether(lines, 4.5), "the overlapping lines should light together");
    }

    /// <summary>Line sync only: line clocks, no invented words, both singers.</summary>
    private static List<LyricLine> AssertLineSync(IEnumerable<LyricLine>? loaded)
    {
        var lines = Sung(loaded);
        Assert.Equal(new[] { "Paper lanterns drifting", "Silver marmalade", "Quiet harbor" }, lines.Select(l => l.Text));
        Assert.Equal(new[] { 1.0, 3.5, 8.0 }, lines.Select(l => l.Timestamp!.Value.TotalSeconds));
        Assert.All(lines, l => Assert.False(l.HasWords));
        Assert.Equal(new[] { LyricVoice.Default, LyricVoice.Voice2, LyricVoice.Default }, lines.Select(l => l.Voice));
        return lines;
    }

    private static bool LitTogether(IReadOnlyList<LyricLine> lines, double seconds)
    {
        var timeline = new LyricsTimeline(lines, TimeSpan.Zero, TimeSpan.Zero);
        timeline.Update(TimeSpan.FromSeconds(seconds));
        return timeline.ActiveIndices.Count >= 2;
    }

    private static LyricsViewModel.LocalLyricsProbe Probe(Track track) =>
        LyricsViewModel.ProbeLocalLyricSources(track, joinSplitWords: true, uiLanguage: "en");

    /// <summary>What the desktop page shows for a track: the probe's lines, or — for lyrics kept
    /// on the track itself ("Embedded") — its synced text, or LRC in its lyrics field, through
    /// the same LRC parser (LoadEmbeddedLyrics).</summary>
    private static List<LyricLine>? PageLines(Track track)
    {
        var probe = Probe(track);
        if (probe.Lines != null) return probe.Lines;
        Assert.Equal("Embedded", probe.Source);
        var synced = !string.IsNullOrWhiteSpace(track.SyncedLyrics) ? track.SyncedLyrics
            : LrcParser.ContainsTimestamp(track.Lyrics) ? track.Lyrics
            : null;
        return synced == null ? null : LyricsViewModel.ParseLrcContent(synced);
    }

    private string Song(string ext = "flac", Action<TagLib.File>? tag = null) =>
        EmbeddedLyricsTagTests.Copy(_dir, ext, tag, name: "song");

    private async Task<(MetadataViewModel Vm, Track Track)> OpenEditInfo(string path, LyricsSaveTarget target)
    {
        var track = new MetadataService().ReadTrackMetadata(path)!;
        var library = new FakeLibraryService();
        library.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new MetadataService(), library, new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            TrashFile = p => { File.Delete(p); return true; },
            LyricsSaveTarget = () => target,
        };
        await vm.InitializeAsync();
        return (vm, track);
    }

    // ── (a) A .ttml beside the song ──

    [Fact]
    public void Sidecar_WordTimed_ShowsKaraoke()
    {
        var path = Song();
        File.WriteAllText(Path.ChangeExtension(path, ".ttml"), WordTtml);
        var track = new MetadataService().ReadTrackMetadata(path)!;

        var probe = Probe(track);

        Assert.Equal("Sidecar:Ttml", probe.Source);
        AssertKaraoke(probe.Lines);
    }

    [Fact]
    public void Sidecar_LineTimed_KeepsLineSync_AndOverlap_WithoutWords()
    {
        var path = Song();
        File.WriteAllText(Path.ChangeExtension(path, ".ttml"), LineTtml);
        var track = new MetadataService().ReadTrackMetadata(path)!;

        var lines = AssertLineSync(Probe(track).Lines);

        Assert.Equal(TimeSpan.FromSeconds(5), lines[0].EndTimestamp);
        Assert.True(LitTogether(lines, 4.5));
    }

    // ── (c) A .ttml imported in Edit Info ──

    [Theory]
    [InlineData(LyricsSaveTarget.Sidecar)]
    [InlineData(LyricsSaveTarget.Embedded)]
    [InlineData(LyricsSaveTarget.Both)]
    public async Task EditInfo_ImportedWordTimedTtml_ThePageShowsAsKaraoke(LyricsSaveTarget target)
    {
        var path = Song();
        var (vm, track) = await OpenEditInfo(path, target);

        vm.ImportLyricsText(WordTtml, "song.ttml");
        Assert.True(vm.HasWordTimedLines);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        AssertKaraoke(PageLines(track));
        AssertKaraoke(PageLines(new MetadataService().ReadTrackMetadata(path)!));
    }

    [Fact]
    public async Task EditInfo_ImportedLineTimedTtml_KeepsLineSync_WithoutWords()
    {
        var path = Song();
        var (vm, track) = await OpenEditInfo(path, LyricsSaveTarget.Sidecar);

        vm.ImportLyricsText(LineTtml, "song.ttml");
        await vm.SaveCommand.ExecuteAsync(null);

        AssertLineSync(PageLines(track));
    }

    // ── (d) Lyrics Studio ──

    [Fact]
    public void Studio_ImportBox_TakesTheSungLines_ForRetiming()
    {
        // The Studio's lyrics box is the text to time; its own timing is dropped by design.
        Assert.Equal(new[] { "Paper lanterns drifting", "Silver marmalade", "Quiet harbor" },
            LyricsStudioEngine.ImportText(WordTtml, "song.ttml"));
    }

    // ── (e) TTML kept in the audio file's lyrics tag ──

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    public void LyricsTag_HoldingWordTimedTtml_ShowsKaraoke_NotMarkup(string ext)
    {
        var path = Song(ext, f => f.Tag.Lyrics = WordTtml);
        var track = new MetadataService().ReadTrackMetadata(path)!;
        Assert.Contains("<tt", track.Lyrics);

        var probe = Probe(track);

        AssertKaraoke(probe.Lines);
        Assert.Equal("Paper lanterns drifting\nSilver marmalade\nQuiet harbor", probe.UnsyncedPlain);
    }

    [Fact]
    public void LyricsTag_HoldingLineTimedTtml_KeepsLineSync()
    {
        var path = Song(tag: f => f.Tag.Lyrics = LineTtml);
        var track = new MetadataService().ReadTrackMetadata(path)!;

        var lines = AssertLineSync(Probe(track).Lines);
        Assert.True(LitTogether(lines, 4.5));
    }

    [Fact]
    public void Phone_LyricsFieldHoldingTtml_ShowsKaraoke()
    {
        var track = new Track { Id = Guid.NewGuid(), FilePath = "content://x/tree/t/document/primary%3AMusic%2Fsong.flac", Lyrics = WordTtml };

        var loaded = LyricsLoader.Load(track, new FakeTrackFiles(), joinSplitWords: true);

        Assert.True(loaded.IsSynced);
        AssertKaraoke(loaded.Lines);
    }
}
