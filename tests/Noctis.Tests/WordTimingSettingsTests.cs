using Noctis.Features.WordLyrics;
using Xunit;

namespace Noctis.Tests;

public class WordTimingSettingsTests
{
    [Theory]
    [InlineData("false", "Full", "Off")]
    [InlineData("true", "Light", "Light")]
    [InlineData("true", "Half", "Medium")]
    [InlineData("true", "Full", "Full")]
    [InlineData(null, null, "Medium")]
    [InlineData("invalid", "unknown", "Medium")]
    public void LegacyChoicesCarryOver(string? automatic, string? speed, string expected)
    {
        var raw = new Dictionary<string, string>();
        if (automatic is not null) raw["auto"] = automatic;
        if (speed is not null) raw["speed"] = speed;
        Assert.Equal(expected, WordTimingSettings.Read(raw).ToString());
        Assert.True(WordTimingSettings.Migrate(raw));
        Assert.Equal(expected, raw[WordTimingSettings.ModeKey]);
        Assert.False(WordTimingSettings.Migrate(raw)); // no repeated settings writes at startup
    }

    [Fact]
    public void NativeChoiceWinsAndOtherSettingsStayIntact()
    {
        var raw = new Dictionary<string, string>
        { ["defaultMode"] = "Light", ["auto"] = "false", ["speed"] = "Full", ["folder"] = "custom path", ["backup"] = "backup path" };
        WordTimingSettings.Migrate(raw);
        Assert.Equal("true", raw["auto"]); Assert.Equal("Light", raw["speed"]);
        Assert.Equal("custom path", raw["folder"]); Assert.Equal("backup path", raw["backup"]);
    }

    [Fact]
    public void OffDoesNotStartABackendOrForgetPreviousEffort()
    {
        var raw = new Dictionary<string, string> { ["auto"] = "true", ["speed"] = "Full" };
        WordTimingSettings.Write(raw, WordTimingMode.Off);
        Assert.Null(WordTimingSettings.BackendSpeed(WordTimingSettings.Read(raw)));
        Assert.Equal("Full", raw["speed"]);
        WordTimingSettings.Write(raw, WordTimingMode.Medium);
        Assert.Equal("Half", raw["speed"]); Assert.Equal("true", raw["auto"]);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("2")]
    [InlineData("unknown")]
    public void MalformedNativeValueFallsBackToLegacyChoice(string invalid)
    {
        var raw = new Dictionary<string, string> { ["defaultMode"] = invalid, ["auto"] = "false", ["speed"] = "Full" };
        Assert.Equal(WordTimingMode.Off, WordTimingSettings.Read(raw));
    }
}
