using System.Xml;
using System.Xml.Linq;
using Noctis.Services;
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
}
