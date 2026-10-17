namespace Noctis.Services.Search;

// Scores are relevance on the library index's scale (100 = the name typed exactly, 70 = every
// word in the name, 40-55 = words spread over name and credits), so both halves rank together.

/// <summary>An online catalog artist. Ids are opaque ("deezer:123").</summary>
public sealed record OnlineArtist(string Id, string Name, string PictureUrl = "", string Genre = "", double Score = 0);

/// <summary>An online catalog release. <paramref name="Kind"/> is "album", "single" or "ep".</summary>
public sealed record OnlineAlbum(string Id, string Title, string ArtistName, string Year = "", string Kind = "album",
    int TrackCount = 0, bool? Explicit = null, string ArtworkUrl = "", double Score = 0);

/// <summary>An online catalog song. <paramref name="LocalTrackId"/> is set when the same recording
/// is already in the library (then the library row is shown instead).</summary>
public sealed record OnlineSong(string Id, string Title, string ArtistDisplay, string Album = "",
    double? Seconds = null, bool? Explicit = null, string ArtworkUrl = "", Guid? LocalTrackId = null, string AlbumId = "", double Score = 0);

/// <summary>
/// One state of an online search. A search yields several, each more complete than the last
/// (songs first, then artists and albums, then library matches); the last has
/// <paramref name="Complete"/> set. Lists are in relevance order.
/// </summary>
public sealed record OnlineSearchResult(string Query, IReadOnlyList<OnlineSong> Songs, IReadOnlyList<OnlineArtist> Artists,
    IReadOnlyList<OnlineAlbum> Albums, bool Complete, string Warning = "")
{
    /// <summary>The top result's kind ("artist", "song", "album") and id, when the source has an opinion.</summary>
    public (string Kind, string Id)? Top { get; init; }
}

public enum OnlineDownloadState { None, Queued, Downloading, Saved, Failed }

public sealed record OnlineDownloadStatus(OnlineDownloadState State, double Progress = 0, string Message = "")
{
    public static readonly OnlineDownloadStatus None = new(OnlineDownloadState.None);
}

/// <summary>
/// The online half of Search, provided by a built-in feature (Free Music Finder). The Search page
/// works without it (library only). Calls come from the UI thread; results may complete on any
/// thread. Browsing never downloads audio: only <see cref="DownloadAsync"/> does, after a click.
/// </summary>
public interface IOnlineMusicSearch
{
    /// <summary>Opens the network connection ahead of the first query (DNS, TCP, TLS through the
    /// user's proxy), so the first search does not pay for it. Cheap to call again.</summary>
    void WarmUp();

    /// <summary>Progressive results for <paramref name="query"/>, each a fuller picture than the last.</summary>
    IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, CancellationToken ct);

    /// <summary>A local file for <paramref name="url"/> if it was fetched before, else null (no network).</summary>
    string? CachedArtwork(string url);

    /// <summary>Fetches artwork into the local cache; the file path, or null when it cannot be had.</summary>
    Task<string?> ArtworkAsync(string url, CancellationToken ct);

    /// <summary>True when downloads can start right away (signed in to the download service).</summary>
    bool CanDownload { get; }

    /// <summary>Asks the user to sign in to the download service.</summary>
    void RequestSignIn();

    OnlineDownloadStatus StatusOf(OnlineSong song);

    /// <summary>Raised (any thread) when a download's state or progress changes.</summary>
    event EventHandler? DownloadsChanged;

    /// <summary>Downloads (now = ahead of the queue) or queues the songs. Returns a short message for the user.</summary>
    Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct);

    /// <summary>Shows the download queue.</summary>
    void OpenDownloads();

    /// <summary>Opens the native artist page for an online artist.</summary>
    void OpenArtist(OnlineArtist artist);

    /// <summary>Opens the native album page for an online release.</summary>
    Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct);
}

/// <summary>Where the running online source is published (set by the feature while it runs).</summary>
public static class OnlineMusicSearch
{
    private static IOnlineMusicSearch? _current;

    public static IOnlineMusicSearch? Current
    {
        get => _current;
        set
        {
            if (ReferenceEquals(_current, value)) return;
            _current = value;
            CurrentChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? CurrentChanged;

    /// <summary>A feature asks the app to show Search (its shortcut, "Find more by this artist").
    /// Null keeps the last query.</summary>
    public static event EventHandler<string?>? OpenRequested;

    /// <summary>False when nothing shows Search (tests, or the app shell not built yet).</summary>
    public static bool RequestOpen(string? query)
    {
        if (OpenRequested is not { } handler) return false;
        handler(null, query);
        return true;
    }
}
