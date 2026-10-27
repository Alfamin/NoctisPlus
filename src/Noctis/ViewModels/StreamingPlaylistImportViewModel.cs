using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeMusicFinder;

namespace Noctis.ViewModels;

/// <summary>The native playlist import flow uses the saved ordered manifests, not bot playlist links.</summary>
public sealed partial class StreamingPlaylistImportViewModel : ObservableObject, IDisposable
{
    private readonly SpotifyPlaylists _imports;
    private readonly Func<string> _folder;
    private readonly Func<bool> _signedIn;
    private readonly Action _signIn;
    private readonly CancellationTokenSource _stop = new();
    private int _posted;
    private volatile bool _disposed;

    internal StreamingPlaylistImportViewModel(SpotifyPlaylists imports, Func<string> folder, Func<bool> signedIn, Action signIn)
    {
        _imports = imports; _folder = folder; _signedIn = signedIn; _signIn = signIn;
        _imports.Changed += OnChanged;
        Refresh();
    }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ImportCommand))] private string _links = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInteract)), NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(QueueMissingCommand), nameof(CheckLibraryCommand), nameof(RemoveImportCommand))]
    private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private StreamingImportRow? _selected;
    public ObservableCollection<StreamingImportRow> Imports { get; } = [];
    public bool CanInteract => !IsBusy && !_disposed;
    public bool CanImport => CanInteract && Links.Trim().Length > 0;
    public event Action? DownloadsRequested;

    private void OnChanged()
    {
        if (_disposed || Interlocked.Exchange(ref _posted, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Volatile.Write(ref _posted, 0); if (!_disposed) Refresh(); });
    }

    internal void Refresh()
    {
        var jobs = _imports.Jobs;
        var selectedId = Selected?.Job.LibraryId;
        var desired = jobs.Select(j => new StreamingImportRow(j)).ToArray();
        Imports.Clear();
        foreach (var row in desired) Imports.Add(row);
        Selected = Imports.FirstOrDefault(r => r.Job.LibraryId == selectedId) ?? Imports.LastOrDefault();
        Status = jobs.Count > 0 && _imports.Status.StartsWith("Paste ", StringComparison.Ordinal) ? "" : _imports.Status;
    }

    private async Task Act(Func<Task> action)
    {
        if (!CanInteract) return;
        IsBusy = true; Error = "";
        try { await action(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) Error = SpotifyPlaylists.Explain(ex); }
        finally { IsBusy = false; if (!_disposed) Refresh(); }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private Task Import() => Act(() => _imports.ImportAsync(Links, _stop.Token));

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task CheckLibrary() => Act(() => _imports.CheckLibraryAsync(_stop.Token));

    // The view asks first. Removing management keeps the native playlist and audio.
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task RemoveImport(StreamingImportRow? row) => row is null ? Task.CompletedTask : Act(() => _imports.RemoveAsync(row.Job));

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task QueueMissing(StreamingImportRow? row)
    {
        // Each action targets one playlist. The importer deduplicates shared recordings
        // against the whole queue, and keeps unsupported/unavailable entries visible.
        if (row is null || !row.CanDownload || !CanInteract) return Task.CompletedTask;
        if (!_signedIn()) { _signIn(); return Task.CompletedTask; }
        return Act(async () =>
        {
            var queued = await _imports.QueueMissingAsync(_folder(), _stop.Token, row.Job);
            if (!_disposed && queued > 0) DownloadsRequested?.Invoke();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _imports.Changed -= OnChanged;
        _stop.Cancel();
    }
}

public sealed class StreamingImportRow
{
    internal SpotifyPlaylist Job { get; }
    internal StreamingImportRow(SpotifyPlaylist job)
    {
        Job = job;
        Name = job.Manifest?.Name is { Length: > 0 } name ? SpotifyPlaylists.DisplayName(name) : "Reading playlist…";
        Summary = job.Status;
        CanDownload = job.Created && !job.FillingStopped && job.Found < job.Total;
        // Incomplete manifests can still be changing on a worker thread. Show the progress
        // message until the list is complete rather than enumerating an in-flight list.
        Missing = job.Manifest is { CompleteIds: true } m && !job.FillingStopped
            ? m.Entries.Where(e => !job.Matches.ContainsKey(e.Position)).Select(e => new StreamingMissingRow(
                e.Position, e.Request?.Title ?? "Unavailable entry", e.Request?.Artist ?? "",
                e.Request is null ? e.Issue : job.MissingReasons.GetValueOrDefault(e.Position, "No matching local recording"))).ToArray()
            : [];
    }
    public string Name { get; }
    public string Summary { get; }
    public bool CanDownload { get; }
    public IReadOnlyList<StreamingMissingRow> Missing { get; }
}

public sealed record StreamingMissingRow(int Position, string Title, string Artist, string Reason)
{
    /// <summary>Owner #183: the plain "not in your library" reason under every row (all 50 on an empty library) said what
    /// the heading already says; only a reason worth reading (another length, another artist, unavailable) shows.</summary>
    public bool ShowReason => !Reason.StartsWith("No matching local recording", StringComparison.Ordinal);
}
