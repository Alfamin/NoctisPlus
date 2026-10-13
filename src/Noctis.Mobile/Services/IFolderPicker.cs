namespace Noctis.Mobile.Services;

/// <summary>
/// Lets the user choose a music folder. Android implements it over the Storage Access
/// Framework and returns the persisted tree URI; the string is opaque to the ViewModels,
/// which only store it in AppSettings.MusicFolders and hand it to the scan.
/// Returns null when the user cancels.
/// </summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync();

    /// <summary>Whether the app can still read <paramref name="folder"/> (Android: its persisted
    /// grant still exists). Picking the folder again restores a lost one.</summary>
    bool HasAccess(string folder) => true;

    /// <summary>Gives up the app's access to <paramref name="folder"/> (Android: releases its
    /// persisted grant, of which an app has a limited number). Called when a folder is removed,
    /// or a pick is refused.</summary>
    void ReleaseAccess(string folder) { }
}
