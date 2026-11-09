using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Search;

namespace Noctis.Mobile.ViewModels;

public sealed partial class ArtistPageViewModel
{
    private OnlineArtist? _requestedArtist;
    private OnlineArtistPage? _catalogProfile;
    internal Task CatalogLoad { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private bool _isCatalogLoading;
    [ObservableProperty] private string? _catalogMessage;
    [ObservableProperty] private bool _hasCatalogError;

    [RelayCommand] private async Task RetryCatalogAsync() => await (CatalogLoad = LoadArtistCatalogAsync());

    /// <summary>Owner #282: while the catalog loads and no release is listed yet, the Albums and Singles & EPs rails show
    /// pulsing placeholder tiles, so the page never looks empty while something is coming.</summary>
    public bool ShowsReleasePlaceholders => IsCatalogLoading && !HasAlbums && !HasSingles;
    partial void OnIsCatalogLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowsReleasePlaceholders));
    partial void OnAlbumsChanged(IReadOnlyList<ArtistRelease> value) => OnPropertyChanged(nameof(ShowsReleasePlaceholders));
    partial void OnSinglesChanged(IReadOnlyList<ArtistRelease> value) => OnPropertyChanged(nameof(ShowsReleasePlaceholders));

    private async Task LoadArtistCatalogAsync()
    {
        if (Shell.OnlineSearch?.Browser is not { } browser || _photoCts.IsCancellationRequested) return;
        IsCatalogLoading = true; CatalogMessage = null; HasCatalogError = false;
        try
        {
            var page = _requestedArtist is { } artist ? await browser.ReadArtistAsync(artist, _photoCts.Token)
                : await browser.FindLibraryArtistAsync(Name, Releases.Select(a => a.Name).ToArray(), Songs.Select(t => t.Title).ToArray(), _photoCts.Token);
            if (page is null || _photoCts.IsCancellationRequested) return;
            _catalogProfile = page; _requestedArtist = page.Artist;
            ApplyArtistCatalog();
            CatalogMessage = page.Warning.Length > 0 ? page.Warning : null;
            if (page.Artist.PictureUrl.Length > 0)
            {
                var photo = Shell.OnlineSearch.CachedArtwork(page.Artist.PictureUrl)
                    ?? await Shell.OnlineSearch.ArtworkAsync(page.Artist.PictureUrl, _photoCts.Token);
                if (!_photoCts.IsCancellationRequested && photo is not null) { _photo = photo; ApplyHero(); }
            }
        }
        catch (OperationCanceledException) when (_photoCts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_photoCts.IsCancellationRequested)
            { CatalogMessage = ex is OfflineModeException ? "Offline mode" : "Couldn't load catalog"; HasCatalogError = true; }
            DebugLog.Write("Artist", "Catalog unavailable: " + ex.GetType().Name);
        }
        finally { if (!_photoCts.IsCancellationRequested) IsCatalogLoading = false; }
    }

    private void ApplyArtistCatalog()
    {
        if (_catalogProfile is not { } page || Shell.OnlineSearch is not { } source) return;
        var ownedReleases = Shell.Library.Service.Albums.Where(a => string.Equals(a.Artist, Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        var online = page.Releases.Select(r =>
        {
            var display = AlbumPageViewModel.OnlinePlaceholder(r, source);
            var local = ownedReleases.FirstOrDefault(a => string.Equals(a.Name, r.Title, StringComparison.OrdinalIgnoreCase));
            if (local is not null) { display.Tracks = local.Tracks; display.ArtworkPath ??= local.ArtworkPath; }
            return display;
        }).ToList();
        Releases = online.Concat(ownedReleases.Where(a => !online.Any(r => string.Equals(r.Name, a.Name, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(MobileLibrary.ReleaseSortDate).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Albums = Releases.Where(a => !IsSingleOrEp(a)).Select(ArtistRelease.Own).ToList();
        Singles = Releases.Where(IsSingleOrEp).Select(ArtistRelease.Own).ToList();
        Cards = BuildCards(Releases, _plays);
        if (page.Songs.Count > 0)
        {
            Songs = page.Songs.Select(song => song.LocalTrackId is { } id && Shell.Library.Service.GetTrackById(id) is { IsFileMissing: false } local
                ? local : OnlineStandIn.Create(source, song, false, Name)).ToArray();
            var ranked = Songs.Take(TopSongCount).ToList();
            TopSongs = ranked.Select((t, i) => new ArtistTopSong(t, i + 1, i % TopSongRowsPerPage != TopSongRowsPerPage - 1 && i != ranked.Count - 1)).ToList();
            TopSongPages = TopSongs.Chunk(TopSongRowsPerPage).Select(rows => new ArtistTopSongPage(rows)).ToList();
            SyncNowPlaying();
        }
        // Owner #282: every release's cover, in the order shown (a prolific artist's past the first 40 never loaded), from
        // the release's own address at row size; one already on disk shows at once.
        var urls = page.Releases.Where(r => r.ArtworkUrl.Length > 0).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().ArtworkUrl);
        foreach (var release in Releases.Where(a => a.ArtworkPath is null && a.CatalogReleaseId is { Length: > 0 }))
        {
            var url = urls.GetValueOrDefault(release.CatalogReleaseId!) ?? source.Browser?.KnownAlbum(release.CatalogReleaseId!)?.ArtworkUrl;
            if (url is not { Length: > 0 }) continue;
            if (source.CachedRowArtwork(url) is { } cached) { release.ArtworkPath = cached; continue; }
            _ = LoadReleaseArtAsync(release, url);
        }
        foreach (var row in TopSongs.Where(r => r.Track.PendingStream is Noctis.ViewModels.PendingStream)) _ = LoadSongArtAsync(row.Track);
    }

    private async Task LoadReleaseArtAsync(Album release, string url)
    {
        try
        {
            var path = await Shell.OnlineSearch!.RowArtworkAsync(url, _photoCts.Token);
            if (_photoCts.IsCancellationRequested || path is null) return;
            release.ArtworkPath = path;
            ShareAlbumCover(release.CatalogReleaseId, release.Name, path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private async Task LoadSongArtAsync(Track track)
    {
        if (track.PendingStream is not Noctis.ViewModels.PendingStream pending || pending.Song.ArtworkUrl.Length == 0) return;
        // Owner #282: one cover per album; a song whose album's cover is here already takes it, no second fetch.
        if (AlbumCoverFor(pending.Song) is { } shared) { track.ShowArtwork(shared); return; }
        try
        {
            var path = await pending.Source.QueueCoverAsync(pending.Song, _photoCts.Token);
            if (!_photoCts.IsCancellationRequested && path is not null && track.AlbumArtworkPath is null) track.ShowArtwork(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>Owner #282: the cover of a release on this page (or a library album of the artist), for a song of it.</summary>
    private string? AlbumCoverFor(OnlineSong song)
        => Releases.FirstOrDefault(r => r.ArtworkPath is not null && (song.AlbumId.Length > 0 && r.CatalogReleaseId == song.AlbumId
               || song.Album.Length > 0 && string.Equals(r.Name, song.Album, StringComparison.OrdinalIgnoreCase)))?.ArtworkPath;

    /// <summary>A release's cover just arrived: the page's songs of that album show it too (no fetch of their own).</summary>
    private void ShareAlbumCover(string? releaseId, string name, string path)
    {
        foreach (var row in TopSongs)
            if (row.Track.PendingStream is Noctis.ViewModels.PendingStream p && row.Track.AlbumArtworkPath is null
                && (releaseId is { Length: > 0 } && p.Song.AlbumId == releaseId || string.Equals(p.Song.Album, name, StringComparison.OrdinalIgnoreCase)))
                row.Track.ShowArtwork(path);
    }
    private bool CanPlayCatalog(IEnumerable<Track> tracks)
    {
        if (tracks.Any(t => t.PendingStream is Noctis.ViewModels.PendingStream p && !p.Source.CanDownload &&
            !(p.Source.CompletedPlaybackPath(p.Song) is { } path && File.Exists(path))))
        { Shell.OpenTelegramSignIn(); return false; }
        return true;
    }
}
