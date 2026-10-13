using Android.Content;

namespace Noctis.Android.Services;

/// <summary>
/// Whether a Storage Access Framework URI is still covered by one of the app's persisted read
/// grants. A grant can disappear without the app hearing about it (the user revokes it, or a
/// reinstall drops it), and every song under that folder then fails with a provider
/// SecurityException that Media3 reports as a bare "Source error".
/// </summary>
internal static class SafGrants
{
    /// <summary>True for anything that is not a content:// URI (plain files need no grant), and
    /// when the grant list cannot be read: only a confirmed missing grant is reported.</summary>
    public static bool CanRead(Context context, string uri)
    {
        if (!uri.StartsWith("content://", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var grants = context.ContentResolver?.PersistedUriPermissions;
            if (grants == null) return true;
            return grants.Any(g => g.IsReadPermission && g.Uri?.ToString() is { } granted && IsCoveredBy(uri, granted));
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>The grant's own URI, or one under it ("…/tree/X/document/…"). A bare string
    /// prefix also matched a sibling or child TREE ("…/tree/primary%3AMusic%2FTones" under a
    /// grant for "…/tree/primary%3AMusic"), which Android does not let the app read: Settings
    /// said the folder was fine while every scan of it failed.</summary>
    internal static bool IsCoveredBy(string uri, string granted) =>
        uri.Equals(granted, StringComparison.Ordinal) || uri.StartsWith(granted + "/", StringComparison.Ordinal);
}
