namespace FreeMusicFinder;

/// <summary>
/// Owner #185: a new install downloads MP3 by default. Anyone who used Free Music Finder before, when FLAC was the
/// default, keeps FLAC. Decided once and saved, so it never changes again on its own.
/// </summary>
internal static class QualityDefault
{
    internal const string Key = "preferredQuality";

    /// <returns>True when the saved settings were changed.</returns>
    public static bool Settle(Dictionary<string, string> stored, string dataFolder)
    {
        if (stored.ContainsKey(Key)) return false;
        var usedBefore = stored.Count > 0 || (Directory.Exists(dataFolder) && Directory.EnumerateFileSystemEntries(dataFolder).Any());
        stored[Key] = usedBefore ? "FLAC" : "MP3";
        return true;
    }
}
