using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

/// <summary>One track the bot listed. <paramref name="FileName"/> is the name it is saved under, without the extension; <paramref name="Fetch"/> downloads it.</summary>
internal sealed record BotTrack(string Title, string Artist, TimeSpan? Duration, string FileName, TrackFetch Fetch)
{
    public MusicRequest? Request { get; init; }
}

/// <summary>Writes a track's audio to <paramref name="output"/> and returns its file extension ("flac").</summary>
internal delegate Task<string> TrackFetch(Stream output, IProgress<double>? progress, CancellationToken ct);

/// <summary>The bot answered, but not in the form this plugin reads.</summary>
internal sealed class BotAnswerException : Exception
{
    public BotAnswerException(string message) : base(message) { }
}

/// <summary>Reading the bot's result list, and telling whether two descriptions mean the same song. No Telegram in here.</summary>
internal static partial class BotText
{
    /// <summary>One numbered line of the bot's result list.</summary>
    public sealed record Line(string Number, string Artist, string Title, TimeSpan? Duration);

    // "3. Kevin MacLeod - Darkest Child (3:59)"
    [GeneratedRegex(@"^(\d+)\.\s+(.+?) - (.+?)(?:\s+\((\d+(?::\d\d){1,2})\))?\s*$", RegexOptions.Multiline)]
    private static partial Regex ResultLine();

    // "(Live)", "[Remastered 2011]"
    [GeneratedRegex(@"\([^()]*\)|\[[^\[\]]*\]")]
    private static partial Regex Bracketed();

    public static IReadOnlyList<Line> Lines(string text)
        => ResultLine().Matches(text.Replace("\r", ""))
            .Select(m => new Line(m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim(), ParseDuration(m.Groups[4].Value)))
            .ToList();

    /// <summary>"4:18" or "1:02:03".</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        if (text.Length == 0) return null;
        long seconds = 0;
        foreach (var part in text.Split(':'))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
            seconds = seconds * 60 + number;
        }
        return seconds <= TimeSpan.FromDays(1).TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>
    /// A name reduced to what matters when comparing: letters and digits only, lower case, no
    /// accents, and the Arabic forms of ya and kaf written the Persian way.
    /// </summary>
    public static string Key(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var key = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            if (!char.IsLetterOrDigit(c)) continue;
            key.Append(c switch { 'ي' or 'ى' => 'ی', 'ك' => 'ک', _ => char.ToLowerInvariant(c) });
        }
        return key.ToString();
    }

    /// <summary>The title without its bracketed additions: "Song (Live) [2011]" → "Song".</summary>
    public static string Plain(string title)
    {
        var plain = Bracketed().Replace(title, " ").Trim();
        return plain.Length == 0 ? title.Trim() : plain;
    }

    /// <summary>
    /// Whether a file the bot sent is the listed track that was asked for. The bot answers every
    /// request in the same chat, so a file can also belong to an earlier request that came late.
    /// It is the right one when its length or its name fits; when the file says neither, there
    /// is nothing to tell it by and it is taken.
    /// </summary>
    /// <param name="performer">The performer the file names, if any.</param>
    /// <param name="title">The title the file names, if any.</param>
    /// <param name="fileName">The file's own name, if any.</param>
    /// <param name="seconds">The file's length; 0 when it does not say.</param>
    public static bool IsRequested(Line wanted, string? performer, string? title, string? fileName, int seconds)
    {
        var lengthKnown = wanted.Duration is not null && seconds > 0;
        var sentTitle = Key(title);
        var sentAll = Key(performer) + sentTitle + Key(Path.GetFileNameWithoutExtension(fileName ?? ""));
        var nameKnown = sentAll.Length > 0;
        if (!lengthKnown && !nameKnown) return true;

        if (lengthKnown && Math.Abs(wanted.Duration!.Value.TotalSeconds - seconds) <= 2) return true;
        if (!nameKnown) return false;
        return Within(Key(wanted.Title), sentTitle, sentAll) || Within(Key(Plain(wanted.Title)), sentTitle, sentAll);
    }

    // Equal, or one inside the other. A short name ("Go") is inside almost anything, so it only counts when equal.
    private static bool Within(string wanted, string sentTitle, string sentAll)
    {
        if (wanted.Length == 0) return false;
        if (wanted == sentTitle) return true;
        if (wanted.Length >= 4 && sentAll.Contains(wanted, StringComparison.Ordinal)) return true;
        return sentTitle.Length >= 4 && wanted.Contains(sentTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a listed track and a library track are the same song: the same title by an artist
    /// that fits and, where both lengths are known, within three seconds of each other. Titles
    /// that only agree without their bracketed additions count only with matching lengths.
    /// </summary>
    public static bool SameSong(string artist, string title, TimeSpan? duration, string libraryArtist, string libraryTitle, TimeSpan libraryDuration)
    {
        var lengthKnown = duration is not null && libraryDuration > TimeSpan.Zero;
        if (lengthKnown && Math.Abs((duration!.Value - libraryDuration).TotalSeconds) > 3) return false;

        var wanted = Key(title);
        if (wanted.Length == 0) return false;
        if (wanted != Key(libraryTitle))
        {
            var plain = Key(Plain(title));
            if (!lengthKnown || plain.Length == 0 || plain != Key(Plain(libraryTitle))) return false;
        }

        var a = Key(artist);
        var b = Key(libraryArtist);
        if (a.Length == 0 || b.Length == 0) return true;
        return a == b || (a.Length >= 3 && b.Contains(a, StringComparison.Ordinal)) || (b.Length >= 3 && a.Contains(b, StringComparison.Ordinal));
    }
}
