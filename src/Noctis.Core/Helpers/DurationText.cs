namespace Noctis.Helpers;

/// <summary>Total-length labels for albums, playlists and artists ("1h 12m", "54 min").</summary>
public static class DurationText
{
    /// <summary>
    /// "1h 12m" from an hour on, whole minutes below that, and seconds under a minute ("24 sec"), so a short
    /// single or playlist never reads "0 min".
    /// </summary>
    /// <param name="minutes">The minute suffix the surface already uses: " min" or "m".</param>
    public static string Total(TimeSpan total, string minutes = " min")
    {
        if (total.TotalHours >= 1) return $"{(int)total.TotalHours}h {total.Minutes}m";
        if (total > TimeSpan.Zero && total.TotalMinutes < 1) return $"{Math.Max(1, (int)total.TotalSeconds)} sec";
        return $"{(int)total.TotalMinutes}{minutes}";
    }
}
