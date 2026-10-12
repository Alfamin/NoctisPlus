namespace Noctis.Models;

/// <summary>Where the app puts lyrics it saves (GitHub #135).</summary>
public enum LyricsSaveTarget
{
    /// <summary>Lyrics files beside the song (.lrc, .elrc for word timings, .ttml on request): the behaviour before #135.</summary>
    Sidecar,

    /// <summary>Inside the audio file's tags only; no lyrics file is written.</summary>
    Embedded,

    /// <summary>Lyrics files beside the song and the audio file's tags.</summary>
    Both,
}

/// <summary>Which form of online lyrics an automatic pick keeps (GitHub #135).</summary>
public enum LyricsPreferredType
{
    /// <summary>The richest the sources have: word-synced, then line-synced, then plain (the pick before #135).</summary>
    WordSynced,

    /// <summary>Line timings only: word timings are dropped from what is kept.</summary>
    LineSynced,

    /// <summary>Plain text only, no timings.</summary>
    Plain,
}

/// <summary>Parsing for the string-valued lyrics save settings in <see cref="AppSettings"/>.</summary>
public static class LyricsSaveOptions
{
    /// <summary>The stored setting as a target; anything unknown is the default, <see cref="LyricsSaveTarget.Sidecar"/>.</summary>
    public static LyricsSaveTarget ParseTarget(string? value) =>
        Enum.TryParse<LyricsSaveTarget>(value, ignoreCase: true, out var target) && Enum.IsDefined(target)
            ? target : LyricsSaveTarget.Sidecar;

    /// <summary>The stored setting as a type; anything unknown is the default, <see cref="LyricsPreferredType.WordSynced"/>.</summary>
    public static LyricsPreferredType ParsePreferredType(string? value) =>
        Enum.TryParse<LyricsPreferredType>(value, ignoreCase: true, out var type) && Enum.IsDefined(type)
            ? type : LyricsPreferredType.WordSynced;

    /// <summary>Lyrics files are written for this target.</summary>
    public static bool WritesSidecars(this LyricsSaveTarget target) => target != LyricsSaveTarget.Embedded;

    /// <summary>The audio file's tags are written for this target.</summary>
    public static bool WritesTags(this LyricsSaveTarget target) => target != LyricsSaveTarget.Sidecar;
}
