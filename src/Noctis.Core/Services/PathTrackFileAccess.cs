namespace Noctis.Services;

/// <summary>
/// <see cref="ITrackFileAccess"/> for a track that is an ordinary file (on Android: one Music Finder
/// saved in the app's private storage, where there is no SAF document). Same naming rule
/// (<see cref="SidecarNames.Match"/>) and the same size cap as the SAF reader; never throws.
/// </summary>
public sealed class PathTrackFileAccess : ITrackFileAccess
{
    public const int MaxSidecarBytes = 4 * 1024 * 1024;

    /// <summary>True for an absolute file-system path (not a content:// or other URI).</summary>
    public static bool IsFilePath(string? trackPath)
        => !string.IsNullOrEmpty(trackPath) && !trackPath.Contains("://", StringComparison.Ordinal) && Path.IsPathFullyQualified(trackPath);

    public SidecarFile? ReadSidecar(string trackPath, IReadOnlyList<string> extensions)
    {
        try
        {
            if (!IsFilePath(trackPath) || Path.GetDirectoryName(trackPath) is not { Length: > 0 } dir || !Directory.Exists(dir)) return null;
            var siblings = Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>().ToList();
            if (SidecarNames.Match(Path.GetFileName(trackPath), siblings, extensions) is not { } hit) return null;
            var file = new FileInfo(Path.Combine(dir, hit.Name));
            if (!file.Exists || file.Length > MaxSidecarBytes) return null;
            return new SidecarFile(hit.Extension, File.ReadAllBytes(file.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DebugLog.Write("Lyrics", $"Sidecar lookup failed: {ex.GetType().Name}");
            return null;
        }
    }

    public Stream? OpenAudio(string trackPath)
    {
        try
        {
            return IsFilePath(trackPath) && File.Exists(trackPath)
                ? new FileStream(trackPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DebugLog.Write("Lyrics", $"Audio open failed: {ex.GetType().Name}");
            return null;
        }
    }
}
