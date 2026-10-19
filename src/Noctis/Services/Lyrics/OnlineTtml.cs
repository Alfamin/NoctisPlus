namespace Noctis.Services.Lyrics;

/// <summary>
/// A TTML document from an online source (lrc.red, Unison) as the text an online result carries:
/// timed TTML becomes enhanced LRC through <see cref="TtmlParser.ToLrc(IReadOnlyList{Noctis.Models.LyricLine})"/>,
/// which keeps word timings as ELRC word tags, duet voices as "v2:"/"v3:" and background vocals as
/// "[bg: …]" lines — all of which the lyrics page, the offline cache and the .elrc sidecar read back.
/// Untimed TTML is plain text.
/// </summary>
internal static class OnlineTtml
{
    /// <summary>(Synced ELRC/LRC or null when untimed, plain text); null when the TTML has no lines or does not parse.</summary>
    public static (string? Synced, string Plain)? Convert(string? ttml)
    {
        // joinSplitWords off: the shipped default of Settings ▸ Join Split Words.
        var (lines, plain) = TtmlParser.Parse(ttml, joinSplitWords: false);
        if (lines is not { Count: > 0 } || string.IsNullOrWhiteSpace(plain)) return null;
        var synced = lines.Any(l => l.Timestamp.HasValue) ? TtmlParser.ToLrc(lines) : null;
        return (string.IsNullOrWhiteSpace(synced) ? null : synced, plain);
    }
}
