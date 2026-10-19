using System.Xml;
using System.Xml.Linq;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (nutf!xx): the .ttml files Noctis saves showed every line's words run together in
/// another player — "YeahIdon'tsleep", "Creepin'aroundlikenooneknows". The space between two
/// words was a whitespace-only text node between their spans, which XML readers drop by default
/// (.NET's XDocument / XmlDocument, "skip whitespace" pull parsers). Noctis's own reader keeps
/// whitespace, so its lyrics page never showed it.
/// </summary>
public class TtmlWordSpacesTests
{
    private static readonly XNamespace Tt = "http://www.w3.org/ns/ttml";

    private const string Synced =
        "[00:01.00]<00:01.00>Yeah <00:01.30>I <00:01.50>don't <00:01.80>sleep <00:02.40>please <00:02.70>don't <00:03.00>wake <00:03.40>me<00:04.00>\n" +
        "[00:05.00]<00:05.00>Creepin' <00:05.50>around <00:06.00>like <00:06.30>no <00:06.50>one <00:06.80>knows<00:07.50>";

    /// <summary>
    /// Each &lt;p&gt;'s text as a player reads it that takes the text of the spans and of what is
    /// between them, with an XML reader left at its defaults (whitespace-only text dropped).
    /// Romanization / translation layer spans are left out, as a player showing the line would.
    /// </summary>
    private static string[] DefaultReaderLines(string ttml) =>
        XDocument.Parse(ttml).Descendants(Tt + "p").Select(PlainText).ToArray();

    private static string[] XmlDocumentLines(string ttml)
    {
        var doc = new XmlDocument(); // PreserveWhitespace = false, the default
        doc.LoadXml(ttml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("tt", Tt.NamespaceName);
        return doc.SelectNodes("//tt:p", ns)!.Cast<XmlNode>().Select(p => p.InnerText.Trim()).ToArray();
    }

    private static string PlainText(XElement p) =>
        string.Concat(p.DescendantNodes().OfType<XText>()
            .Where(t => !t.Ancestors().Any(a => a.Attributes().Any(x => x.Name.LocalName == "role" && x.Value is "x-roman" or "x-translation")))
            .Select(t => t.Value)).Trim();

    [Fact]
    public void SavedTtml_ReadByAnotherPlayer_KeepsTheSpacesBetweenWords()
    {
        var ttml = LyricsViewModel.BuildTtmlFromSynced(Synced)!;

        var expected = new[] { "Yeah I don't sleep please don't wake me", "Creepin' around like no one knows" };
        Assert.Equal(expected, DefaultReaderLines(ttml));
        Assert.Equal(expected, XmlDocumentLines(ttml));
    }

    [Fact]
    public void SavedTtml_WritesTheSpaceInsideTheWordSpan_NotBetweenSpans()
    {
        var ttml = LyricsViewModel.BuildTtmlFromSynced(Synced)!;

        Assert.Contains(">Yeah </span><span begin=", ttml);
        Assert.Contains(">wake </span><span begin=", ttml);
        Assert.Contains(">me</span></p>", ttml); // the line's last word has no trailing space
        Assert.DoesNotContain("</span> <span", ttml);
    }

    [Fact]
    public void SavedTtml_ReadBackByNoctis_IsTheSameLines_WithoutDoubleSpaces()
    {
        var source = ExistingLyricsLoader.ParseTimed(Synced);
        var fromTtml = TtmlParser.Parse(LyricsViewModel.BuildTtmlFromSynced(Synced)!).Lines!;

        Assert.Equal(source.Select(l => l.Text), fromTtml.Select(l => l.Text));
        Assert.Equal(new[] { "Yeah ", "I ", "don't ", "sleep ", "please ", "don't ", "wake ", "me" }, fromTtml[0].Words!.Select(w => w.Text));
        for (var i = 0; i < source.Count; i++)
        {
            Assert.Equal(source[i].Words.Select(w => w.Start), fromTtml[i].Words!.Select(w => w.Start));
            Assert.Equal(source[i].Words.Select(w => (TimeSpan?)w.End), fromTtml[i].Words!.Select(w => w.End));
        }
        Assert.DoesNotContain(fromTtml, l => l.Text.Contains("  "));

        // And the file Noctis writes from those lines again is the same file.
        var again = ExistingLyricsLoader.ParseTimed(TtmlParser.ToLrc(LyricsViewModel.BuildTtmlFromSynced(Synced))!);
        Assert.Equal(LyricsViewModel.BuildTtmlFromSynced(Synced), TimedLyricsBuilder.BuildTtml(again));
    }

    /// <summary>Background vocals: a space before the x-bg row and between its words, read either way.</summary>
    [Fact]
    public void SavedTtml_BackgroundVocals_KeepTheirSpaces()
    {
        var song = new List<AlignedLine>
        {
            new("Come take it (oh yeah)", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(22), new List<AlignedWord>
            {
                new("Come", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20.3)),
                new("take", TimeSpan.FromSeconds(20.3), TimeSpan.FromSeconds(20.6)),
                new("it", TimeSpan.FromSeconds(20.6), TimeSpan.FromSeconds(20.9)),
                new("(oh", TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(21.4)),
                new("yeah)", TimeSpan.FromSeconds(21.4), TimeSpan.FromSeconds(22)),
            }, 1, false),
        };
        var ttml = TimedLyricsBuilder.BuildTtml(song);

        Assert.Equal(new[] { "Come take it (oh yeah)" }, DefaultReaderLines(ttml));
        var line = Assert.Single(TtmlParser.Parse(ttml).Lines!);
        Assert.Equal("Come take it", line.Text);
        Assert.Equal(new[] { "Come ", "take ", "it" }, line.Words!.Select(w => w.Text));
        Assert.Equal(new[] { "(oh ", "yeah)" }, line.BackgroundWords!.Select(w => w.Text));
    }

    /// <summary>A line timed by line only, with a background vocal: a space before the x-bg row too.</summary>
    [Fact]
    public void SavedTtml_LineLevelLineWithBackground_KeepsTheSpaceBeforeIt()
    {
        const string synced = "[00:10.00]Hold on\n[bg: <00:10.50>(hold <00:11.00>on)<00:11.50>]";
        var ttml = LyricsViewModel.BuildTtmlFromSynced(synced)!;

        Assert.Equal(new[] { "Hold on (hold on)" }, DefaultReaderLines(ttml));
        var line = Assert.Single(TtmlParser.Parse(ttml).Lines!);
        Assert.Equal("Hold on", line.Text);
        Assert.Equal(new[] { "(hold ", "on)" }, line.BackgroundWords!.Select(w => w.Text));
    }

    /// <summary>A word sung in syllables stays one word in the file: no space inside it.</summary>
    [Fact]
    public void SavedTtml_SyllableWord_StaysJoined()
    {
        const string synced = "[00:10.00]<00:10.00>Sleep<00:10.30>in' <00:10.60>you're <00:11.00>on<00:11.50>";
        var ttml = LyricsViewModel.BuildTtmlFromSynced(synced)!;

        Assert.Equal(new[] { "Sleepin' you're on" }, DefaultReaderLines(ttml));
        Assert.Equal("Sleepin' you're on", Assert.Single(TtmlParser.Parse(ttml).Lines!).Text);
    }

    // ── CJK: characters timed one by one, written with no space between ───────────

    /// <summary>Save ▸ TTML of a CJK word-synced .elrc: the saved file put a space between every character.</summary>
    [Fact]
    public void SavedTtml_OfCjkCharacters_WritesNoSpaceBetweenThem()
    {
        const string synced = "[00:20.51]<00:20.51>偶<00:20.94>尔<00:23.53>\n[00:24.44]<00:24.44>我<00:24.63>会 <00:24.79>怀<00:24.99>疑<00:25.14>";
        var ttml = LyricsViewModel.BuildTtmlFromSynced(synced)!;

        Assert.Contains(">偶</span><span", ttml);
        Assert.Equal(new[] { "偶尔", "我会 怀疑" }, DefaultReaderLines(ttml));
        Assert.Equal(new[] { "偶尔", "我会 怀疑" }, TtmlParser.Parse(ttml).Lines!.Select(l => l.Text));
        Assert.Equal(new[] { "偶尔", "我会 怀疑" }, LyricsViewModel.ParseLrcContent(TimedLyricsBuilder.BuildElrc(ExistingLyricsLoader.ParseTimed(synced))).Select(l => l.Text));
    }

    /// <summary>
    /// A CJK TTML imported in Edit Info / Lyrics Studio (TtmlParser.ToLrc): the span layout of
    /// amll-ttml-db am-lyrics/1010728767.ttml line L1 and L4 (a real space between two words).
    /// </summary>
    [Fact]
    public void ImportedCjkTtml_KeepsItsCharactersTogether()
    {
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
            <p begin="20.519" end="23.530"><span begin="20.519" end="20.942">偶</span><span begin="20.942" end="23.530">尔</span></p>
            <p begin="30.258" end="32.497"><span begin="30.258" end="30.621">再</span><span begin="30.621" end="31.042">怎</span><span begin="31.042" end="31.412">么</span> <span begin="31.412" end="31.839">深</span><span begin="31.839" end="32.172">呼</span><span begin="32.172" end="32.497">吸</span></p>
            </div></body></tt>
            """;
        var lrc = TtmlParser.ToLrc(ttml)!;

        Assert.Equal(new[] { "偶尔", "再怎么 深呼吸" }, LyricsViewModel.ParseLrcContent(lrc).Select(l => l.Text));
        Assert.Equal(new[] { "偶尔", "再怎么 深呼吸" }, ExistingLyricsLoader.ParseTimed(lrc).Select(l => l.Text));
        Assert.Equal(new[] { "偶尔", "再怎么 深呼吸" }, TtmlParser.Parse(TimedLyricsBuilder.BuildTtml(ExistingLyricsLoader.ParseTimed(lrc))).Lines!.Select(l => l.Text));
    }

    /// <summary>
    /// A TTML whose spans carry the space BEFORE the word ("&lt;span&gt; world&lt;/span&gt;"), as
    /// the lyrics page shows it ("Hello world"), imported or fetched online (TtmlParser.ToLrc,
    /// OnlineTtml.Convert): the LRC it became ran the words together ("Helloworld").
    /// </summary>
    [Fact]
    public void ImportedTtml_WithTheSpaceBeforeEachWord_KeepsItsSpaces()
    {
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
            <p begin="1.000" end="3.000"><span begin="1.000" end="1.500">Hello</span><span begin="1.500" end="2.000"> world</span><span begin="2.000" end="3.000"> again</span></p>
            </div></body></tt>
            """;
        Assert.Equal("Hello world again", Assert.Single(TtmlParser.Parse(ttml).Lines!).Text);

        var lrc = TtmlParser.ToLrc(ttml)!;
        var online = OnlineTtml.Convert(ttml)!.Value.Synced!;

        Assert.Equal(new[] { "Hello world again" }, LyricsViewModel.ParseLrcContent(lrc).Select(l => l.Text));
        Assert.Equal(new[] { "Hello world again" }, LyricsViewModel.ParseLrcContent(online).Select(l => l.Text));
        Assert.Equal(new[] { "Hello world again" }, ExistingLyricsLoader.ParseTimed(lrc).Select(l => l.Text));
    }

    /// <summary>Duet agents are unchanged and each singer's line keeps its spaces.</summary>
    [Fact]
    public void SavedTtml_Duet_KeepsAgentsAndSpaces()
    {
        const string synced =
            "[00:01.00]v1:<00:01.00>first <00:01.50>voice<00:02.00>\n" +
            "[00:02.00]v2:<00:02.00>second <00:02.50>voice<00:03.00>";
        var ttml = LyricsViewModel.BuildTtmlFromSynced(synced)!;

        Assert.Contains("ttm:agent=\"v2\"", ttml);
        Assert.Equal(new[] { "first voice", "second voice" }, DefaultReaderLines(ttml));
    }

    // ── Every path that writes a .ttml, and the files released versions wrote ──

    /// <summary>
    /// The line exactly as v1.6.0–v1.6.3 wrote it (TimedLyricsBuilder.BuildTtml at ad41b97c): the
    /// space between two words a whitespace-only text node between their spans.
    /// </summary>
    private const string ReleasedTtml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\">\n" +
        "  <head><metadata/></head>\n  <body>\n    <div begin=\"00:00:01.000\" end=\"00:00:04.000\">\n" +
        "      <p begin=\"00:00:01.000\" end=\"00:00:04.000\" itunes:key=\"L1\"><span begin=\"00:00:01.000\" end=\"00:00:01.300\">Yeah</span> <span begin=\"00:00:01.300\" end=\"00:00:01.500\">I</span> <span begin=\"00:00:01.500\" end=\"00:00:01.800\">don't</span> <span begin=\"00:00:01.800\" end=\"00:00:02.400\">sleep</span> <span begin=\"00:00:02.400\" end=\"00:00:02.700\">please</span> <span begin=\"00:00:02.700\" end=\"00:00:03.000\">don't</span> <span begin=\"00:00:03.000\" end=\"00:00:03.400\">wake</span> <span begin=\"00:00:03.400\" end=\"00:00:04.000\">me</span></p>\n" +
        "    </div>\n  </body>\n</tt>";

    /// <summary>The same line as Noctis writes it now: each word's space inside its span.</summary>
    private const string FixedLine =
        "<p begin=\"00:00:01.000\" end=\"00:00:04.000\" itunes:key=\"L1\"><span begin=\"00:00:01.000\" end=\"00:00:01.300\">Yeah </span><span begin=\"00:00:01.300\" end=\"00:00:01.500\">I </span><span begin=\"00:00:01.500\" end=\"00:00:01.800\">don't </span><span begin=\"00:00:01.800\" end=\"00:00:02.400\">sleep </span><span begin=\"00:00:02.400\" end=\"00:00:02.700\">please </span><span begin=\"00:00:02.700\" end=\"00:00:03.000\">don't </span><span begin=\"00:00:03.000\" end=\"00:00:03.400\">wake </span><span begin=\"00:00:03.400\" end=\"00:00:04.000\">me</span></p>";

    private const string OneLine =
        "[00:01.00]<00:01.00>Yeah <00:01.30>I <00:01.50>don't <00:01.80>sleep <00:02.40>please <00:02.70>don't <00:03.00>wake <00:03.40>me<00:04.00>";

    /// <summary>The report: a reader that drops whitespace-only text runs the released file's words together.</summary>
    [Fact]
    public void ReleasedTtml_ReadByAWhitespaceDroppingParser_RunsTheWordsTogether()
    {
        Assert.Equal(new[] { "YeahIdon'tsleeppleasedon'twakeme" }, DefaultReaderLines(ReleasedTtml));
        Assert.Equal(new[] { "YeahIdon'tsleeppleasedon'twakeme" }, XmlDocumentLines(ReleasedTtml));
    }

    /// <summary>Noctis keeps whitespace when it reads, so it always showed those files with their spaces.</summary>
    [Fact]
    public void ReleasedTtml_ReadByNoctis_HasItsSpaces()
    {
        var line = Assert.Single(TtmlParser.Parse(ReleasedTtml).Lines!);
        Assert.Equal("Yeah I don't sleep please don't wake me", line.Text);
        Assert.Equal(8, line.Words!.Count);
    }

    /// <summary>Lyrics page ▸ Save Lyrics to File ▸ TTML (also LRC/ELRC → TTML): the exact line written.</summary>
    [Fact]
    public void LyricsPageSave_WritesTheSpaceInsideEachSpan_Exactly()
    {
        var ttml = LyricsViewModel.BuildTtmlFromSynced(OneLine)!;

        Assert.Contains("      " + FixedLine + "\n", ttml);
        Assert.Equal(ReleasedTtml.Replace(ReleasedTtml.Split('\n')[5], "      " + FixedLine), ttml);
    }

    /// <summary>
    /// Lyrics Studio ▸ Also save as TTML: Whisper's words carry a leading space (" Yeah"); the
    /// file has each word's space after it, inside its span, and none before the first.
    /// </summary>
    [Fact]
    public void LyricsStudioSave_WhisperWords_WriteTheSpaceInsideEachSpan_Exactly()
    {
        var starts = new[] { 1.0, 1.3, 1.5, 1.8, 2.4, 2.7, 3.0, 3.4, 4.0 };
        var texts = new[] { " Yeah", " I", " don't", " sleep", " please", " don't", " wake", " me" };
        var words = texts.Select((t, i) => new AlignedWord(t, TimeSpan.FromSeconds(starts[i]), TimeSpan.FromSeconds(starts[i + 1]))).ToList();
        var line = ExistingLyricsLoader.ParseTimed(OneLine)[0] with { Words = words };

        var ttml = TimedLyricsBuilder.BuildTtml(new[] { line });

        Assert.Contains("      " + FixedLine + "\n", ttml);
    }

    /// <summary>
    /// A released file read back and saved again (Save ▸ TTML of synced lyrics that came from it,
    /// or online TTML in Apple's between-spans layout from lrc.red / Unison): the new file has
    /// the space inside the spans, so other players read it.
    /// </summary>
    [Fact]
    public void ReleasedOrAppleLayoutTtml_SavedAgain_MovesTheSpacesIntoTheSpans()
    {
        var viaParser = LyricsViewModel.BuildTtmlFromSynced(TtmlParser.ToLrc(ReleasedTtml)!)!;
        var viaOnline = LyricsViewModel.BuildTtmlFromSynced(OnlineTtml.Convert(ReleasedTtml)!.Value.Synced!)!;

        foreach (var ttml in new[] { viaParser, viaOnline })
        {
            Assert.Contains("      " + FixedLine + "\n", ttml);
            Assert.DoesNotContain("</span> <span", ttml);
            Assert.Equal(new[] { "Yeah I don't sleep please don't wake me" }, DefaultReaderLines(ttml));
        }
    }
}
