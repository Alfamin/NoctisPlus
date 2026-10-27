using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #183: an empty library showed "No matching local recording …" under all 50 missing rows.</summary>
public class ImportMissingRowTests
{
    [Fact]
    public void OnlyReasonsWorthReading_AreShown()
    {
        Assert.False(new StreamingMissingRow(1, "Song", "Artist", "No matching local recording (artist, title and version checked)").ShowReason);
        Assert.False(new StreamingMissingRow(1, "Song", "Artist", "No matching local recording").ShowReason);
        Assert.True(new StreamingMissingRow(1, "Song", "Artist", "Different recording length: Spotify 3:01, local 2:40. A different version will not be substituted.").ShowReason);
        Assert.True(new StreamingMissingRow(1, "Unavailable entry", "", "Spotify lists this entry as unavailable.").ShowReason);
    }
}
