using Noctis.Helpers;
using Noctis.Services.ProfileImport;

namespace Noctis.Services;

/// <summary>
/// Decides at launch whether this run carries the official Noctis profile into a new Noctis Plus profile. Only a
/// release build on its default profile does it on its own; a developer can point any build at a test profile with
/// NOCTIS_PLUS_IMPORT_FROM. Reads only; the copy itself runs behind the import window.
/// </summary>
internal static class ProfileImportStartup
{
    internal const string ImportFromVariable = "NOCTIS_PLUS_IMPORT_FROM";

    internal static ProfileImporter? Prepare()
    {
#if DEBUG
        const bool debugBuild = true;
#else
        const bool debugBuild = false;
#endif
        return Prepare(Environment.GetEnvironmentVariable, debugBuild, AppPaths.DataRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }

    internal static ProfileImporter? Prepare(Func<string, string?> env, bool debugBuild, string dataRoot, string appData)
    {
        try
        {
            var official = OfficialProfile(env, appData);
            var explicitSource = env(ImportFromVariable);
            string source;
            if (!string.IsNullOrWhiteSpace(explicitSource))
                source = ProfileImporter.Normalize(explicitSource);
            // A developer build or a custom Plus profile never fills itself from the real Noctis profile.
            else if (debugBuild || !string.IsNullOrWhiteSpace(env("NOCTIS_PLUS_DATA_DIR")))
                return null;
            else
                source = official;

            var decision = ProfileImporter.Evaluate(source, dataRoot);
            DebugLog.Write("ProfileImport", $"{decision.Kind}: {decision.Reason}");
            if (decision.Kind == ProfileImportDecisionKind.NotNeeded) return null;

            var isOfficial = string.Equals(source, official,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            return new ProfileImporter(source, dataRoot, isOfficial ? ProfileImporter.IsOfficialNoctisRunning : null);
        }
        catch (Exception ex)
        {
            // Never a reason not to start: the app opens with the profile it has.
            DebugLog.Write("ProfileImport", $"not offered: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Where official Noctis keeps its profile: its own NOCTIS_DATA_DIR override, else %APPDATA%\Noctis.</summary>
    internal static string OfficialProfile(Func<string, string?> env, string appData)
    {
        var overridden = env("NOCTIS_DATA_DIR");
        return ProfileImporter.Normalize(string.IsNullOrWhiteSpace(overridden) ? Path.Combine(appData, "Noctis") : overridden);
    }
}
