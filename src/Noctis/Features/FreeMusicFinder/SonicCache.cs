using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>Small reusable embeddings only. No preview URL, waveform or user credentials are persisted.</summary>
internal sealed class SonicCache(string folder, string model)
{
    private sealed record Entry(int Version, string Model, string Key, string CatalogId, string Domain, float[] Values);
    private string FileFor(string key, string catalogId, string domain) => Path.Combine(folder,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model + "\n" + domain + "\n" + catalogId + "\n" + key))) + ".json");

    public SonicVector? Read(string key, string catalogId, string domain = "preview")
    {
        try
        {
            var path = FileFor(key, catalogId, domain);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 100_000) return null;
            var e = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path));
            return e is { Version: 1 } && e.Model == model && e.Key == key && e.CatalogId == catalogId && e.Domain == domain
                ? SonicVector.Create(e.Model, e.Domain, e.Values ?? []) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public void Write(string key, string catalogId, SonicVector vector)
    {
        if (vector.Model != model || SonicVector.Create(vector.Model, vector.Domain, vector.Values) is not { } valid) return;
        string? temp = null;
        try
        {
            Directory.CreateDirectory(folder);
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return;
            // Bounded even for an unusually large library. Existing vectors remain usable when full.
            if (Directory.EnumerateFiles(folder, "*.json").Take(20_001).Count() > 20_000) return;
            var path = FileFor(key, catalogId, vector.Domain);
            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(new Entry(1, model, key, catalogId, vector.Domain, valid.Values)));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        finally { if (temp is not null) try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
