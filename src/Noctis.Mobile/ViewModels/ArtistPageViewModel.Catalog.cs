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
        foreach (var release in Releases.Take(40).Where(a => a.ArtworkPath is null && a.CatalogReleaseId is { Length: > 0 }))
            if (source.Browser?.KnownAlbum(release.CatalogReleaseId!) is { ArtworkUrl.Length: > 0 } r) _ = LoadReleaseArtAsync(release, r.ArtworkUrl);
        foreach (var row in TopSongs.Where(r => r.Track.PendingStream is Noctis.ViewModels.PendingStream)) _ = LoadSongArtAsync(row.Track);
    }

    private async Task LoadReleaseArtAsync(Album release, string url)
    {
        try
        {
            var path = await Shell.OnlineSearch!.ArtworkAsync(url, _photoCts.Token);
            if (!_photoCts.IsCancellationRequested) release.ArtworkPath = path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private async Task LoadSongArtAsync(Track track)
    {
        if (track.PendingStream is not Noctis.ViewModels.PendingStream pending || pending.Song.ArtworkUrl.Length == 0) return;
        try
        {
            var path = await pending.Source.QueueCoverAsync(pending.Song, _photoCts.Token);
            if (!_photoCts.IsCancellationRequested && path is not null) track.ShowArtwork(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private bool CanPlayCatalog(IEnumerable<Track> tracks)
    {
        if (tracks.Any(t => t.PendingStream is Noctis.ViewModels.PendingStream p && !p.Source.CanDownload &&
            !(p.Source.CompletedPlaybackPath(p.Song) is { } path && File.Exists(path))))
        { Shell.OpenTelegramSignIn(); return false; }
        return true;
    }
}
