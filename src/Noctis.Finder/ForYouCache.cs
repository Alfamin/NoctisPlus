using System.Text.Json;

namespace FreeMusicFinder;

/// <summary>A bounded last successful page, not the user's history. A bad/redirected cache never blocks discovery.
/// The same file rules keep the few earlier pages the shelf can go back to (owner #139).</summary>
internal sealed class ForYouCache(string path)
{
    internal sealed record Page(int Version, DateTimeOffset At, DateTimeOffset Until, DiscoverResult Result);
    /// <summary>Earlier pages, newest first.</summary>
    internal sealed record Earlier(int Version, List<Page> Pages);
    private const long PageBytes = 2 * 1024 * 1024, EarlierBytes = 8 * 1024 * 1024;

    public Page? Read(DateTimeOffset now, bool expired = false)
    {
        try
        {
            if (!Safe() || !File.Exists(path) || new FileInfo(path).Length > PageBytes) return null;
            var page = JsonSerializer.Deserialize<Page>(File.ReadAllText(path));
            return Valid(page, now, expired) ? page : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    // Owner #248: a page kept to go back to (expired) stays at any age, until the listener removes it or ten newer pages
    // push it out; only a page served as the fresh one has a week.
    private static bool Valid(Page? page, DateTimeOffset now, bool expired) =>
        page is { Version: 1, Result.Songs: not null, Result.Artists: not null, Result.Notice: not null } &&
        page.At <= now.AddMinutes(5) && (expired || page.At >= now.AddDays(-7) && page.Until > now) && page.Until <= page.At.AddDays(7) &&
        page.Result.Songs.Count <= 100 && page.Result.Artists.Count <= 30 &&
        page.Result.Songs.All(s => s is { Track.Song.Artists: { Count: > 0 }, Track.Song.Id: not null, Track.Song.Title: not null, Because: not null } &&
            s.Track.Song.Artists.All(a => a is { Id: not null, Name: not null }) && double.IsFinite(s.Score)) &&
        page.Result.Artists.All(a => a is { Artist.Id: not null, Artist.Name: not null, Because: not null });

    /// <summary>The earlier pages to go back to, newest first. A bad entry drops alone.</summary>
    public List<Page> ReadEarlier(DateTimeOffset now, int keep)
    {
        try
        {
            if (!Safe() || !File.Exists(path) || new FileInfo(path).Length > EarlierBytes) return [];
            var saved = JsonSerializer.Deserialize<Earlier>(File.ReadAllText(path));
            return saved is { Version: 1, Pages: not null } ? saved.Pages.Where(p => Valid(p, now, expired: true)).Take(keep).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return []; }
    }

    public void Write(Page page)
    {
        try { if (Safe() && JsonSerializer.SerializeToUtf8Bytes(page).Length <= PageBytes) AtomicJson.Write(path, page); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Noctis.Services.DebugLog.Write("ForYou", $"Page cache unavailable: {ex.GetType().Name}"); }
    }

    public void WriteEarlier(IReadOnlyList<Page> pages)
    {
        var saved = new Earlier(1, [.. pages]);
        try { if (Safe() && JsonSerializer.SerializeToUtf8Bytes(saved).Length <= EarlierBytes) AtomicJson.Write(path, saved); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Delete()
    {
        try { if (Safe() && File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private bool Safe()
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
            { Noctis.Services.DebugLog.Write("ForYou", $"Redirected cache ancestor kept untouched: {dir.Name}"); return false; }
        return !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    }
}
