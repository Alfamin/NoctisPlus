using System.Globalization;
using Noctis.Converters;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Bug (seen live 10-12): Home's Last Played and album rows read "1 plays": the word after
/// the count was always the plural. The singular shows at exactly one.
/// </summary>
public class PlayCountWordTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(16, false)]
    public void SingularWord_ShowsAtExactlyOne(int plays, bool singular)
    {
        Assert.Equal(singular, PlayCountConverters.IsOne.Convert(plays, typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(!singular, PlayCountConverters.IsNotOne.Convert(plays, typeof(bool), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Views_ShowTheSingularWordAtOne()
    {
        var root = Path.GetDirectoryName(typeof(PlayCountWordTests).Assembly.Location)!;
        while (!File.Exists(Path.Combine(root, "Noctis.sln")) && Directory.GetParent(root) is { } parent) root = parent.FullName;
        foreach (var view in new[] { "HomeView.axaml", "LibraryAlbumsView.axaml" })
        {
            var xaml = File.ReadAllText(Path.Combine(root, "src", "Noctis", "Views", view));
            Assert.Contains("{loc:T Common.Play}", xaml);
            Assert.Contains("PlayCountConverters.IsOne", xaml);
            Assert.Contains("PlayCountConverters.IsNotOne", xaml);
        }
    }
}
