using System.Text.Json;
using System.Text.RegularExpressions;
using Noctis.Services;

namespace FreeMusicFinder;

/// <summary>An online album the app should show on its own album page: the release, its songs,
/// and which of them the library already has.</summary>
internal sealed record CatalogAlbumOpen(CatalogAlbum Album, IReadOnlyDictionary<string, DiscoveryMatch> Matches);

/// <summary>
/// The catalog behind the app's artist and album pages (Noctis Plus, built in). Typed calls
/// only: the pages ask for an artist's identity, profile and albums and render them with their
/// own templates. It holds no controls, scans no windows and touches no private fields; the
/// earlier plugin-era bridge did all three. Catalog albums are never added to the library,
/// persisted, scanned or played.
/// </summary>
internal sealed class CatalogPages : IDisposable
{
    private readonly Func<ArtistCatalog> _catalog;
    private readonly Func<bool> _enabled;
    private readonly Func<ILibraryService?> _library;
    private readonly Action<string> _log;
    private readonly string? _choicesPath;
    private readonly Dictionary<string, CatalogArtist> _chosen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _manual = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Choice(string Id, string Name, string Genre = "", bool Manual = false);

    /// <summary>Search, For you or a similar artist asks for an artist page with this identity.</summary>
    internal static event Action<CatalogArtist>? ArtistRequested;

    /// <summary>An online album should open on the app's album page.</summary>
    internal static event Action<CatalogAlbumOpen>? AlbumRequested;

    /// <summary>An online album was clicked: its page can open now, before its songs are read.</summary>
    internal static event Action<CatalogRelease>? AlbumOpening;

    /// <summary>Reading the album failed (empty message: cancelled by a newer click).</summary>
    internal static event Action<CatalogRelease, string>? AlbumOpenFailed;

    public CatalogPages(Func<ArtistCatalog> catalog, DiscoveryActions actions, Action<string> log, Func<bool> enabled,
        Func<ILibraryService?>? library = null, string? choicesPath = null)
    {
        _catalog = catalog; Actions = actions; _log = log; _enabled = enabled; _choicesPath = choicesPath;
        _library = library ?? (() => Noctis.App.Services?.GetService(typeof(ILibraryService)) as ILibraryService);
        try
        {
            if (choicesPath is not null && File.Exists(choicesPath) && new FileInfo(choicesPath).Length < 256 * 1024 && (File.GetAttributes(choicesPath) & FileAttributes.ReparsePoint) == 0)
                foreach (var (name, artist) in JsonSerializer.Deserialize<Dictionary<string, Choice>>(File.ReadAllText(choicesPath)) ?? [])
                    if (name.Length <= 200 && artist is { Name.Length: > 0 and <= 1000, Id: not null } && Regex.IsMatch(artist.Id, @"^(apple|deezer):[1-9][0-9]{0,17}$"))
                    { _chosen[name] = new(artist.Id, artist.Name, artist.Genre ?? ""); if (artist.Manual) _manual.Add(name); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _log("CATALOG_CHOICES_UNREADABLE: saved artist choices could not be read; you can choose them again."); }
    }

    public ArtistCatalog Catalog => _catalog();
    public DiscoveryActions Actions { get; }

    /// <summary>Settings › Free Music Finder › "Show the online catalog on artist pages".</summary>
    public bool Enabled => _enabled();

    /// <summary>The library's album names for an artist, most complete first (identity evidence).</summary>
    internal IReadOnlyList<string> LocalAlbums(string name)
    {
        if (_library() is not { } library) return [];
        var key = BotText.Key(name);
        return library.Albums.Where(a => BotText.Key(a.Artist) == key)
            .OrderByDescending(a => a.Tracks.Count).Select(a => a.Name).ToArray();
    }

    /// <summary>Which catalog artist a page's name means. Among several namesakes the evidence (albums, a manual choice)
    /// decides, else the most likely one opens. Null with candidates only when no catalog artist has this exact name.</summary>
    internal async Task<(CatalogArtist? Artist, IReadOnlyList<CatalogArtist> Candidates)> IdentifyAsync(
        string name, IReadOnlyList<string> albums, IReadOnlyList<string> titles, CancellationToken ct)
    {
        _chosen.TryGetValue(name, out var saved);
        if (saved is not null && _manual.Contains(name)) return (saved, []);
        var catalog = _catalog();
        var result = await catalog.IdentifyAsync(name, albums.Count > 0 ? albums : LocalAlbums(name), saved, ct);
        if (result.Artist is { } matched)
        {
            var separated = await catalog.SeparateNamesakesAsync(matched, albums, titles, ct);
            Save(name, separated);
            return (separated, []);
        }
        if (titles.Count >= 2 && result.Candidates.FirstOrDefault(a => a.Id.StartsWith("deezer:") && BotText.Key(a.Name) == BotText.Key(name)) is { } candidate)
        {
            var verified = await catalog.SeparateNamesakesAsync(candidate, albums, titles, ct);
            if (verified.Id.StartsWith("apple:")) { Save(name, verified); return (verified, []); }
        }
        if (await catalog.PopularNamesakeAsync(name, result.Candidates, ct).ConfigureAwait(false) is { } popular)
        {
            var separated = await catalog.SeparateNamesakesAsync(popular, albums, titles, ct).ConfigureAwait(false);
            Save(name, separated);
            return (separated, []);
        }
        // Owner #140: an exact namesake from another catalog opens too (its first result); only a name with no exact
        // match at all still offers the spellings found.
        if (result.Candidates.FirstOrDefault(a => BotText.Key(a.Name) == BotText.Key(name)) is { } first)
        {
            Save(name, first);
            return (first, []);
        }
        return (null, result.Candidates);
    }

    /// <summary>The user picked one of several same-name artists: remembered for this name.</summary>
    internal void Choose(string name, CatalogArtist artist) => Save(name, artist, manual: true);

    private void Save(string name, CatalogArtist artist, bool manual = false)
    {
        var unchanged = _chosen.TryGetValue(name, out var old) && old == artist && _manual.Contains(name) == manual;
        _chosen[name] = artist;
        if (manual) _manual.Add(name); else _manual.Remove(name);
        if (unchanged || _choicesPath is null) return;
        try { AtomicJson.Write(_choicesPath, _chosen.ToDictionary(p => p.Key, p => new Choice(p.Value.Id, p.Value.Name, p.Value.Genre, _manual.Contains(p.Key)))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log("CATALOG_CHOICE_SAVE_FAILED: artist choice works for this session but could not be saved."); }
    }

    /// <summary>Opens the app's artist page for an online artist (Search, For you, Similar).</summary>
    public void OpenArtist(CatalogArtist artist)
    {
        if (ArtistRequested is null) throw new InvalidOperationException("Noctis is not showing pages yet.");
        ArtistRequested(artist);
    }

    /// <summary>Reads an online album (metadata only, no audio) and opens it on the album page.</summary>
    public async Task OpenAlbumAsync(CatalogRelease release, CancellationToken ct)
    {
        if (!_enabled()) throw new InvalidOperationException("Turn on the online catalog on artist pages (Free Music Finder settings) to open online albums.");
        // Owner #34: the page opens at once (skeleton rows) and fills when the songs arrive.
        AlbumOpening?.Invoke(release);
        try
        {
            var album = await ReadAlbumAsync(release, ct);
            ct.ThrowIfCancellationRequested();
            AlbumRequested?.Invoke(album);
        }
        catch (Exception ex)
        {
            AlbumOpenFailed?.Invoke(release, ex is OperationCanceledException ? "" : ex.Message);
            throw;
        }
    }

    /// <summary>Browsing must not lose a fetched tracklist because a scan or library check is busy.
    /// Download actions still do their own strict ownership check before writing anything.</summary>
    internal async Task<CatalogAlbumOpen> ReadAlbumAsync(CatalogRelease release, CancellationToken ct, bool refresh = false)
    {
        var album = await _catalog().AlbumAsync(release, refresh, ct);
        IReadOnlyDictionary<string, DiscoveryMatch> matches;
        try { matches = await Actions.MatchAsync(album.Value.Songs, ct, reuse: true); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            _log("CATALOG_LIBRARY_CHECK_FAILED: " + ex.Message);
            matches = new Dictionary<string, DiscoveryMatch>();
        }
        return new(album.Value, matches);
    }

    internal async Task<CatalogRelease?> FindLibraryReleaseAsync(CatalogArtist artist, string title, CancellationToken ct)
    {
        var catalog = _catalog();
        var profile = await catalog.SavedArtistAsync(artist, ct) ?? await catalog.ArtistAsync(artist, false, ct);
        bool Matches(CatalogRelease release) => release.Artist.Id == artist.Id && CatalogEditions.Matches(release, title, artist.Name);
        if (profile.Value.Releases.FirstOrDefault(Matches) is { } saved) return saved;
        // A saved profile may predate this release or be capped by the provider. Search the
        // album directly, keeping BOTH the edition and stable artist identity checks.
        var found = await catalog.SearchAlbumsAsync(artist.Name + " " + title, ct);
        return found.Value.FirstOrDefault(Matches);
    }

    /// <summary>The library album a catalog release stands for (same title edition, same artist), if any.</summary>
    internal Noctis.Models.Album? OwnedEdition(CatalogRelease release)
        => _library()?.Albums.FirstOrDefault(a => CatalogEditions.Matches(release, a.Name, a.Artist));

    public void Dispose() { }
}
