using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

/// <summary>Optional private deployment defaults. Never compiled into the public DLL/package.</summary>
internal sealed class TelegramDefaults
{
    public int ApiId { get; private set; }
    public string ApiHash { get; private set; } = "";
    public string Error { get; private set; } = "";
    public bool Available => ApiId > 0 && ApiHash.Length == 32;

    public static TelegramDefaults Read(string path)
    {
        var result = new TelegramDefaults();
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return result;
            if (file.Length > 4096 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var id = doc.RootElement.GetProperty("api_id").GetInt32();
            var hash = doc.RootElement.GetProperty("api_hash").GetString() ?? "";
            if (id <= 0 || !Regex.IsMatch(hash, "\\A[a-fA-F0-9]{32}\\z")) throw new InvalidDataException();
            result.ApiId = id; result.ApiHash = hash;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            result.Error = "TELEGRAM_DEFAULTS_INVALID: the private defaults file could not be read. Open Advanced and enter your own Telegram API id/hash.";
        }
        return result;
    }
}
