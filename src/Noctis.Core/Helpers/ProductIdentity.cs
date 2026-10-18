namespace Noctis.Helpers;

/// <summary>
/// Who this build is. Noctis Plus is a fork of Noctis (heartached/Noctis, MIT) with its own data folder,
/// single-instance lock, update feed, installer and Windows registration, so it never opens the profile of,
/// updates from, or installs over an official Noctis on the same computer. Every place that identifies the
/// product reads it from here; the code namespaces and assembly names stay Noctis to keep upstream merges small
/// and community plugins working.
/// </summary>
public static class ProductIdentity
{
    /// <summary>Shown to the user (window, tray, Volume Mixer, start-up entry).</summary>
    public const string Name = "Noctis Plus";

    /// <summary>Per-user data folder name under %APPDATA% (or the platform equivalent).</summary>
    public const string DataFolderName = "Noctis Plus";

    /// <summary>Debug builds use their own empty profile; they never copy credentials or live databases.</summary>
    public const string DevDataFolderName = "Noctis Plus-dev";

    /// <summary>No spaces: the single-instance mutex and activation pipe names, user agents.</summary>
    public const string Key = "NoctisPlus";

    /// <summary>The product's own repository: releases (the update feed) and the project page.</summary>
    public const string Repository = "Alfamin/NoctisPlus";

    public const string ProjectUrl = "https://github.com/" + Repository;

    /// <summary>The project Noctis Plus is built on, credited on the About page.</summary>
    public const string UpstreamRepository = "heartached/Noctis";

    public const string UpstreamProjectUrl = "https://github.com/" + UpstreamRepository;

    /// <summary>
    /// The Noctis release this build is based on. Plugins see this version and plugin.json's minAppVersion is checked
    /// against it, because community plugins target Noctis versions: Noctis Plus 2.0.0 is still Noctis 1.6.3 to them.
    /// Change it only when an upstream Noctis release is merged.
    /// </summary>
    public const string NoctisBaseVersion = "1.6.3";

    /// <summary>The credit line on the About page.</summary>
    public const string BasedOn = "Based on Noctis " + NoctisBaseVersion + " by heartached · MIT";

    /// <summary>
    /// Inno Setup AppId (installer.iss uses the same value). Different from official Noctis's, so the two install,
    /// update and uninstall separately.
    /// </summary>
    public const string InstallerAppId = "{46F93B65-9A61-4601-8CF8-365E785E34A0}";

    /// <summary>Windows file-association ProgID and the registry key holding the app's capabilities.</summary>
    public const string ProgId = "NoctisPlus.AudioFile";

    public const string RegistryKey = @"Software\NoctisPlus";
}
