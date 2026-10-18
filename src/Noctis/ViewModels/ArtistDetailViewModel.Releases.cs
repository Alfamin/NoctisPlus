using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeMusicFinder;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// Noctis Plus: the artist's online releases on the page itself. With Free Music Finder on,
/// the page asks the catalog who this artist is (one choice when several share the name),
/// loads their releases and shows the ones the library lacks in its own grids, wearing a
/// download badge. Clicking one opens the album page with its songs. The releases are
/// display-only Album objects: never added to the library, and every play/shuffle command
/// still reads only <see cref="_allReleases"/> (local music).
/// </summary>
public partial class ArtistDetailViewModel
{
    private List<Album> _catalogReleases = [];
    private List<Album> _catalogAppearsOn = [];
    private readonly Dictionary<string, Album> _catalogAlbums = new(StringComparer.Ordinal);
    private readonly Dictionary<Album, CatalogRelease> _catalogReleaseOf = new(ReferenceEqualityComparer.Instance);
    private CatalogProfile? _catalogProfile;
    private CatalogArtist? _requestedIdentity;
    private CancellationTokenSource? _releasesStop;

    /// <summary>A line under the header while the catalog loads or when it could not; empty when all is well.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCatalogStatus)), NotifyPropertyChangedFor(nameof(IsCatalogLoading))]
    private string _catalogStatus = "";
    public bool HasCatalogStatus => CatalogStatus.Length > 0;
    /// <summary>Shows the spinner beside "Loading releases…" (owner, 2026-10-18: the text alone was too easy to miss).</summary>
    public bool IsCatalogLoading => CatalogStatus.StartsWith("Loading", StringComparison.Ordinal);

    /// <summary>The catalog could not be read: the status line offers Retry.</summary>
    [ObservableProperty] private bool _catalogFailed;

    /// <summary>Several catalog artists share this name: the user picks one once (remembered).</summary>
    public ObservableCollection<CatalogArtistChoice> CatalogChoices { get; } = [];
    public bool HasCatalogChoices => CatalogChoices.Count > 0;

    /// <summary>All releases on show: the library's and the catalog's (display only).</summary>
    private IEnumerable<Album> DisplayReleases => _catalogReleases.Count == 0 ? _allReleases : _allReleases.Concat(_catalogReleases);
    private IEnumerable<Album> DisplayAppearsOn => _catalogAppearsOn.Count == 0 ? _allAppearsOn : _allAppearsOn.Concat(_catalogAppearsOn);

    /// <summary>Search, For you or a similar artist opened this page for one catalog identity.</summary>
    internal void UseCatalogIdentity(CatalogArtist artist)
    {
        _requestedIdentity = artist;
        StartCatalog();
    }

    /// <summary>Loads the online releases (cached first, then fresh). Safe to call again.</summary>
    internal void StartCatalog(bool refresh = false)
    {
        if (_cts.IsCancellationRequested) return;
        if (FreeMusicPlugin.Current?.CatalogPages is not { Enabled: true } pages) { ClearCatalogReleases(); return; }
        _releasesStop?.Cancel(); _releasesStop?.Dispose();
        var stop = _releasesStop = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = LoadCatalogReleasesAsync(pages, refresh, stop.Token);
    }

    [RelayCommand] private void RefreshCatalog() => StartCatalog(refresh: true);

    [RelayCommand]
    private void ChooseCatalogArtist(CatalogArtistChoice? choice)
    {
        if (choice is null || FreeMusicPlugin.Current?.CatalogPages is not { } pages) return;
        pages.Choose(ArtistName, choice.Artist);
        CatalogChoices.Clear(); OnPropertyChanged(nameof(HasCatalogChoices));
        UseCatalogIdentity(choice.Artist);
    }

    private async Task LoadCatalogReleasesAsync(CatalogPages pages, bool refresh, CancellationToken ct)
    {
        CatalogFailed = false;
        CatalogStatus = _catalogProfile is null ? "Loading releases…" : "";
        try
        {
            var catalog = pages.Catalog;
            var identity = _requestedIdentity is { } requested
                ? await catalog.SeparateNamesakesAsync(requested, IdentityAlbums, IdentityTitles, ct)
                : null;
            if (identity is null)
            {
                var (artist, candidates) = await pages.IdentifyAsync(ArtistName, IdentityAlbums, IdentityTitles, ct);
                if (artist is null)
                {
                    CatalogChoices.Clear();
                    foreach (var candidate in candidates) CatalogChoices.Add(new(candidate));
                    OnPropertyChanged(nameof(HasCatalogChoices));
                    CatalogStatus = candidates.Count > 0 ? "Several artists share this name. Which one is this?" : "";
                    return;
                }
                identity = artist;
            }
            ct.ThrowIfCancellationRequested();
            SetCatalogArtist(catalog, identity, refresh);
            if (!refresh && await catalog.SavedArtistAsync(identity, ct) is { } saved) ApplyCatalogProfile(saved.Value, catalog);
            var fresh = await catalog.ArtistAsync(identity, refresh, ct);
            ct.ThrowIfCancellationRequested();
            ApplyCatalogProfile(fresh.Value, catalog);
            CatalogStatus = "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            CatalogFailed = true;
            CatalogStatus = "Online releases unavailable: " + ArtistCatalog.Explain(ex);
        }
    }

    /// <summary>Releases the library lacks become catalog tiles (same object per release across
    /// refreshes, so tiles keep their covers); owned editions keep their library tile.</summary>
    internal void ApplyCatalogProfile(CatalogProfile profile, ArtistCatalog catalog)
    {
        if (_cts.IsCancellationRequested) return;
        _catalogProfile = profile;
        var owned = _allReleases.Concat(_allAppearsOn)
            .Select(a => (CatalogEditions.Key(a.Name), BotText.Key(a.Artist))).ToHashSet();
        var mine = new List<Album>(); var featured = new List<Album>();
        _catalogReleaseOf.Clear();
        foreach (var release in profile.Releases)
        {
            if (owned.Contains((CatalogEditions.Key(release.Title), BotText.Key(release.Artist.Name)))) continue;
            if (!_catalogAlbums.TryGetValue(release.Id, out var album))
                _catalogAlbums[release.Id] = album = CatalogAlbumFor(release);
            // The sharpest copy already on disk: the album page's large cover once it was opened (owner
            // 10-18: Back still showed the blurry one), then a tile-size copy, then the catalog's small one.
            if (BestSavedCover(catalog, release.Artwork) is { } best) album.ArtworkPath = best;
            _catalogReleaseOf[album] = release;
            (release.Artist.Id == profile.Artist.Id ? mine : featured).Add(album);
        }
        _catalogReleases = mine;
        _catalogAppearsOn = featured;
        // The newest release (online or not) leads the hero.
        var newest = DisplayReleases.OrderByDescending(a => a.Year).ThenBy(a => a.CatalogReleaseId is null ? 0 : 1).FirstOrDefault();
        if (newest is not null && (LatestRelease is null || newest.Year > LatestRelease.Year)) LatestRelease = newest;
        ApplyLists();
        _ = LoadCatalogCoversAsync(catalog, _releasesStop?.Token ?? _cts.Token);
        _ = FillLatestReleaseCountAsync(catalog, _releasesStop?.Token ?? _cts.Token);
    }

    /// <summary>The catalog's artist album list carries no track counts, so the hero's online latest
    /// release fetches its own tracklist once (the album page's cached lookup) for "25 songs · 1:02:10".</summary>
    private async Task FillLatestReleaseCountAsync(ArtistCatalog catalog, CancellationToken ct)
    {
        if (LatestRelease is not { TrackCount: <= 0 } album || !_catalogReleaseOf.TryGetValue(album, out var release)) return;
        try
        {
            var answer = await catalog.AlbumAsync(release, false, ct);
            if (ct.IsCancellationRequested || answer.Value.Songs.Count == 0) return;
            var songs = answer.Value.Songs;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested) return;
                album.TrackCount = songs.Count;
                if (album.TotalDuration <= TimeSpan.Zero)
                    album.TotalDuration = TimeSpan.FromSeconds(songs.Sum(s => s.Seconds ?? 0));
                if (ReferenceEquals(LatestRelease, album)) OnPropertyChanged(nameof(LatestReleaseSongsLine));
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Noctis.Services.DebugLog.Write("Artist", $"latest release count unavailable: {ex.Message}");
        }
        catch (OperationCanceledException) { }
    }

    private static Album CatalogAlbumFor(CatalogRelease release) => new()
    {
        Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("FreeMusicFinder/catalog/" + release.Id)).AsSpan(0, 16)),
        Name = release.Title,
        Artist = release.Artist.Name,
        Year = int.TryParse(release.Date.Split('-')[0], out var year) ? year : 0,
        TrackCount = release.Count,
        CatalogReleaseId = release.Id,
        CatalogReleaseType = release.Kind switch
        {
            "single" => ReleaseType.Single,
            "ep" => ReleaseType.EP,
            "compile" or "compilation" => ReleaseType.Compilation,
            "live" => ReleaseType.Live,
            _ => ReleaseType.Album,
        },
    };

    /// <summary>Covers for the catalog tiles not on disk yet, newest first, a few at a time
    /// (the catalog's own artwork gate limits the downloads).</summary>
    private async Task LoadCatalogCoversAsync(ArtistCatalog catalog, CancellationToken ct)
    {
        // Pass 1: the catalog's small cover where a tile has none, so every tile fills fast.
        var missing = _catalogReleaseOf.Where(p => p.Key.ArtworkPath is null && p.Value.Artwork.Length > 0)
            .OrderByDescending(p => p.Key.Year).ToList();
        await FetchCoversAsync(missing, pair => pair.Value.Artwork, catalog, ct, replace: false);
        // Pass 2: a tile-size copy (sharp at 2x) replaces the small one — the small cover alone looked
        // blurry however long the page stayed open (owner 10-18).
        var blurry = _catalogReleaseOf.Where(p => p.Value.Artwork.Length > 0 && SharpSavedCover(catalog, p.Value.Artwork) is null)
            .OrderByDescending(p => p.Key.Year).ToList();
        await FetchCoversAsync(blurry, pair => CatalogArtwork.SizedUrl(pair.Value.Artwork, TileArtwork) ?? pair.Value.Artwork, catalog, ct, replace: true);
    }

    private static async Task FetchCoversAsync(List<KeyValuePair<Album, CatalogRelease>> covers, Func<KeyValuePair<Album, CatalogRelease>, string> url,
        ArtistCatalog catalog, CancellationToken ct, bool replace)
    {
        foreach (var batch in covers.Chunk(6))
        {
            if (ct.IsCancellationRequested) return;
            await Task.WhenAll(batch.Select(async pair =>
            {
                try
                {
                    var path = await catalog.ArtworkAsync(url(pair), ct);
                    if (path is not null && !ct.IsCancellationRequested)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (replace) pair.Key.ArtworkPath = path; else pair.Key.ArtworkPath ??= path;
                        });
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or TimeoutException or InvalidDataException) { }
            }));
        }
    }

    /// <summary>Release tiles are 190-216 px: 600 px stays sharp at 2x without a 1000 px cover per release.</summary>
    private const int TileArtwork = 600;

    private static string? SharpSavedCover(ArtistCatalog catalog, string artwork) =>
        catalog.SavedArtwork(CatalogArtwork.LargeUrl(artwork), highQuality: true)
        ?? catalog.SavedArtwork(CatalogArtwork.SizedUrl(artwork, TileArtwork));

    private static string? BestSavedCover(ArtistCatalog catalog, string artwork) =>
        SharpSavedCover(catalog, artwork) ?? catalog.SavedArtwork(artwork);

    /// <summary>A catalog tile was clicked: its songs on the album page (metadata only).</summary>
    private async Task OpenCatalogAlbumAsync(Album album)
    {
        if (!_catalogReleaseOf.TryGetValue(album, out var release) || FreeMusicPlugin.Current?.CatalogPages is not { } pages) return;
        try
        {
            CatalogStatus = "Opening " + release.Title + "…";
            await pages.OpenAlbumAsync(release, _cts.Token);
            CatalogStatus = "";
        }
        catch (OperationCanceledException) { CatalogStatus = ""; }
        catch (Exception ex) { CatalogFailed = true; CatalogStatus = ArtistCatalog.Explain(ex); }
    }

    private void ClearCatalogReleases()
    {
        _releasesStop?.Cancel();
        if (_catalogReleases.Count == 0 && _catalogAppearsOn.Count == 0) return;
        _catalogReleases = []; _catalogAppearsOn = []; _catalogReleaseOf.Clear(); _catalogProfile = null;
        ApplyLists();
    }
}

/// <summary>One of several same-name catalog artists the user can pick.</summary>
public sealed class CatalogArtistChoice
{
    internal CatalogArtistChoice(CatalogArtist artist) => Artist = artist;
    internal CatalogArtist Artist { get; }
    public string Label => Artist.Genre.Length > 0 ? Artist.Name + " · " + Artist.Genre : Artist.Name;
}
