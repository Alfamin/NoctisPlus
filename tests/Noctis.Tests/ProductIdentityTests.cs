using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Noctis Plus must never share anything with an official Noctis on the same computer: not its data folder,
/// single-instance lock, update feed, installer entry, Windows registration or default network ports.
/// </summary>
public class ProductIdentityTests
{
    // What official Noctis 1.6.3 uses.
    private const string OfficialAppId = "{E8A3B5F1-7C2D-4A9E-B6F0-1D3E5A7C9B2F}";

    [Fact]
    public void DataFolders_AreOurOwn()
    {
        Assert.Equal("Noctis Plus", ProductIdentity.DataFolderName);
        Assert.NotEqual("Noctis", ProductIdentity.DataFolderName);
        Assert.NotEqual("Noctis-dev", ProductIdentity.DevDataFolderName);
        Assert.NotEqual(ProductIdentity.DataFolderName, ProductIdentity.DevDataFolderName);
    }

    [Fact]
    public void SingleInstanceLockAndActivationPipe_DoNotCollideWithOfficialNoctis()
    {
        foreach (var name in new[] { "MutexName", "PipeName" })
        {
            var value = (string)typeof(SingleInstanceGuard).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.StartsWith(ProductIdentity.Key + "-", value);
            Assert.False(value.StartsWith("Noctis-", StringComparison.Ordinal), value);
        }
    }

    [Fact]
    public void UpdateFeed_ReadsOnlyOurRepository()
    {
        foreach (var name in new[] { "ReleaseUrl", "ReleaseListUrl" })
        {
            var url = (string)typeof(UpdateService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.StartsWith("https://api.github.com/repos/" + ProductIdentity.Repository + "/releases", url);
            Assert.DoesNotContain("heartached", url, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(UpdateService.IsConfiguredRepoAssetUrl("https://api.github.com/repos/heartached/Noctis/releases/assets/587322385"));
        Assert.True(UpdateService.IsConfiguredRepoAssetUrl("https://api.github.com/repos/" + ProductIdentity.Repository + "/releases/assets/587322385"));
    }

    [Fact]
    public void Installer_HasItsOwnAppId_AndTheUpdaterLooksForThatOne()
    {
        Assert.NotEqual(OfficialAppId, ProductIdentity.InstallerAppId, StringComparer.OrdinalIgnoreCase);
        var uninstallKey = (string)typeof(UpdateService).GetField("InnoUninstallSubKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.EndsWith(ProductIdentity.InstallerAppId + "_is1", uninstallKey);

        // installer.iss writes the same AppId (Inno doubles the opening brace) and its own registration keys.
        var script = File.ReadAllText(FindRepositoryFile("installer.iss"));
        Assert.Contains("AppId={" + ProductIdentity.InstallerAppId, script);
        Assert.DoesNotContain(OfficialAppId.Trim('{', '}'), script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#define MyAppName \"" + ProductIdentity.Name + "\"", script);
        Assert.DoesNotContain("\"Noctis.AudioFile\"", script);
        Assert.DoesNotContain(@"Software\Noctis\", script);
    }

    [Fact]
    public void WindowsRegistration_UsesOurProgIdAndKey()
    {
        Assert.Equal(ProductIdentity.ProgId, WindowsFileAssociations.ProgId);
        Assert.NotEqual("Noctis.AudioFile", WindowsFileAssociations.ProgId);
        var capabilities = (string)typeof(WindowsFileAssociations).GetField("CapabilitiesPath", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.StartsWith(ProductIdentity.RegistryKey + @"\", capabilities);
        var startupName = (string)typeof(StartupHelper).GetField("AppName", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(ProductIdentity.Name, startupName);
    }

    [Fact]
    public void DefaultPorts_DifferFromOfficialNoctis_SoBothCanRun()
    {
        var settings = new AppSettings();
        Assert.NotEqual(9420, settings.WebRemotePort);
        Assert.NotEqual(9421, settings.LocalApiPort);
        Assert.NotEqual(settings.WebRemotePort, settings.LocalApiPort);
    }

    [Fact]
    public void UserAgents_NameUsAndOurContactPage()
    {
        Assert.StartsWith(ProductIdentity.Key + "/", Noctis.Services.MetadataSearch.ProviderHttp.UserAgent);
        Assert.Contains(ProductIdentity.ProjectUrl, Noctis.Services.MetadataSearch.ProviderHttp.UserAgent);
    }

    private static string FindRepositoryFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(name + " not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void WordLyricsSetup_ChildrenGetBackendOnlyMode_AndKeepTheirConfiguredPaths()
    {
        var before = Environment.GetEnvironmentVariable("WORDLYRICS_NO_NOCTIS");
        var toolFolder = Environment.GetEnvironmentVariable("WORDLYRICS_DIR");
        var profile = Environment.GetEnvironmentVariable("NOCTIS_PLUS_DATA_DIR");
        try
        {
            Noctis.Program.ConfigureBundledTools();
            // ProcessStartInfo's default environment is what the actual plugin's setup cmd
            // inherits. No installer/network process is started by this test.
            var child = new System.Diagnostics.ProcessStartInfo("cmd.exe");
            Assert.Equal("1", child.Environment["WORDLYRICS_NO_NOCTIS"]);
            Assert.Equal(toolFolder, Environment.GetEnvironmentVariable("WORDLYRICS_DIR"));
            Assert.Equal(profile, Environment.GetEnvironmentVariable("NOCTIS_PLUS_DATA_DIR"));
        }
        finally { Environment.SetEnvironmentVariable("WORDLYRICS_NO_NOCTIS", before); }
    }

    [Fact]
    public void EquivalentProfilePaths_ShareALock_WhileDevAndReleaseStaySeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisPlus-lock-test");
        Assert.Equal(SingleInstanceGuard.ProfileIdentity(root), SingleInstanceGuard.ProfileIdentity(root + Path.DirectorySeparatorChar));
        Assert.Equal(SingleInstanceGuard.ProfileIdentity(root), SingleInstanceGuard.ProfileIdentity(Path.Combine(root, "child", "..")));
        if (OperatingSystem.IsWindows())
            Assert.Equal(SingleInstanceGuard.ProfileIdentity(root), SingleInstanceGuard.ProfileIdentity(root.ToLowerInvariant()));
        Assert.NotEqual(SingleInstanceGuard.ProfileIdentity(root), SingleInstanceGuard.ProfileIdentity(root + "-dev"));
    }
}
