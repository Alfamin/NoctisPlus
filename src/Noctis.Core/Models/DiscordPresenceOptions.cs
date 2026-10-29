namespace Noctis.Models;

/// <summary>
/// What Discord's one-line status reads after "Listening to" (the member list and the
/// profile). The full card shows the title and the artist either way (Discord ask, 2026-10-14).
/// </summary>
public enum DiscordStatusDisplay
{
    /// <summary>The artist: the line before this setting existed.</summary>
    Artist,

    /// <summary>The song title.</summary>
    Title,

    /// <summary>The Discord application's name: "Listening to Noctis".</summary>
    AppName,
}

/// <summary>Parsing for the string-valued Discord settings in <see cref="AppSettings"/>.</summary>
public static class DiscordPresenceOptions
{
    /// <summary>The stored setting as a status line; anything unknown is the default, <see cref="DiscordStatusDisplay.Artist"/>.</summary>
    public static DiscordStatusDisplay ParseStatusDisplay(string? value) =>
        Enum.TryParse<DiscordStatusDisplay>(value, ignoreCase: true, out var display) && Enum.IsDefined(display)
            ? display : DiscordStatusDisplay.Artist;
}
