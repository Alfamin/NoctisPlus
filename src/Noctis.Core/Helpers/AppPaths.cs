namespace Noctis.Helpers;

/// <summary>
/// Centralized lookup for Noctis Plus's per-user data directory (<see cref="ProductIdentity.DataFolderName"/>). Honors the
/// NOCTIS_PLUS_DATA_DIR environment variable so dev builds can be pointed at a
/// different folder than the installed copy and avoid clobbering each
/// other's library/settings/playlists. Debug builds additionally default to
/// a separate, empty "Noctis Plus-dev" profile. Profile import is always explicit.
/// </summary>
public static class AppPaths
{
    private static readonly object _rootLock = new();
    private static string? _dataRoot;

    /// <summary>Resolved lazily so a host can <see cref="OverrideDataRoot"/> first.</summary>
    public static string DataRoot
    {
        get { lock (_rootLock) return _dataRoot ??= ResolveDataRoot(); }
    }

    /// <summary>
    /// Points the data root somewhere else (the Android head passes Context.FilesDir).
    /// Must run before any service reads <see cref="DataRoot"/>: a later call with a
    /// different root throws, because half the services would already be writing to
    /// the old one. Re-asserting the current root is a no-op.
    /// </summary>
    public static void OverrideDataRoot(string root)
    {
        var full = Path.GetFullPath(root);
        lock (_rootLock)
        {
            if (_dataRoot != null && !string.Equals(_dataRoot, full, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"AppPaths.DataRoot is already '{_dataRoot}'; override it before any service touches it.");
            _dataRoot = full;
        }
    }

    private static string ResolveDataRoot()
    {
        var env = Environment.GetEnvironmentVariable("NOCTIS_PLUS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            // Expand tokens like %LOCALAPPDATA% (launchSettings.json sets the dev
            // profile that way) so we never create a literal "%LOCALAPPDATA%..."
            // folder in the working directory, and resolve to an absolute path.
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(env));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
#if DEBUG
        return Path.Combine(appData, ProductIdentity.DevDataFolderName);
#else
        return Path.Combine(appData, ProductIdentity.DataFolderName);
#endif
    }

}
