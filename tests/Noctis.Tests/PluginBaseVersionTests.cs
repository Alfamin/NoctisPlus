using Noctis.Helpers;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Noctis Plus has its own version number (2.0.0) while community plugins name Noctis releases in minAppVersion.
/// Plugins are checked against the Noctis base, so a 2.0.0 product number never claims to be "Noctis 2.0".
/// </summary>
public class PluginBaseVersionTests
{
    [Fact]
    public void Plugins_AreCheckedAgainstTheNoctisBase_NotTheProductVersion()
    {
        var forBase = PluginManifest.Parse(PluginSandbox.Manifest(minAppVersion: ProductIdentity.NoctisBaseVersion));
        Assert.Null(forBase.CheckCompatibility(ProductIdentity.NoctisBaseVersion));

        // A plugin made for a later upstream Noctis stays blocked, even though the product number is higher.
        var forLaterNoctis = PluginManifest.Parse(PluginSandbox.Manifest(minAppVersion: "1.7.0"));
        Assert.Contains("Needs Noctis 1.7.0", forLaterNoctis.CheckCompatibility(ProductIdentity.NoctisBaseVersion));
        Assert.Null(forLaterNoctis.CheckCompatibility("2.0.0")); // what passing the product version would wrongly allow
    }

    [Fact]
    public void About_CreditsTheNoctisBaseVersion()
    {
        Assert.Equal("Based on Noctis " + ProductIdentity.NoctisBaseVersion + " by heartached · MIT", ProductIdentity.BasedOn);
        Assert.True(Version.TryParse(ProductIdentity.NoctisBaseVersion, out var parsed) && parsed.Build >= 0, "a three-part version");
    }
}
