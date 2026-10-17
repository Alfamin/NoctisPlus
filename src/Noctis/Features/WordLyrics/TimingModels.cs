using System.Text.RegularExpressions;
using Noctis.Services;

namespace Noctis.Features.WordLyrics;

internal enum TimingAction { Missing, Current, Fresh, Custom }
internal sealed record ProgressState(string Message, string Detail = "", int Completed = 0, int Total = 0,
    double? Fraction = null, bool Finished = false, bool Error = false, string Report = "",
    bool Controllable = false, bool Paused = false, bool Cancelling = false);

internal static class EmbeddedWordLyrics
{
    private static readonly Regex WordStamp = new(@"<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>\s*\S", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    public static bool HasWords(string path, Func<string, string?>? read = null)
    {
        try { var text = (read ?? (p => EmbeddedLyrics.ReadFile(p).TimedText))(path); return text is { Length: > 2_000_000 } || text is not null && WordStamp.IsMatch(text); }
        catch { return false; }
    }
}

internal static class LyricDrafts
{
    internal static string Normalize(string text)
    {
        if (text.Length > 500_000) throw new InvalidOperationException("Lyrics are too large (maximum 500,000 characters).");
        return string.Join("\n", text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l != ".done"));
    }
    internal static string Save(string data, string text)
    {
        text = Normalize(text);
        if (text.Length == 0) throw new InvalidOperationException("Paste the lyrics first. Empty lines are ignored.");
        var folder = Path.Combine(data, "lyric drafts"); Directory.CreateDirectory(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("The draft folder is redirected; choose a regular feature data folder.");
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".txt");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false)); writer.Write(text + "\n");
        return path;
    }
    internal static string Existing(string path)
    {
        foreach (var ext in new[] { ".elrc", ".lrc", ".txt" })
        {
            var file = Path.ChangeExtension(path, ext);
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > 2_000_000 || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var text = File.ReadAllText(file);
                text = Regex.Replace(text, @"^\s*\[(?!offset:)[a-zA-Z]{2,}:[^\]]*\]\s*$", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                return Normalize(Regex.Replace(text, @"<\d+:\d+(?:[.:]\d+)?>", ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        var embedded = EmbeddedLyrics.ReadFile(path);
        return Normalize(embedded.TimedText is { Length: > 0 } timed ? timed : embedded.PlainText);
    }
}
