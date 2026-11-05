using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>The saved playlists (playlists.json), pinned first; searchable and sortable from
/// the header's tools, as a song list is.</summary>
public sealed partial class PlaylistListPageViewModel : MobilePage, ISortable
{
    public PlaylistListPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        _imports = shell.OnlineSearch?.PlaylistImports;
        if (_imports is not null) _imports.Changed += OnImportsChanged;
        SortChoices = new[]
        {
            new SortChoice("Title", PlaylistSort.Title) { IsSelected = true },
            new SortChoice("Recently Updated", PlaylistSort.RecentlyUpdated),
            new SortChoice("Recently Added", PlaylistSort.RecentlyAdded),
        };
        Shell.Library.Refreshed += OnRefreshed;
        Refresh();
    }

    public ShellViewModel Shell { get; }

    public override string Title => Loc.T("Nav.Playlists");

    /// <summary>The playlists the page shows, in order (the search and sort applied).</summary>
    public BulkObservableCollection<Playlist> Playlists { get; } = new();

    /// <summary>The same, as rows: the cover and "1 song" / "12 songs".</summary>
    public BulkObservableCollection<PlaylistRow> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaylists), nameof(HasTools))]
    private int _playlistCount;

    public bool HasPlaylists => PlaylistCount > 0;
    public bool CanImport => _imports is not null;
    public bool HasTools => HasPlaylists || CanImport;
    private readonly Noctis.Services.Search.IOnlinePlaylistImports? _imports;
    private readonly CancellationTokenSource _importStop = new();
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string? _importMessage;

    [RelayCommand]
    private void ImportPlaylists()
    {
        if (_imports is null || IsImporting) return;
        Shell.ShowPrompt(new PromptViewModel(Shell, "Import playlists", "Spotify or Deezer links", [
            new PromptAction("Import", async prompt =>
            {
                IsImporting = true; ImportMessage = null;
                try
                {
                    await _imports.ImportAsync(prompt.Entered, _importStop.Token);
                    if (!_importStop.IsCancellationRequested)
                    {
                        Refresh();
                        var jobs = _imports.Snapshot();
                        ImportMessage = jobs.Any(j => !j.Complete || j.Stopped) ? "Some playlists need attention" : "Playlists imported";
                    }
                }
                catch (OperationCanceledException) when (_importStop.IsCancellationRequested) { }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { if (!_importStop.IsCancellationRequested) ImportMessage = OnlineResultsViewModel.Short(ex); }
                finally { if (!_importStop.IsCancellationRequested) IsImporting = false; }
            }),
        ]) { HasField = true, MultilineField = true, FieldPlaceholder = "Playlist links, one per line" });
    }

    private void OnImportsChanged() => Shell.Marshal(() => { if (!_importStop.IsCancellationRequested) Refresh(); });
    public System.Collections.ObjectModel.ObservableCollection<Noctis.Services.Search.OnlineImportJob> ImportIssues { get; } = new();
    [RelayCommand] private void ShowImportIssue(Noctis.Services.Search.OnlineImportJob? job)
    { if (job is not null) Shell.ShowOptions(OptionsMenuViewModel.ForNote(job.Name, null, job.Status)); }

    /// <summary>The search found nothing (the tools stay up so it can be cleared).</summary>
    public bool ShowNoMatches => HasPlaylists && Playlists.Count == 0;

    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => Refresh();

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchOpen = !IsSearchOpen;
        if (!IsSearchOpen) FilterText = string.Empty;
    }

    public IReadOnlyList<SortChoice> SortChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSorted))]
    private PlaylistSort _sort = PlaylistSort.Title;

    public bool IsSorted => Sort != PlaylistSort.Title;

    public bool HasSortDirection => false;
    public bool IsDescending => false;
    /// <summary>The toolbar's sort button: the sort menu on the glass pop menu, out of the button.</summary>
    [RelayCommand]
    private void ShowSortMenu() => Shell.ShowOptions(OptionsMenuViewModel.ForSort(Shell, this));

    public void ChooseDirection(bool descending) { }

    public void ChooseSort(SortChoice choice)
    {
        foreach (var c in SortChoices) c.IsSelected = ReferenceEquals(c, choice);
        Sort = (PlaylistSort)choice.Key;
        Refresh();
    }

    public void Refresh()
    {
        var imports = _imports?.Snapshot() ?? [];
        StableRows.Update(ImportIssues, imports.Where(j => !j.Complete || j.Stopped).ToList());
        IEnumerable<Playlist> list = Shell.Library.Playlists;
        PlaylistCount = Shell.Library.Playlists.Count;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var match = new MobileSearch.Matcher(FilterText);
            list = list.Where(p => match.Hit(p.Name, SearchText.Normalize(p.Name)));
        }
        var pinnedFirst = list.OrderByDescending(p => p.IsPinned);
        var sorted = Sort switch
        {
            PlaylistSort.RecentlyUpdated => pinnedFirst.ThenByDescending(p => p.ModifiedAt),
            PlaylistSort.RecentlyAdded => pinnedFirst.ThenByDescending(p => p.CreatedAt),
            _ => pinnedFirst.ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        // Only when something changed: a refresh of the same playlists must not Reset the list.
        MobileLibrary.ReplaceIfChanged(Playlists, sorted.ToList());
        var counts = imports.Where(j => j.Complete && !j.Stopped).ToDictionary(j => j.Id, j => j.Total);
        var covers = imports.ToDictionary(j => j.Id, j => j.ArtworkUrl);
        MobileLibrary.ReplaceIfChanged(Rows, Playlists.Select(p => new PlaylistRow(p, p.CoverArtPath ?? Shell.Library.PlaylistArtwork(p),
            counts.TryGetValue(p.Id, out var total) ? total : null, Shell.OnlineSearch, covers.GetValueOrDefault(p.Id) ?? "")).ToList());
        OnPropertyChanged(nameof(ShowNoMatches));
    }

    [RelayCommand]
    private void Open(Playlist? playlist) => Shell.OpenPlaylistCommand.Execute(playlist);

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    public override void OnClosed()
    {
        _importStop.Cancel();
        Shell.Library.Refreshed -= OnRefreshed;
        if (_imports is not null) _imports.Changed -= OnImportsChanged;
    }
}

public enum PlaylistSort { Title, RecentlyUpdated, RecentlyAdded }

/// <summary>A playlist row: its cover (the first of its songs that has one) and its song count.</summary>
public sealed record PlaylistRow(Playlist Playlist, string? ArtworkPath, int? CatalogCount = null,
    Noctis.Services.Search.IOnlineMusicSearch? CoverSource = null, string CoverUrl = "")
{
    public string Name => Playlist.Name;
    public string Subtitle => (CatalogCount ?? Playlist.TrackIds.Count) == 1 ? "1 song" : $"{CatalogCount ?? Playlist.TrackIds.Count} songs";
}
