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

    /// <summary>Owner #186: the release's built-in pair ships scrambled (telegram-app.dat), so it is not readable or
    /// copyable as text. Not encryption: anything an app uses can be dug out of it, but not by opening a file.</summary>
    internal const string PackedFile = "telegram-app.dat";
    private static readonly byte[] PackKey = "Noctis Plus - Free Music Finder - #186"u8.ToArray();

    internal static byte[] Pack(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= PackKey[i % PackKey.Length];
        return bytes;
    }

    private static string Unpack(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= PackKey[i % PackKey.Length];
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public static TelegramDefaults Read(string path) => Read(path, packed: false);
    public static TelegramDefaults ReadPacked(string path) => Read(path, packed: true);

    private static TelegramDefaults Read(string path, bool packed)
    {
        var result = new TelegramDefaults();
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return result;
            if (file.Length > 4096 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException();
            var text = packed ? Unpack(File.ReadAllBytes(path)) : File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
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
