using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>A bounded last successful page, not the user's history. A bad/redirected cache never blocks discovery.</summary>
internal sealed class ForYouCache(string path)
{
    internal sealed record Page(int Version, DateTimeOffset At, DateTimeOffset Until, DiscoverResult Result);
    public Page? Read(DateTimeOffset now)
    {
        try
        {
            if (!Safe() || !File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return null;
            var page = JsonSerializer.Deserialize<Page>(File.ReadAllText(path));
            return page is { Version: 1, Result.Songs: not null, Result.Artists: not null, Result.Notice: not null } &&
                page.At <= now.AddMinutes(5) && page.At >= now.AddDays(-7) && page.Until > now && page.Until <= page.At.AddDays(7) &&
                page.Result.Songs.Count <= 100 && page.Result.Artists.Count <= 30 &&
                page.Result.Songs.All(s => s is { Track.Song.Artists: { Count: > 0 }, Track.Song.Id: not null, Track.Song.Title: not null, Because: not null } &&
                    s.Track.Song.Artists.All(a => a is { Id: not null, Name: not null }) && double.IsFinite(s.Score)) &&
                page.Result.Artists.All(a => a is { Artist.Id: not null, Artist.Name: not null, Because: not null }) ? page : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public void Write(Page page)
    {
        try { if (Safe() && JsonSerializer.SerializeToUtf8Bytes(page).Length <= 2 * 1024 * 1024) AtomicJson.Write(path, page); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private bool Safe()
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    }
}
