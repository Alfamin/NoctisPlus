using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Search;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

public sealed partial class AlbumPageViewModel
{
    private readonly CancellationTokenSource _catalogStop = new();
    private readonly OnlineAlbum? _requestedRelease;
    private OnlineAlbumPage? _catalogPage;
    private bool _catalogClosed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCatalogStatus))] private bool _isCatalogLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCatalogStatus))] private string? _catalogMessage;
    [ObservableProperty] private bool _hasCatalogError;
    public bool HasCatalogStatus => IsCatalogLoading || !string.IsNullOrEmpty(CatalogMessage);
    public bool ShowCatalogSkeleton => IsCatalogLoading && Tracks.Count == 0;
    internal Task CatalogLoad { get; private set; } = Task.CompletedTask;
    internal Task CatalogArtworkLoad { get; private set; } = Task.CompletedTask;
    public IReadOnlyList<int> SkeletonRows { get; } = [0, 1, 2, 3, 4, 5];
    partial void OnIsCatalogLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowCatalogSkeleton));

    public static Album OnlinePlaceholder(OnlineAlbum release, IOnlineMusicSearch source) => new()
    {
        Id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("album:" + release.Id))),
        Name = release.Title, Artist = release.ArtistName, TrackCount = release.TrackCount,
        Year = int.TryParse(release.Year, out var year) ? year : 0,
        CatalogReleaseId = release.Id, CatalogReleaseType = release.Kind switch
        { "single" => ReleaseType.Single, "ep" => ReleaseType.EP, _ => ReleaseType.Album },
        ArtworkPath = source.CachedArtwork(release.ArtworkUrl),
    };

    private async Task LoadCatalogAsync()
    {
        if (Shell.OnlineSearch?.Browser is not { } browser || _catalogClosed) return;
        IsCatalogLoading = true; CatalogMessage = null; HasCatalogError = false;
        try
        {
            var release = _catalogPage?.Release ?? _requestedRelease;
            var page = release is not null ? await browser.ReadAlbumAsync(release, _catalogStop.Token)
                : await browser.FindLibraryAlbumAsync(Album.Artist, Album.Name, Album.Tracks.Select(t => t.Title).ToArray(), _catalogStop.Token);
            if (_catalogClosed || _catalogStop.IsCancellationRequested || page is null) return;
            _catalogPage = page;
            ApplyCatalog(page);
            CatalogArtworkLoad = LoadCatalogArtworkAsync(page.Release);
        }
        catch (OperationCanceledException) when (_catalogStop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_catalogClosed) { CatalogMessage = ex is OfflineModeException ? "Offline mode" : "Couldn't load songs"; HasCatalogError = true; }
            DebugLog.Write("Album", "Catalog unavailable: " + ex.GetType().Name);
        }
        finally { if (!_catalogClosed) IsCatalogLoading = false; }
    }

    private void ApplyCatalog(OnlineAlbumPage page)
    {
        var source = Shell.OnlineSearch!;
        var rows = page.Songs.Select((entry, index) =>
        {
            var owned = entry.Song.LocalTrackId is { } id ? Shell.Library.Service.GetTrackById(id) : null;
            var track = owned is { IsFileMissing: false } ? owned : OnlineStandIn.Create(source, entry.Song, false, page.Release.Title);
            // Owner #290: a song of this album shows the album's cover (it showed none once tapped).
            if (track.PendingStream is not null && Album.ArtworkPath is { } cover) track.ShowArtwork(cover);
            return new AlbumTrackRow(track, entry.Number > 0 ? entry.Number : index + 1, entry.Disc);
        }).ToList();
        var display = OnlinePlaceholder(page.Release, source);
        display.Id = AlbumId;
        display.ArtworkPath = Album.ArtworkPath ?? display.ArtworkPath;
        // Catalog albums stay display-only: their Core Tracks collection holds owned files only.
        // The native page's separate rows carry pending streams for playback, never library imports.
        display.Tracks = rows.Where(r => !r.IsRemote).Select(r => r.Track).ToList();
        display.TrackCount = rows.Count;
        display.TotalDuration = TimeSpan.FromSeconds(page.Songs.Sum(s => s.Song.Seconds is > 0 and <= 86400 ? s.Song.Seconds.Value : 0));
        Apply(display);
        Tracks = rows;
        OnPropertyChanged(nameof(ShowCatalogSkeleton));
        FilterRows(); SyncNowPlaying();
        Selection.Prune();
        if (!page.IsComplete)
        {
            SongsLine = $"{rows.Count} of {page.Release.TrackCount} songs";
            CatalogMessage = "Some songs unavailable";
        }
        if (DateTime.TryParse(page.ReleaseDate, out var date)) ReleaseDateText = date.ToString("MMMM d, yyyy");
    }

    private async Task LoadCatalogArtworkAsync(OnlineAlbum release)
    {
        if (release.ArtworkUrl.Length == 0 || Shell.OnlineSearch is not { } source) return;
        try
        {
            var small = source.CachedArtwork(release.ArtworkUrl) ?? await source.ArtworkAsync(release.ArtworkUrl, _catalogStop.Token);
            if (!_catalogClosed && small is not null) SetArt(small);
            var sharp = await source.SharpCoverAsync(new(release.Id, release.Title, release.ArtistName, ArtworkUrl: release.ArtworkUrl), _catalogStop.Token);
            if (!_catalogClosed && sharp is not null) SetArt(sharp);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { DebugLog.Write("Album", "Cover unavailable: " + ex.GetType().Name); }
        void SetArt(string path)
        {
            Album.ArtworkPath = path; OnPropertyChanged(nameof(HasArtwork)); Tint.Load(path);
            // Owner #290: one cover per album: the album's songs you don't have take it too (no fetch of their own).
            foreach (var row in Tracks) if (row.Track.PendingStream is not null) row.Track.ShowArtwork(path);
        }
    }

    [RelayCommand] private void RetryCatalog() { if (!IsCatalogLoading && !_catalogClosed) CatalogLoad = LoadCatalogAsync(); }

    private bool CanPlayCatalog(IEnumerable<Track> tracks)
    {
        if (tracks.Any(t => t.PendingStream is Noctis.ViewModels.PendingStream p && !p.Source.CanDownload &&
            !(p.Source.CompletedPlaybackPath(p.Song) is { } path && File.Exists(path))))
        { Shell.OpenTelegramSignIn(); return false; }
        return true;
    }

    [RelayCommand]
    private void ShowTrackMenu(AlbumTrackRow? row)
    {
        if (row is null) return;
        if (!row.IsRemote) { Shell.OpenTrackSheetCommand.Execute(row.Track); return; }
        Shell.ShowOptions(OptionsMenuViewModel.ForItems([
            new(Shell, "Play", false, () => PlayFrom(Tracks.ToList().IndexOf(row))),
            new(Shell, "Download now", false, () => _ = DownloadCatalogAsync([row], true)),
            new(Shell, "Add to downloads", false, () => _ = DownloadCatalogAsync([row], false)),
        ]));
    }

    private void ShowCatalogAlbumMenu()
    {
        var items = new List<OptionsMenuItem>
        {
            new(Shell, "Play", false, () => PlayFrom(0)),
            new(Shell, "Shuffle", false, Shuffle),
            new(Shell, Loc.T("Mobile.Select"), false, () => Selection.Begin()),
        };
        if (_catalogPage?.IsComplete == true && Tracks.Any(r => r.IsRemote))
        {
            items.Add(new(Shell, "Download missing", false, () => _ = DownloadCatalogAsync(Tracks, true)));
            items.Add(new(Shell, "Add missing to downloads", false, () => _ = DownloadCatalogAsync(Tracks, false)));
        }
        if (Album.Tracks.Count > 0) items.Add(new(Shell, "Album options", false, () => Shell.OpenAlbumMenu(Album, Selection)));
        Shell.ShowOptions(OptionsMenuViewModel.ForItems(items));
    }

    private async Task DownloadCatalogAsync(IEnumerable<AlbumTrackRow> rows, bool now)
    {
        if (Shell.OnlineSearch is not { } source) return;
        var songs = rows.Select(r => r.Track.PendingStream).OfType<Noctis.ViewModels.PendingStream>().Select(p => p.Song).ToArray();
        if (songs.Length == 0) return;
        if (!source.CanDownload) { Shell.OpenTelegramSignIn(); return; }
        try { CatalogMessage = await source.DownloadAsync(songs, now, _catalogStop.Token); }
        catch (OperationCanceledException) when (_catalogStop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_catalogClosed) CatalogMessage = OnlineResultsViewModel.Short(ex); }
    }
}
