using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services.Search;

namespace Noctis.Mobile.ViewModels;

public sealed partial class SongListPageViewModel
{
    private readonly IOnlinePlaylistImports? _playlistImports;
    private readonly CancellationTokenSource _importStop = new();
    private readonly Dictionary<int, (OnlinePlaylistEntry Entry, Track Track)> _importRows = new();
    private OnlineImportedPlaylist? _imported;
    public bool IsImported => _imported is not null;
    public bool HasMissingImported => _imported?.Import.HasMissing == true;
    [ObservableProperty] private bool _isImportBusy;
    [ObservableProperty] private string? _importMessage;
    private static bool Playable(Track track) => !(track.IsFileMissing && track.FilePath.Length == 0);

    private void OnImportedPlaylistChanged() => Shell.Marshal(() => { if (!_importStop.IsCancellationRequested) Refresh(); });

    private List<Track>? ImportedRows()
    {
        _imported = _playlist is null ? null : _playlistImports?.Read(_playlist.Id);
        OnPropertyChanged(nameof(IsImported)); OnPropertyChanged(nameof(HasMissingImported));
        if (_imported is null || Shell.OnlineSearch is not { } source) { _importRows.Clear(); return null; }
        var rows = new List<Track>(_imported.Entries.Count);
        foreach (var entry in _imported.Entries)
        {
            if (entry.Song?.LocalTrackId is { } id && Shell.Library.Service.GetTrackById(id) is { IsFileMissing: false } owned)
            { rows.Add(owned); _importRows.Remove(entry.Position); continue; }
            if (_importRows.TryGetValue(entry.Position, out var saved) && saved.Entry == entry) { rows.Add(saved.Track); continue; }
            var track = entry.Song is { } song ? OnlineStandIn.Create(source, song, false, "playlist:" + _playlist!.Id)
                : new Track
                {
                    Id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"unavailable:{_playlist!.Id}:{entry.Position}"))),
                    Title = "Unavailable song", Artist = entry.Issue, IsFileMissing = true,
                };
            rows.Add(track); _importRows[entry.Position] = (entry, track);
        }
        var positions = _imported.Entries.Select(e => e.Position).ToHashSet();
        foreach (var old in _importRows.Keys.Where(p => !positions.Contains(p)).ToArray()) _importRows.Remove(old);
        return rows;
    }

    [RelayCommand] private async Task DownloadMissingAsync() => await ImportAction(async () =>
    {
        if (Shell.OnlineSearch?.CanDownload != true) { Shell.OpenTelegramSignIn(); return; }
        if (_playlistImports is null || _playlist is null) return;
        var count = await _playlistImports.DownloadMissingAsync(_playlist.Id, _importStop.Token);
        ImportMessage = count > 0 ? $"{count} added to downloads" : "No downloadable songs missing";
    });

    private async Task ImportAction(Func<Task> action)
    {
        if (IsImportBusy || _importStop.IsCancellationRequested) return;
        IsImportBusy = true; ImportMessage = null;
        try { await action(); }
        catch (OperationCanceledException) when (_importStop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_importStop.IsCancellationRequested) ImportMessage = OnlineResultsViewModel.Short(ex); }
        finally { if (!_importStop.IsCancellationRequested) { IsImportBusy = false; Refresh(); } }
    }

    private void ShowImportMenu()
    {
        var items = new List<OptionsMenuItem>
        {
            new(Shell, "Play", false, PlayAll), new(Shell, "Shuffle", false, ShuffleAll),
        };
        if (HasMissingImported) items.Add(new(Shell, "Download missing", false, () => _ = DownloadMissingAsync()));
        items.Add(new(Shell, "Check library", false, () => _ = ImportAction(() => _playlistImports!.CheckLibraryAsync(_importStop.Token))));
        items.Add(new(Shell, "Stop managing import", false, () => Shell.ShowPrompt(new PromptViewModel(Shell,
            "Stop managing import?", "Your playlist and music stay", [new PromptAction("Stop managing", _ => ImportAction(async () =>
            { await _playlistImports!.RemoveAsync(_playlist!.Id, _importStop.Token); }), true)]))));
        items.Add(new(Shell, "Playlist options", false, () => Shell.OpenPlaylistSheetCommand.Execute(Playlist)));
        Shell.ShowOptions(OptionsMenuViewModel.ForItems(items));
    }
}
