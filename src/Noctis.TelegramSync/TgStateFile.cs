using System.Text.Json;

namespace Noctis.TelegramSync;

internal static class TgStateFile
{
    internal static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(data);
            file.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(temp, path, overwrite: true);
    }

    internal static JsonDocument? Read(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try { return JsonDocument.Parse(File.ReadAllBytes(candidate)); }
            catch (JsonException) { }
        }
        if (File.Exists(path) || File.Exists(path + ".bak"))
            throw new InvalidDataException("Sync state is damaged. Keep the files and restore from the channel snapshot.");
        return null;
    }
}
