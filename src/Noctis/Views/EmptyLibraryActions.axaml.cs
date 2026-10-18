using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Noctis.Localization;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>Find music + Add music folder on the empty library pages; acts through the main window's view model.</summary>
public partial class EmptyLibraryActions : UserControl
{
    public EmptyLibraryActions() => InitializeComponent();

    private MainWindowViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;

    private void OnFindMusic(object? sender, RoutedEventArgs e) => Main?.OpenSearchPage();

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Main is not { } main || TopLevel.GetTopLevel(this) is not { } top) return;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Loc.T("Home.AddMusicFolder"),
                AllowMultiple = false
            });
            if (folders.Count > 0 && folders[0].Path.LocalPath is { Length: > 0 } path)
                await main.Settings.AddFolderPath(path);
        }
        catch (Exception ex)
        {
            Noctis.Services.DebugLog.Write("Library", $"add folder failed: {ex.Message}");
        }
    }
}
