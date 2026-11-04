using Noctis.Helpers;
using Xunit;
namespace Noctis.Tests;

// Plus already isolated every profile before the upstream fix; keep its product-specific names.
public class SingleInstanceGuardTests
{
    [Fact]
    public void InstalledDevAndTestProfilesKeepSeparateInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisPlus-InstanceTest");
        var installed = SingleInstanceGuard.ProfileIdentity(Path.Combine(root, "Noctis Plus"));
        var dev = SingleInstanceGuard.ProfileIdentity(Path.Combine(root, "Noctis Plus-dev"));
        var test = SingleInstanceGuard.ProfileIdentity(Path.Combine(root, "NoctisPlus-Test"));
        Assert.Matches("^[0-9A-F]{16}$", installed);
        Assert.Equal(3, new[] { installed, dev, test }.Distinct().Count());
    }
    [Fact]
    public void ProfileSpellingUsesTheSameInstance()
    {
        var path = Path.Combine(Path.GetTempPath(), "NoctisPlus-InstanceTest");
        var id = SingleInstanceGuard.ProfileIdentity(path);
        Assert.Equal(id, SingleInstanceGuard.ProfileIdentity(path + Path.DirectorySeparatorChar));
        Assert.Equal(id, SingleInstanceGuard.ProfileIdentity(Path.Combine(path, ".")));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(id, SingleInstanceGuard.ProfileIdentity(path.ToLowerInvariant()));
            Assert.Equal(id, SingleInstanceGuard.ProfileIdentity(path.Replace('\\', '/')));
        }
    }
}