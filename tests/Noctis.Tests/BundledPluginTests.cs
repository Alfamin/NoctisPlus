using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services.Plugins;
using Xunit;

namespace Noctis.Tests;

public class BundledPluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisPlus-bundles-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new() { CommunityPluginsEnabled = false };
    private PluginHost? _host;
    private string Bundles => Path.Combine(_root, "app", "bundled-plugins");

    private PluginHost Create() => _host = new PluginHost(null, Path.Combine(_root, "profile"), () => _settings,
        () => { }, "1.6.3", bundledDirectory: Bundles);

    private string Install(string root, string id)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Noctis.SamplePlugin.dll"), Path.Combine(dir, "Sample.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(new
        {
            id, name = "Bundled fixture", version = "1.0.0", type = "dotnet", entry = "Sample.dll",
            entryType = "Noctis.SamplePlugin.PulseRingPlugin", apiVersion = "1.1", minAppVersion = "1.5.3",
            permissions = new[] { "playback.control" }
        }));
        return dir;
    }

    [AvaloniaFact]
    public void BuiltIn_RunsWithCommunityOff_AndCanBeDisabledAcrossRestarts()
    {
        Install(Bundles, "test.builtin");
        var host = Create();
        host.LoadAll();
        var plugin = Assert.Single(host.Plugins);
        Assert.True(plugin.IsBundled);
        Assert.False(plugin.CanRemove);
        Assert.Equal(PluginStatus.Running, plugin.Status);
        host.SetEnabled(plugin, false);
        host.LoadAll();
        Assert.Equal(PluginStatus.Disabled, Assert.Single(host.Plugins).Status);
        host.SetEnabled(Assert.Single(host.Plugins), true);
        Assert.Equal(PluginStatus.Running, Assert.Single(host.Plugins).Status);
    }

    [AvaloniaFact]
    public void BuiltIns_DoNotOptIntoCommunityPlugins_OrApproveCommunityCode()
    {
        _settings.CommunityPluginsEnabled = null;
        Install(Bundles, "test.builtin");
        var host = Create();
        host.LoadAll();
        Assert.False(host.CommunityPluginsEnabled);
        Assert.Empty(_settings.PluginPermissionGrants);
        Install(host.PluginsDirectory, "test.community");
        host.LoadAll();
        Assert.Equal(PluginStatus.Running, host.FindById("test.builtin")!.Status);
        Assert.Equal(PluginStatus.Restricted, host.FindById("test.community")!.Status);
    }

    [AvaloniaFact]
    public void DuplicateUserCopy_CannotRunTwiceOrInheritBuiltInTrust()
    {
        Install(Bundles, "test.builtin");
        var host = Create();
        var userDir = Install(host.PluginsDirectory, "test.builtin");
        host.LoadAll();
        var plugin = Assert.Single(host.Plugins);
        Assert.StartsWith(Bundles, plugin.Directory);
        Assert.True(File.Exists(Path.Combine(userDir, "Sample.dll")));
        Assert.Single(host.VisualLayers);
    }

    [AvaloniaFact]
    public void RemovingBuiltIn_CannotDeleteApplicationOrPluginData()
    {
        var dir = Install(Bundles, "test.builtin");
        var host = Create();
        host.LoadAll();
        var plugin = Assert.Single(host.Plugins);
        var marker = Path.Combine(plugin.DataDirectory, "keep.txt");
        File.WriteAllText(marker, "kept");
        Assert.False(host.Remove(plugin, deleteData: true));
        Assert.True(File.Exists(marker));
        Assert.True(File.Exists(Path.Combine(dir, "Sample.dll")));
        Assert.Equal(PluginStatus.Running, plugin.Status);
    }

    [AvaloniaFact]
    public void CommunityToggle_DoesNotTurnOffBuiltIns()
    {
        Install(Bundles, "test.builtin");
        var host = Create();
        host.LoadAll();
        host.SetCommunityPluginsEnabled(true);
        host.SetCommunityPluginsEnabled(false);
        Assert.Equal(PluginStatus.Running, Assert.Single(host.Plugins).Status);
    }

    [Fact]
    public void DeliveredModules_MatchPinnedHashesAndHostApi()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "bundled-plugins");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var modules = doc.RootElement.GetProperty("modules");
        Assert.Equal(4, modules.GetArrayLength());
        foreach (var module in modules.EnumerateArray())
        {
            // Compiled into the app now (src/Noctis/Features): its DLL is no longer delivered.
            if (module.GetProperty("id").GetString() is "dev.moshi.freemusicfinder" or "dev.moshi.lyricmotion") continue;
            var dir = Path.Combine(root, module.GetProperty("id").GetString()!);
            var manifest = PluginManifest.TryLoad(dir)!;
            Assert.Equal(module.GetProperty("version").GetString(), manifest.Version);
            Assert.Null(manifest.CheckCompatibility("1.6.3"));
            foreach (var file in module.GetProperty("files").EnumerateObject())
                Assert.Equal(file.Value.GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, file.Name)))).ToLowerInvariant());
        }
    }

    [AvaloniaFact]
    public void DeliveredModules_InitializeAgainstTheForkSdk()
    {
        _host = new PluginHost(null, Path.Combine(_root, "profile"), () => _settings, () => { }, "1.6.3",
            bundledDirectory: Path.Combine(AppContext.BaseDirectory, "bundled-plugins"));
        try
        {
            // Two delivered modules plus Finder and Lyric Motion, built into the app.
            Noctis.Features.BuiltInFeatures.Register(_host);
            _host.LoadAll();
            Assert.Equal(4, _host.Plugins.Count);
            Assert.Contains(_host.Plugins, p => p.Id == "dev.moshi.freemusicfinder" && p.IsBundled);
            Assert.Contains(_host.Plugins, p => p.Id == "dev.moshi.lyricmotion" && p.IsBundled && p.AssemblyPath is null);
            Assert.All(_host.Plugins, p => Assert.True(p.Status == PluginStatus.Running, $"{p.Name}: {p.Status}: {p.Error}"));
        }
        finally { _host.UnloadAll(); }
    }

    [AvaloniaFact]
    public void PackageInstall_CannotOverwriteABuiltIn()
    {
        var dir = Install(Bundles, "test.builtin");
        var host = Create();
        host.LoadAll();
        var packageDir = Install(Path.Combine(_root, "package"), "test.builtin");
        var zip = Path.Combine(_root, "update.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(packageDir, zip);
        var result = host.InstallPackage(zip, allowUpdate: true);
        Assert.Equal(PluginInstallOutcome.AlreadyInstalled, result.Outcome);
        Assert.Contains("included with Noctis Plus", result.Message);
        Assert.True(File.Exists(Path.Combine(dir, "Sample.dll")));
        Assert.Empty(Directory.GetDirectories(host.PluginsDirectory));
        Assert.Equal(PluginStatus.Running, Assert.Single(host.Plugins).Status);
    }

    public void Dispose()
    {
        _host?.UnloadAll();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
