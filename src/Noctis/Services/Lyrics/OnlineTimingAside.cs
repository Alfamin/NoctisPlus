using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Noctis.Services.Lyrics;

/// <summary>
/// Owner #233: a song's online word timing (lrc.red, Unison, Kugou) set aside for WordLyrics' timing. While WordLyrics
/// runs it is still what the lyrics page shows; once WordLyrics has timed the song it is kept, so the lyrics credit can
/// switch between the two either way. Only marked online .ttml files are ever moved; nothing is deleted.
/// </summary>
internal static class OnlineTimingAside
{
    static readonly ConcurrentDictionary<string, string> Running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where kept online timings live (the WordLyrics data folder's "online-kept"); null = none kept.</summary>
    public static string? Folder { get; set; }

    static string Stem(string song) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(song)) ?? "", Path.GetFileNameWithoutExtension(song));

    static string? KeptPath(string song)
    {
        if (Folder is not { Length: > 0 } folder) return null;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Stem(song).ToLowerInvariant())));
        return Path.Combine(folder, hash + ".ttml");
    }

    static string Ttml(string song) => Stem(song) + ".ttml";

    /// <summary>A WordLyrics run holds the song's online timing in <paramref name="held"/>.</summary>
    public static void Holding(string song, string held) => Running[Stem(song)] = held;

    public static void Released(string song) => Running.TryRemove(Stem(song), out _);

    /// <summary>The online timing a running WordLyrics job holds for the song (shown meanwhile), or null.</summary>
    public static string? ReadHeld(string song)
    {
        if (!Running.TryGetValue(Stem(song), out var held)) return null;
        try { return File.Exists(held) ? File.ReadAllText(held) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>WordLyrics timed the song: its online timing is kept beside, not deleted.</summary>
    public static void Keep(string song, string held)
    {
        if (KeptPath(song) is not { } kept) { File.Delete(held); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.Move(held, kept, overwrite: true);
    }

    /// <summary>The kept online timing's source ("Kugou") when the song does not show it now, or null.</summary>
    public static string? KeptSource(string song)
    {
        if (KeptPath(song) is not { } kept || !File.Exists(kept) || File.Exists(Ttml(song))) return null;
        try { return OnlineTimingSidecar.SourceOf(File.ReadAllText(kept)) is { } source ? OnlineTimingSidecar.Name(source) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Shows the kept online timing again (it out-ranks WordLyrics' .elrc, which stays).</summary>
    public static bool UseOnline(string song)
    {
        if (KeptPath(song) is not { } kept || !File.Exists(kept) || File.Exists(Ttml(song))) return false;
        try { File.Move(kept, Ttml(song)); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Shows WordLyrics' timing: the online .ttml steps aside into the keep (only a marked one, only when the
    /// song has WordLyrics' .elrc to show instead).</summary>
    public static bool UseWordLyrics(string song)
    {
        var ttml = Ttml(song);
        if (KeptPath(song) is not { } kept || !File.Exists(ttml) || !File.Exists(Stem(song) + ".elrc")) return false;
        try
        {
            if (OnlineTimingSidecar.SourceOf(File.ReadAllText(ttml)) == null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            File.Move(ttml, kept, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
