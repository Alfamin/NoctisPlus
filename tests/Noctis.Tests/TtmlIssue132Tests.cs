using System.Text;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #132: a local .ttml beside the song (Apple Music style) loads as synced lyrics —
/// line timing, word timing, order, repeats, file encodings and duet agents.
/// </summary>
public class TtmlIssue132Tests
{
    // The reporter's file, pretty-printed as it is on disk (text on its own indented line).
    internal const string IssueExample = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Line" xml:lang="en">
          <head>
            <metadata>
              <ttm:agent type="person" xml:id="v1"/>
            </metadata>
          </head>
          <body dur="3:19.167">
            <div begin="7.553" end="15.082" itunes:songPart="Verse">
              <p begin="7.553" end="10.796" itunes:key="L1" ttm:agent="v1">
                Feel the breeze in my fingers out the window
              </p>
              <p begin="11.100" end="15.082" itunes:key="L2" ttm:agent="v1">
                Close your eyes and I'll take you where the wind blows
              </p>
            </div>
          </body>
        </tt>
        """;

    internal const string WordTimed = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Word" xml:lang="en">
          <head><metadata><ttm:agent type="person" xml:id="v1"/></metadata></head>
          <body dur="3:19.167">
            <div begin="7.553" end="15.082" itunes:songPart="Verse">
              <p begin="7.553" end="10.796" itunes:key="L1" ttm:agent="v1">
                <span begin="7.553" end="7.900">Feel</span> <span begin="7.900" end="8.200">the</span> <span begin="8.200" end="10.796">breeze</span>
              </p>
              <p begin="11.100" end="15.082" itunes:key="L2" ttm:agent="v1"><span begin="11.100" end="11.600">Close</span> <span begin="11.600" end="15.082">eyes</span></p>
            </div>
          </body>
        </tt>
        """;

    private static void AssertIssueLines(IReadOnlyList<LyricLine>? lines)
    {
        Assert.NotNull(lines);
        var real = lines!.Where(l => !l.IsIntroPlaceholder && l.Text != "...").ToList();
        Assert.Equal(2, real.Count);
        Assert.Equal("Feel the breeze in my fingers out the window", real[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(7553), real[0].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(10796), real[0].EndTimestamp);
        Assert.Equal("Close your eyes and I'll take you where the wind blows", real[1].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(11100), real[1].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(15082), real[1].EndTimestamp);
        Assert.All(real, l => Assert.Equal(LyricVoice.Default, l.Voice));
    }

    /// <summary>Writes "song.mp3" plus a sidecar in a fresh folder and runs the desktop lyrics probe.</summary>
    private static LyricsViewModel.LocalLyricsProbe ProbeSidecar(byte[] sidecar, string ext = ".ttml")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-132-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var audio = Path.Combine(dir, "song.mp3");
            File.WriteAllBytes(audio, new byte[] { 0 });
            File.WriteAllBytes(Path.Combine(dir, "song" + ext), sidecar);
            var track = new Track { Id = Guid.NewGuid(), FilePath = audio, Title = "song" };
            return LyricsViewModel.ProbeLocalLyricSources(track, joinSplitWords: true, uiLanguage: "en");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IssueExample_Parses_LineTimes()
    {
        var (lines, plain) = TtmlParser.Parse(IssueExample);
        AssertIssueLines(lines);
        Assert.All(lines!, l => Assert.Null(l.Words));
        Assert.Equal("Feel the breeze in my fingers out the window\nClose your eyes and I'll take you where the wind blows", plain);
    }

    [Fact]
    public void IssueExample_LoadsAsSidecar_OnTheLyricsPage()
    {
        var probe = ProbeSidecar(Encoding.UTF8.GetBytes(IssueExample));
        Assert.Equal("Sidecar:Ttml", probe.Source);
        AssertIssueLines(probe.Lines);
    }

    [Theory]
    [InlineData(".ttml")]
    [InlineData(".TTML")]
    public void IssueExample_CrlfAndBom_LoadsAsSidecar(string ext)
    {
        var crlf = IssueExample.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(crlf)).ToArray();
        var probe = ProbeSidecar(bytes, ext);
        Assert.Equal("Sidecar:Ttml", probe.Source);
        AssertIssueLines(probe.Lines);
    }

    [Fact]
    public void IssueExample_WithXmlDeclarationAndDoctype_Parses()
    {
        var withProlog = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\n"
            + "<!DOCTYPE tt [<!ENTITY boom \"x\">]>\n" + IssueExample;
        AssertIssueLines(TtmlParser.Parse(withProlog).Lines);
        AssertIssueLines(ProbeSidecar(Encoding.UTF8.GetBytes(withProlog)).Lines);
    }

    [Fact]
    public void XmlDeclaration_AfterLeadingBlankLine_StillParses()
    {
        // Files saved by hand or by scripts often start with a newline (or a stray BOM
        // character survives a re-encode); the XML reader rejects a declaration that is not
        // the very first thing, which silently turned the whole file into "no lyrics".
        var leading = "\r\n  \uFEFF<?xml version=\"1.0\" encoding=\"utf-16\"?>\n" + IssueExample;
        AssertIssueLines(TtmlParser.Parse(leading).Lines);
    }

    [Fact]
    public void WordTimedVariant_ProducesWordTimings()
    {
        var probe = ProbeSidecar(Encoding.UTF8.GetBytes(WordTimed));
        Assert.Equal("Sidecar:Ttml", probe.Source);
        var lines = probe.Lines!.Where(l => l.Text != "...").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("Feel the breeze", lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(7553), lines[0].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(10796), lines[0].EndTimestamp);
        var words = lines[0].Words!;
        Assert.Equal(new[] { "Feel", "the", "breeze" }, words.Select(w => w.Text.Trim()));
        Assert.Equal(TimeSpan.FromMilliseconds(7553), words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(7900), words[0].End);
        Assert.Equal(TimeSpan.FromMilliseconds(8200), words[2].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(10796), words[2].End);
        Assert.Equal(new[] { "Close", "eyes" }, lines[1].Words!.Select(w => w.Text.Trim()));
    }

    [Fact]
    public void TextWrappedAcrossSourceLines_CollapsesToOneLine()
    {
        // Pretty-printers wrap long text inside <p>; XML whitespace there is one space,
        // not a line break plus the indentation.
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
              <p begin="1" end="2">
                Feel the breeze
                in my fingers
              </p>
            </div></body></tt>
            """;
        var (lines, _) = TtmlParser.Parse(ttml);
        Assert.Equal("Feel the breeze in my fingers", Assert.Single(lines!).Text);
    }

    [Fact]
    public void RepeatedLines_AndOrder_ArePreserved()
    {
        // A chorus sung three times stays three lines; lines sharing a begin (overlapping
        // vocals) keep their file order even past List.Sort's 16-element insertion-sort cutoff.
        var sb = new StringBuilder("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div>");
        for (var i = 0; i < 40; i++)
            sb.Append($"<p begin=\"{10 + i / 4}\" end=\"{11 + i / 4}\">line {i:00}</p>");
        sb.Append("<p begin=\"60\" end=\"61\">Chorus</p><p begin=\"70\" end=\"71\">Chorus</p><p begin=\"80\" end=\"81\">Chorus</p>");
        sb.Append("</div></body></tt>");

        var (lines, _) = TtmlParser.Parse(sb.ToString());

        Assert.Equal(43, lines!.Count);
        for (var i = 0; i < 40; i++)
            Assert.Equal($"line {i:00}", lines[i].Text);
        Assert.Equal(new[] { 60, 70, 80 }, lines.Skip(40).Select(l => (int)l.Timestamp!.Value.TotalSeconds));
        Assert.All(lines.Skip(40), l => Assert.Equal("Chorus", l.Text));
    }

    [Fact]
    public void UntimedParagraphs_KeepFileOrder()
    {
        var sb = new StringBuilder("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div>");
        for (var i = 0; i < 30; i++) sb.Append($"<p>row {i:00}</p>");
        sb.Append("</div></body></tt>");

        var (lines, _) = TtmlParser.Parse(sb.ToString());

        Assert.Equal(Enumerable.Range(0, 30).Select(i => $"row {i:00}"), lines!.Select(l => l.Text));
    }

    // ── Duet agents (ttm:agent) → the lyrics page's existing duet layout (LyricVoice) ──

    private const string Duet = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
          <head><metadata>
            <ttm:agent type="person" xml:id="v1"/>
            <ttm:agent type="person" xml:id="v2"/>
            <ttm:agent type="group" xml:id="v1000"/>
          </metadata></head>
          <body><div>
            <p begin="1" end="2" ttm:agent="v2">she sings first</p>
            <p begin="2" end="3" ttm:agent="v1">he answers</p>
            <p begin="3" end="4" ttm:agent="v1000">together</p>
            <p begin="4" end="5">no agent</p>
            <p begin="5" end="6" ttm:agent="v1 v2">both listed</p>
          </div></body>
        </tt>
        """;

    [Fact]
    public void Agents_MapOntoDuetVoices()
    {
        var (lines, _) = TtmlParser.Parse(Duet);

        // v1 is the first declared person: the normal side. v2, the second person, takes
        // the opposite side even though she sings first. A group agent, or several agents
        // on one line, is both singers.
        Assert.Equal(
            new[] { LyricVoice.Voice2, LyricVoice.Default, LyricVoice.Group, LyricVoice.Default, LyricVoice.Group },
            lines!.Select(l => l.Voice));
    }

    [Fact]
    public void Agent_InheritedFromDiv()
    {
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body>
              <div ttm:agent="a"><p begin="1" end="2">one</p></div>
              <div ttm:agent="b"><p begin="2" end="3">two</p></div>
            </body></tt>
            """;
        var (lines, _) = TtmlParser.Parse(ttml);
        // Undeclared agents take their first appearance order.
        Assert.Equal(new[] { LyricVoice.Default, LyricVoice.Voice2 }, lines!.Select(l => l.Voice));
    }

    [Fact]
    public void SingleAgent_OrOnlySecondDeclaredUsed_StaysOnTheNormalSide()
    {
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <head><metadata><ttm:agent type="person" xml:id="v1"/><ttm:agent type="person" xml:id="v2"/></metadata></head>
              <body><div><p begin="1" end="2" ttm:agent="v2">solo by the feature</p></div></body>
            </tt>
            """;
        Assert.Equal(LyricVoice.Default, Assert.Single(TtmlParser.Parse(ttml).Lines!).Voice);
    }

    // ── Conversion for the importers ──

    [Fact]
    public void ToLrc_LineTimed_GivesLrc()
    {
        Assert.Equal(
            "[00:07.55]Feel the breeze in my fingers out the window\n[00:11.10]Close your eyes and I'll take you where the wind blows",
            TtmlParser.ToLrc(IssueExample));
    }

    [Fact]
    public void ToLrc_WordTimed_GivesElrc_ThatReparsesToTheSameWords()
    {
        var elrc = TtmlParser.ToLrc(WordTimed)!;
        Assert.StartsWith("[00:07.55]<00:07.55>Feel <00:07.90>the <00:08.20>breeze<00:10.79>", elrc);

        var reparsed = LrcParser.Parse(elrc);
        Assert.Equal(new[] { "Feel the breeze", "Close eyes" }, reparsed.Select(l => l.Text));
        Assert.Equal(3, reparsed[0].Words!.Count);
    }

    [Fact]
    public void ToLrc_KeepsVoicesLayersAndBackground()
    {
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <head><metadata><ttm:agent type="person" xml:id="v1"/><ttm:agent type="person" xml:id="v2"/></metadata></head>
              <body><div>
                <p begin="1" end="2" ttm:agent="v1">uno<span ttm:role="x-translation">one</span></p>
                <p begin="2" end="4" ttm:agent="v2"><span begin="2" end="3">dos</span> <span ttm:role="x-bg"><span begin="3" end="4">(two)</span></span></p>
              </div></body>
            </tt>
            """;
        var lrc = TtmlParser.ToLrc(ttml)!;

        var reparsed = LrcParser.Parse(lrc);
        Assert.Equal(2, reparsed.Count);
        Assert.Equal("uno", reparsed[0].Text);
        Assert.Equal("one", reparsed[0].Translation);
        Assert.Equal(LyricVoice.Default, reparsed[0].Voice);
        Assert.Equal("dos", reparsed[1].Text);
        Assert.Equal(LyricVoice.Voice2, reparsed[1].Voice);
        Assert.Equal("(two)", Assert.Single(reparsed[1].BackgroundWords!).Text.Trim());
    }

    [Fact]
    public void ToLrc_Untimed_GivesPlainText_AndMalformed_GivesNull()
    {
        Assert.Equal("a\nb", TtmlParser.ToLrc("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><p>a</p><p>b</p></body></tt>"));
        Assert.Null(TtmlParser.ToLrc("<tt><p>unclosed"));
    }
}
