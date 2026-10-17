using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using FreeMusicFinder;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class StreamingPlaylistImportDialog : Window
{
    private StreamingPlaylistImportViewModel? _vm;
    public bool FileImportRequested { get; private set; }
    private bool _openDownloads;
    public StreamingPlaylistImportDialog() => InitializeComponent();

    internal StreamingPlaylistImportDialog(SpotifyPlaylists imports, Func<string> folder, Func<bool> signedIn, Action<Window> signIn) : this()
    {
        DataContext = _vm = new(imports, folder, signedIn, () => signIn(this));
        _vm.DownloadsRequested += () => { _openDownloads = true; Close(); };
        Opened += (_, _) => LinksField.Focus();
        Closed += (_, _) => _vm.Dispose();
    }

    internal static async Task<bool> TryShowAsync(Guid? playlistId = null)
    {
        if (FreeMusicPlugin.Current is not { PlaylistImports: { } imports } feature ||
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not { } owner)
            return false;
        var dialog = new StreamingPlaylistImportDialog(imports, () => feature.DownloadFolder, () => feature.IsSignedIn, feature.ShowSignIn);
        if (playlistId is { } id && dialog._vm is { } vm)
            vm.Selected = vm.Imports.FirstOrDefault(r => r.Job.LibraryId == id) ?? vm.Selected;
        DialogHelper.SizeToOwner(dialog, owner);
        await dialog.ShowDialog(owner);
        if (dialog.FileImportRequested) await MetadataHelper.OpenPlaylistImportDialog();
        if (dialog._openDownloads && owner.DataContext is MainWindowViewModel main) main.NavigateCommand.Execute("downloads");
        return true;
    }

    private void OnFileImport(object? sender, RoutedEventArgs e) { FileImportRequested = true; Close(); }
    private void OnDone(object? sender, RoutedEventArgs e) => Close();
    private async void OnRemoveImport(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { CanInteract: true } vm || sender is not Control { DataContext: StreamingImportRow row }) return;
        try
        {
            if (await ConfirmationDialog.ShowAsync(this, $"Stop managing \"{row.Name}\"? Its Noctis playlist, songs and existing download requests are kept."))
                await vm.RemoveImportCommand.ExecuteAsync(row);
        }
        catch (Exception ex) { vm.Error = SpotifyPlaylists.Explain(ex); }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        base.OnKeyDown(e);
    }
}
