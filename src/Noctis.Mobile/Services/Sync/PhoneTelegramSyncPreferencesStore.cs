using System.Text.Json;

namespace Noctis.Mobile.Services.Sync;

/// <summary>Android-only transfer policy for the optional Telegram cloud sync, separate from PC/LAN SyncEnabled.</summary>
public sealed record PhoneTelegramSyncPreferences(bool Enabled = false, bool WifiOnly = true, bool ChargingOnly = false)
{
    /// <summary>Whether an automatic transfer may start under the user's saved phone policy.</summary>
    public bool AllowsTransfer(bool onWifi, bool charging) => Enabled && (!WifiOnly || onWifi) && (!ChargingOnly || charging);
}

public sealed record PhoneTelegramSyncPreferencesLoad(PhoneTelegramSyncPreferences Preferences, bool IsCorrupt);

/// <summary>Stores non-secret phone sync choices in the host's private no-backup directory.</summary>
public static class PhoneTelegramSyncPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static PhoneTelegramSyncPreferences Default { get; } = new();

    public static PhoneTelegramSyncPreferencesLoad Load(string path)
    {
        if (!File.Exists(path)) return new(Default, false);
        try
        {
            var value = JsonSerializer.Deserialize<PhoneTelegramSyncPreferences>(File.ReadAllBytes(path), JsonOptions);
            return value is null ? new(Default, true) : new(value, false);
        }
        catch (JsonException) { return new(Default, true); }
    }

    public static void Save(string path, PhoneTelegramSyncPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temp = fullPath + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, value, JsonOptions);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, fullPath, overwrite: true);
    }
}
