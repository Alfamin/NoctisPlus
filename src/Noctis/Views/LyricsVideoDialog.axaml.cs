using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>Owner #243: the Lyrics Reel maker (see <see cref="LyricsVideoViewModel"/>).</summary>
public partial class LyricsVideoDialog : Window
{
    public LyricsVideoDialog() => InitializeComponent();

    public LyricsVideoDialog(LyricsVideoViewModel vm) : this()
    {
        DataContext = vm;
        vm.OnPreviewFrame += OnPreviewFrame;
        Opened += (_, _) => vm.StartPreview();
        Closed += (_, _) =>
        {
            vm.OnPreviewFrame -= OnPreviewFrame;
            vm.Dispose();
        };
    }

    private void OnPreviewFrame() => PreviewImage.InvalidateVisual();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LyricsVideoViewModel vm || !vm.CanExport) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Reel",
            SuggestedFileName = vm.SuggestedFileName,
            DefaultExtension = "mp4",
            FileTypeChoices = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await vm.ExportAsync(path);
    }

    public static async Task ShowAsync(LyricsVideoViewModel vm)
    {
        var dialog = new LyricsVideoDialog(vm);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: Window owner })
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }
}
