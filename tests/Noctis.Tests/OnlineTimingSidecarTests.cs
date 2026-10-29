using Noctis.Helpers;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>#200: an online word timing goes into the song's .ttml only, marked with its source; WordLyrics' .elrc,
/// the .lrc and the user's own .ttml are never touched.</summary>
public sealed class OnlineTimingSidecarTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-online-sidecar-" + Guid.NewGuid().ToString("N"));
    readonly LyricsWriter _writer;
    readonly string _song;

    const string Ttml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div>" +
        "<p begin=\"10.000\" end=\"11.000\"><span begin=\"10.000\" end=\"10.400\">hello</span> <span begin=\"10.500\" end=\"11.000\">you</span></p></div></body></tt>";

    public OnlineTimingSidecarTests()
    {
        Directory.CreateDirectory(_dir);
        _song = Path.Combine(_dir, "01 - Artist - Song.mp3");
        File.WriteAllBytes(_song, [1, 2, 3]);
        _writer = new LyricsWriter(null!, null, new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache"));
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(_dir, true); } catch { }
    }

    static string Marked(WordTimingSource s = WordTimingSource.LrcRed, double shift = 0.48) => OnlineTimingSidecar.Mark(Ttml, s, shift)!;

    [Fact]
    public void TheMarkNamesTheSourceAndReadsAsTheSameLyrics()
    {
        var marked = Marked();
        Assert.Contains("plus:source=\"lrc.red\" plus:shift=\"0.48\"", marked);
        Assert.Equal(WordTimingSource.LrcRed, OnlineTimingSidecar.SourceOf(marked));
        Assert.Null(OnlineTimingSidecar.SourceOf(Ttml));
        Assert.Null(OnlineTimingSidecar.Mark(marked, WordTimingSource.Unison, 0));   // never marked twice
        Assert.Equal(WordTimingSource.Kugou, OnlineTimingSidecar.SourceOf(Marked(WordTimingSource.Kugou)));
        var (before, _) = TtmlParser.Parse(Ttml);
        var (after, _) = TtmlParser.Parse(marked);
        Assert.Equal(before!.SelectMany(l => l.Words!).Select(w => (w.Text, w.Start)), after!.SelectMany(l => l.Words!).Select(w => (w.Text, w.Start)));
    }

    [Fact]
    public void TheLyricsPageReadsTheTtmlFirstAndKnowsItsSource()
    {
        File.WriteAllText(Path.ChangeExtension(_song, ".elrc"), "[00:10.00]<00:10.00>hello <00:10.50>you <00:11.00>");
        var track = new Noctis.Models.Track { FilePath = _song, Title = "Song" };
        var before = Noctis.ViewModels.LyricsViewModel.ProbeLocalLyricSources(track, true, "en");
        Assert.Equal("Sidecar:Elrc", before.Source);
        Assert.Null(before.OnlineTimingSource);
        Assert.True(_writer.WriteOnlineTiming(_song, Marked(WordTimingSource.Unison, 0)));
        var after = Noctis.ViewModels.LyricsViewModel.ProbeLocalLyricSources(track, true, "en");
        Assert.Equal("Sidecar:Ttml", after.Source);
        Assert.Equal("Unison", after.OnlineTimingSource);
        Assert.Equal("Word timing from Unison", Noctis.Localization.Loc.T("Lyrics.OnlineTimingCredit", "Unison"));
    }

    [Fact]
    public void WritesOnlyTheTtmlBesideWordLyrics()
    {
        var elrc = Path.ChangeExtension(_song, ".elrc");
        var lrc = Path.ChangeExtension(_song, ".lrc");
        File.WriteAllText(elrc, "[00:10.00]<00:10.00>hello <00:10.50>you <00:11.00>");
        File.WriteAllText(lrc, "[00:10.00]hello you");
        Assert.True(_writer.WriteOnlineTiming(_song, Marked()));
        Assert.Equal(WordTimingSource.LrcRed, OnlineTimingSidecar.SourceOf(File.ReadAllText(Path.ChangeExtension(_song, ".ttml"))));
        Assert.Equal("[00:10.00]<00:10.00>hello <00:10.50>you <00:11.00>", File.ReadAllText(elrc));
        Assert.Equal("[00:10.00]hello you", File.ReadAllText(lrc));
    }

    [Fact]
    public void AnEarlierOnlineTimingIsReplacedTheUsersOwnIsKept()
    {
        var ttml = Path.ChangeExtension(_song, ".ttml");
        Assert.True(_writer.WriteOnlineTiming(_song, Marked(WordTimingSource.Unison, 0)));
        Assert.True(_writer.WriteOnlineTiming(_song, Marked(WordTimingSource.LrcRed, 0.1)));
        Assert.Equal(WordTimingSource.LrcRed, OnlineTimingSidecar.SourceOf(File.ReadAllText(ttml)));

        File.WriteAllText(ttml, Ttml);   // the user's own (Lyrics Studio, downloaded)
        Assert.False(_writer.WriteOnlineTiming(_song, Marked()));
        Assert.Equal(Ttml, File.ReadAllText(ttml));
    }

    [Fact]
    public void NothingIsWrittenOverALyricsfileOrUnmarkedOrUnwritable()
    {
        Assert.False(_writer.WriteOnlineTiming(_song, Ttml));   // not an online timing
        var lyricsfile = Path.ChangeExtension(_song, ".lyricsfile");
        File.WriteAllText(lyricsfile, "x");
        Assert.False(_writer.WriteOnlineTiming(_song, Marked()));
        File.Delete(lyricsfile);
        var ttml = Path.ChangeExtension(_song, ".ttml");
        File.WriteAllText(ttml, Marked(WordTimingSource.Unison, 0));
        File.SetAttributes(ttml, FileAttributes.ReadOnly);
        Assert.False(_writer.WriteOnlineTiming(_song, Marked()));
        Assert.Equal(WordTimingSource.Unison, OnlineTimingSidecar.SourceOf(File.ReadAllText(ttml)));
        Assert.False(File.Exists(Path.ChangeExtension(_song, ".elrc")));
    }
}
