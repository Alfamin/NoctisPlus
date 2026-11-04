using System.Text;
using System.Text.Json;

namespace Noctis.TelegramSync;

/// <summary>
/// Review M7/H2: this installation's sync device id, made once and kept in the host's own private,
/// not-backed-up folder. A reinstall (or a restored backup without that folder) gets a new id, so its
/// fresh counters never overwrite the ones an earlier install wrote under the old id.
/// </summary>
public static class TgDeviceIdentity
{
    public static string LoadOrCreate(string path, string kind)
    {
        using (var saved = TgStateFile.Read(path))
            if (saved?.RootElement.TryGetProperty("device", out var stored) == true && stored.GetString() is { Length: > 0 } id
                && Valid(id)) return id;
        var created = $"{Clean(kind)}-{Guid.NewGuid():N}";
        TgStateFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["device"] = created }));
        return created;
    }

    private static bool Valid(string id) => id.Length <= 80 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string Clean(string kind)
    {
        var sb = new StringBuilder();
        foreach (var c in kind.ToLowerInvariant()) if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        return sb.Length == 0 ? "device" : sb.ToString(0, Math.Min(sb.Length, 16));
    }
}
