using Noctis.Mobile.Services.Sync;
using Xunit;

namespace Noctis.Tests;

public sealed class PhoneTelegramSyncPreferencesStoreTests
{
    [Fact]
    public void MissingSettingsDefaultToOffAndWifiOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "telegram-sync.json");
        var result = PhoneTelegramSyncPreferencesStore.Load(path);
        Assert.False(result.IsCorrupt);
        Assert.Equal(new PhoneTelegramSyncPreferences(Enabled: false, WifiOnly: true, ChargingOnly: false), result.Preferences);
    }

    [Fact]
    public void SaveRoundTripsAndReplacesAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "noctis-sync-preferences-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "telegram-sync.json");
        try
        {
            var value = new PhoneTelegramSyncPreferences(Enabled: true, WifiOnly: true, ChargingOnly: true);
            PhoneTelegramSyncPreferencesStore.Save(path, value);
            Assert.Equal(value, PhoneTelegramSyncPreferencesStore.Load(path).Preferences);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void CorruptSettingsStayDisabledAndAreReported()
    {
        var root = Path.Combine(Path.GetTempPath(), "noctis-sync-preferences-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "telegram-sync.json");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(path, "{");
            var result = PhoneTelegramSyncPreferencesStore.Load(path);
            Assert.True(result.IsCorrupt);
            Assert.False(result.Preferences.Enabled);
            Assert.True(result.Preferences.WifiOnly);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
