namespace Noctis.Mobile.Services;

/// <summary>
/// Deletes a song's file from the phone (#306, Remove from Phone). True when the file is gone (or already was);
/// false when it cannot be deleted: a music folder the phone only has read access to, or Android's refusal. The
/// caller hides such a song instead, so nothing comes back on the next scan.
/// </summary>
public interface ISongFileRemover
{
    bool TryDelete(string path);
}

/// <summary>Plain files: the phone's own downloads and the songs synced from the computer. A content:// address is
/// left to the host's remover.</summary>
public sealed class PlainSongFileRemover : ISongFileRemover
{
    public bool TryDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}