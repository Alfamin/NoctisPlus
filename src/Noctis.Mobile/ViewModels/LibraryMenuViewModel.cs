using CommunityToolkit.Mvvm.Input;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The Library tab's ⋯ menu (Apple's Library ⋯, the desktop's three library actions): Add
/// Folder…, Rescan Library and Settings, on the same glass pop menu as every other ⋯. Each row
/// closes the menu before it acts.
/// </summary>
public sealed partial class LibraryMenuViewModel(ShellViewModel shell)
{
    [RelayCommand]
    private void AddFolder()
    {
        shell.CloseSheet();
        shell.Library.AddFolderCommand.Execute(null);
    }

    [RelayCommand]
    private void Rescan()
    {
        shell.CloseSheet();
        shell.Library.RescanCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        shell.CloseSheet();
        shell.OpenSettingsCommand.Execute(null);
    }
}
