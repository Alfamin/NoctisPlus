using System.Diagnostics;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

// Discord (Spark, 09-02, Steam Deck AppImage): "Open Data Folder" did nothing.
// The AppImage's AppRun exports LD_LIBRARY_PATH / VLC_PLUGIN_PATH for our
// process and every child inherited them, so the host's xdg-open / file
// manager loaded the bundled Ubuntu libraries and died — silently, because
// the launcher swallowed every failure. Host tools now start with the
// AppImage runtime scrubbed from their environment.
public class PlatformHelperAppImageTests
{
    private const string AppDir = "/tmp/.mount_NoctisAbc123";

    [Fact]
    public void StripAppDirEntries_DropsOnlyBundledEntries_KeepsOrder()
    {
        var kept = PlatformHelper.StripAppDirEntries(
            $"{AppDir}/usr/lib:{AppDir}/usr/lib/vlc:/usr/lib:/home/deck/lib", AppDir);
        Assert.Equal("/usr/lib:/home/deck/lib", kept);
    }

    [Fact]
    public void StripAppDirEntries_WithoutKnownAppDir_DropsMountEntries()
    {
        var kept = PlatformHelper.StripAppDirEntries($"{AppDir}/usr/lib:/usr/lib", null);
        Assert.Equal("/usr/lib", kept);
    }

    [Fact]
    public void Scrub_RemovesBundledLibraryPaths_AndVlcPluginPath()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["LD_LIBRARY_PATH"] = $"{AppDir}/usr/lib:{AppDir}/usr/lib/vlc:/usr/lib";
        psi.Environment["VLC_PLUGIN_PATH"] = $"{AppDir}/usr/lib/vlc/plugins";
        psi.Environment["NOCTIS_BUNDLED_VLC"] = "1";
        psi.Environment["XDG_DATA_DIRS"] = "/usr/share";

        PlatformHelper.ScrubAppImageEnvironment(psi, AppDir, "/home/deck/Noctis.AppImage");

        Assert.Equal("/usr/lib", psi.Environment["LD_LIBRARY_PATH"]);
        Assert.False(psi.Environment.ContainsKey("VLC_PLUGIN_PATH"));
        Assert.False(psi.Environment.ContainsKey("NOCTIS_BUNDLED_VLC"));
        Assert.Equal("/usr/share", psi.Environment["XDG_DATA_DIRS"]);
    }

    [Fact]
    public void Scrub_RemovesTheVariable_WhenOnlyBundledEntriesRemain()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["LD_LIBRARY_PATH"] = $"{AppDir}/usr/lib:{AppDir}/usr/lib/vlc";
        PlatformHelper.ScrubAppImageEnvironment(psi, AppDir, null);
        Assert.False(psi.Environment.ContainsKey("LD_LIBRARY_PATH"));
    }

    // GitHub #139 log (Fedora 44 KDE): Open Folder's dbus-send, kde-open and gio all loaded
    // /tmp/.mount_noctisDAPFBb/usr/lib although every one of them was scrubbed. That mount
    // was an EARLIER Noctis instance's: the updater's relaunch shell inherited its
    // LD_LIBRARY_PATH, the new AppRun only prepended its own mount, and the scrub dropped
    // entries under the current APPDIR alone. The old mount stays alive (the new process
    // tree inherits its runtime's keep-alive pipe), so its Ubuntu libraries still load.
    private const string EarlierMount = "/tmp/.mount_noctisDAPFBb";

    [Fact]
    public void StripAppDirEntries_DropsAnEarlierInstancesMount_Too()
    {
        var inherited = $"{AppDir}/usr/lib:{AppDir}/usr/lib/vlc:{EarlierMount}/usr/lib:{EarlierMount}/usr/lib/vlc:/usr/lib64";
        Assert.Equal("/usr/lib64", PlatformHelper.StripAppDirEntries(inherited, AppDir));
    }

    [Fact]
    public void StripAppDirEntries_DropsMountsUnderAnyTempDir()
    {
        // The runtime mounts under $TMPDIR, not always /tmp.
        Assert.Equal("/usr/lib", PlatformHelper.StripAppDirEntries("/var/tmp/.mount_NoctisXyz/usr/lib:/usr/lib", null));
    }

    [Fact]
    public void Scrub_RemovesAnEarlierInstancesLibraries_FromAHostTool()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["LD_LIBRARY_PATH"] = $"{AppDir}/usr/lib:{AppDir}/usr/lib/vlc:{EarlierMount}/usr/lib:{EarlierMount}/usr/lib/vlc";
        PlatformHelper.ScrubAppImageEnvironment(psi, AppDir, "/home/user/AppImages/noctis.appimage");
        Assert.False(psi.Environment.ContainsKey("LD_LIBRARY_PATH"));
    }

    [Fact]
    public void ProcessLoaderPaths_AreScrubbedForEveryChild()
    {
        var env = new System.Collections.Generic.Dictionary<string, string?>
        {
            ["LD_LIBRARY_PATH"] = $"{AppDir}/usr/lib:{EarlierMount}/usr/lib:/opt/custom/lib",
            ["LD_PRELOAD"] = $"{AppDir}/usr/lib/libfoo.so",
        };
        var changes = PlatformHelper.AppImageLoaderPathChanges(k => env.GetValueOrDefault(k), AppDir, null);
        Assert.Contains(("LD_LIBRARY_PATH", (string?)"/opt/custom/lib"), changes);
        Assert.Contains(("LD_PRELOAD", (string?)null), changes);
    }

    [Fact]
    public void ProcessLoaderPaths_AreLeftAlone_OutsideAnAppImage()
    {
        var changes = PlatformHelper.AppImageLoaderPathChanges(_ => "/opt/custom/lib", null, null);
        Assert.Empty(changes);
    }

    // Without --print-reply dbus-send exits 0 once the call is queued, even with no
    // FileManager1 provider on the bus (WSL check: exit 0 vs exit 1 ServiceUnknown), so
    // Show in Folder reported success, opened nothing and never fell back.
    [Theory]
    [InlineData("ShowItems")]
    [InlineData("ShowFolders")]
    public void FileManager1Call_WaitsForTheReply(string method)
    {
        var args = PlatformHelper.FileManager1Call(method, "file:///home/u/a%2Cb.flac");
        Assert.Contains("--print-reply", args);
        Assert.Contains("org.freedesktop.FileManager1." + method, args);
        Assert.Contains("array:string:file:///home/u/a%2Cb.flac", args);
    }

    [Fact]
    public void Scrub_IsNoOp_OutsideAnAppImage()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["LD_LIBRARY_PATH"] = "/opt/custom/lib";
        psi.Environment["VLC_PLUGIN_PATH"] = "/opt/vlc/plugins";
        PlatformHelper.ScrubAppImageEnvironment(psi, null, null);
        Assert.Equal("/opt/custom/lib", psi.Environment["LD_LIBRARY_PATH"]);
        Assert.Equal("/opt/vlc/plugins", psi.Environment["VLC_PLUGIN_PATH"]);
    }
}
