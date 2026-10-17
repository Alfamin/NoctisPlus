namespace Noctis.Features.WordLyrics;

internal enum WordTimingMode { Off, Light, Medium, Full }

/// <summary>The native choice and its backwards-compatible plugin settings. Pure policy;
/// not registered until the native worker and UI are ready.</summary>
internal static class WordTimingSettings
{
    internal const string ModeKey = "defaultMode";

    public static WordTimingMode Read(IReadOnlyDictionary<string, string> values)
    {
        var mode = Value(values, ModeKey);
        if (TryMode(mode, out var chosen)) return chosen;
        // Missing auto was true in the plugin, and missing speed was Half.
        if (bool.TryParse(Value(values, "auto"), out var automatic) && !automatic) return WordTimingMode.Off;
        return Value(values, "speed")?.Trim().ToLowerInvariant() switch
        {
            "light" => WordTimingMode.Light,
            "full" => WordTimingMode.Full,
            _ => WordTimingMode.Medium,
        };
    }

    /// <summary>Call on the stored raw values BEFORE manifest defaults are applied. An
    /// existing native choice wins; retain unrelated settings and the old speed for Off.</summary>
    public static bool Migrate(Dictionary<string, string> values)
    {
        var chosen = Read(values);
        return Write(values, chosen);
    }

    /// <summary>Keep old auto/speed meaningful for rollback to the plugin. Off doesn't erase
    /// the old effort preference; enabling a mode maps Medium to the backend's Half.</summary>
    public static bool Write(Dictionary<string, string> values, WordTimingMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var changed = Put(values, ModeKey, mode.ToString());
        changed |= Put(values, "auto", mode == WordTimingMode.Off ? "false" : "true");
        if (BackendSpeed(mode) is { } speed) changed |= Put(values, "speed", speed);
        return changed;
    }

    public static string? BackendSpeed(WordTimingMode mode) => mode switch
    {
        WordTimingMode.Off => null,
        WordTimingMode.Light => "Light",
        WordTimingMode.Medium => "Half",
        WordTimingMode.Full => "Full",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static bool TryMode(string? value, out WordTimingMode mode)
        => Enum.TryParse(value?.Trim(), true, out mode) && Enum.IsDefined(mode) &&
           string.Equals(value?.Trim(), mode.ToString(), StringComparison.OrdinalIgnoreCase);

    private static string? Value(IReadOnlyDictionary<string, string> values, string key)
        => values.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool Put(Dictionary<string, string> values, string key, string value)
    {
        var oldKey = values.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
        if (values.TryGetValue(oldKey, out var old) && old == value) return false;
        values[oldKey] = value; return true;
    }
}
