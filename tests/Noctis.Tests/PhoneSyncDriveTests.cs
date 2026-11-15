using FreeMusicFinder;
using Noctis.Mobile.Services.Finder;
using Noctis.Models;
using Noctis.TelegramSync;
using Xunit;

namespace Noctis.Tests;

public sealed class PhoneSyncDriveTests
{
    [Fact]
    public void PhoneHost_ExposesStableSyncDriveFromItsOwnFinderSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "noctis-phone-sync-drive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var rig = MobileFixtures.MakeRig(Array.Empty<Track>());
        using var host = new PhoneFinderHost(
            Path.Combine(root, "data"), Path.Combine(root, "music"), Path.Combine(root, "missing-telegram-pair"),
            rig.Library, rig.Shell.Library, work => work(), _ => { });
        try
        {
            Assert.IsType<WTelegramDrive>(host.SyncDrive);
            Assert.Same(host.Session.SyncDrive, host.SyncDrive);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
