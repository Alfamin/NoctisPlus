using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeMusicFinder;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

/// <summary>
/// Owner #69: a playlist found in Search, shown on this page before it is saved. It reads like an imported
/// playlist (every song in order, owned ones playable, missing ones with Play and download), but nothing is
/// stored and nothing can be edited until Save turns it into a normal Noctis playlist.
/// </summary>
public partial class PlaylistViewModel
{
    private OnlinePlaylist? _preview;
    private IReadOnlyList<ImportedPlaylistEntry>? _previewEntries;
    private CancellationTokenSource? _previewStop;

    /// <summary>A found playlist, not saved: read-only, with Save.</summary>
    public bool IsPreview => _preview is not null;
    public bool IsEditable => !IsPreview;
    public bool CanAddSongs => IsManualPlaylist && IsEditable;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPlayActions))] private bool _isPreviewLoading;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SavePreviewCommand))] private bool _isSaving;

    /// <summary>Who made it, and how many saved it: "Saarujan · 18,335 saves".</summary>
    public string PreviewByline => _preview is not { } p ? "" : string.Join(" · ", new[]
    {
        p.Owner,
        p.Saves is > 0 and var saves ? (saves == 1 ? "1 save" : $"{saves.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} saves") : "",
    }.Where(s => s.Length > 0));

    /// <summary>Saved: the shell opens the new Noctis playlist in this page's place.</summary>
    public event EventHandler<Guid>? PreviewSaved;

    /// <summary>The page for a found playlist; it reads the playlist and matches it to the library by itself.</summary>
    internal static PlaylistViewModel ForPreview(OnlinePlaylist found, PlayerViewModel player, ILibraryService library,
        IPersistenceService persistence, SidebarViewModel sidebar, ArtistImageService? artistImageService = null)
    {
        var vm = new PlaylistViewModel(new Playlist { Name = found.Name }, player, library, persistence, sidebar, artistImageService);
        vm._preview = found;
        foreach (var name in new[] { nameof(IsPreview), nameof(IsEditable), nameof(CanAddSongs), nameof(PreviewByline), nameof(CanReorder), nameof(HasSuggestions) })
            vm.OnPropertyChanged(name);
        _ = vm.LoadPreviewAsync();
        return vm;
    }

    private async Task LoadPreviewAsync()
    {
        var found = _preview!;
        var stop = _previewStop = new CancellationTokenSource();
        IsPreviewLoading = true;
        CatalogNotice = "";
        RefreshCatalogRows();
        _ = LoadPreviewCoverAsync(found, stop.Token);
        try
        {
            var imports = FreeMusicPlugin.Current?.PlaylistImports ?? throw new InvalidOperationException("Turn on Free Music Finder to open online playlists.");
            var (_, entries) = await imports.PreviewAsync(found.Url, stop.Token);
            if (!_streamingDisposed) ApplyPreview(entries);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_streamingDisposed) CatalogNotice = SpotifyPlaylists.Explain(ex); }
        finally { if (!_streamingDisposed) IsPreviewLoading = false; }
    }

    /// <summary>The read playlist: owned songs become this page's tracks in playlist order, the rest its missing rows.</summary>
    internal void ApplyPreview(IReadOnlyList<ImportedPlaylistEntry> entries)
    {
        _playlist.TrackIds = entries.Where(e => e.LocalId is not null).Select(e => e.LocalId!.Value).ToList();
        _previewEntries = entries;
        LoadTracks();
        RefreshMissingDownloads();
    }

    // The playlist's own cover (Spotify's four-cover mosaic or the one its maker chose) as the page's cover.
    private async Task LoadPreviewCoverAsync(OnlinePlaylist found, CancellationToken ct)
    {
        if (found.ArtworkUrl.Length == 0 || OnlineMusicSearch.Current is not { } online) return;
        try
        {
            var path = online.CachedArtwork(found.ArtworkUrl) ?? await online.ArtworkAsync(found.ArtworkUrl, ct);
            if (path is null || _streamingDisposed) return;
            _playlist.CoverArtPath = path;
            foreach (var property in EditedHeaderProperties) OnPropertyChanged(property);
        }
        catch (OperationCanceledException) { }
    }

    private int PreviewMissingCount => _previewEntries?.Count(e => e.LocalId is null && e.Song is not null) ?? 0;

    // Download missing on a found playlist: its missing songs join the download queue (no dialog: nothing is saved yet).
    private async Task DownloadPreviewMissingAsync()
    {
        if (_catalogOnline is not { } online) return;
        if (!online.CanDownload) { online.RequestSignIn(); return; }
        var songs = CatalogRows.OfType<ArtistCatalogSong>().Where(r => r.Song.CanStartDownload && r.Song.Online is not null)
            .Select(r => r.Song.Online!).ToList();
        if (songs.Count == 0) return;
        try { CatalogNotice = await online.DownloadAsync(songs, now: false, CancellationToken.None) is var message && message == "Added to downloads." ? "" : message; }
        catch (Exception ex) { CatalogNotice = ex.Message; }
        RefreshCatalogDownloads();
    }

    private bool CanSavePreview() => IsPreview && !IsSaving;

    /// <summary>Save: the found playlist becomes a normal Noctis playlist (rename it, add or remove songs).</summary>
    [RelayCommand(CanExecute = nameof(CanSavePreview))]
    private async Task SavePreview()
    {
        if (_preview is not { } found) return;
        var imports = FreeMusicPlugin.Current?.PlaylistImports;
        if (imports is null) { CatalogNotice = "Turn on Free Music Finder to save online playlists."; return; }
        IsSaving = true;
        CatalogNotice = "";
        try
        {
            var id = await imports.SaveAsync(found.Url, CancellationToken.None);
            // Owner #88: songs of it that were already played join Songs now that it is one of your playlists (from their
            // cached copies, no new bot request). A failure here never undoes the save.
            var played = CatalogRows.OfType<ArtistCatalogSong>().Where(r => r.Song.IsStreamed && r.Song.Online is not null).Select(r => r.Song.Online!).ToList();
            if (played.Count > 0 && _catalogOnline is { } online)
                try { await online.DownloadAsync(played, now: true, CancellationToken.None); }
                catch (Exception ex) { DebugLog.Write("Playlist", "Played songs of the saved playlist not added: " + ex.Message); }
            if (_playlist.CoverArtPath is { } cover)
                try { await _sidebar.KeepFoundCoverAsync(id, cover); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DebugLog.Write("Playlist", "Found cover not kept: " + ex.GetType().Name); }
            if (!_streamingDisposed) PreviewSaved?.Invoke(this, id);
        }
        catch (Exception ex) { CatalogNotice = SpotifyPlaylists.Explain(ex); }
        finally { IsSaving = false; }
    }

    private void StopPreview()
    {
        _previewStop?.Cancel();
        _previewStop?.Dispose();
        _previewStop = null;
    }
}
