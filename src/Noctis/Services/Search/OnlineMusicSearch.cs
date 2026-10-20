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

/// <summary>A public playlist found by name (owner #69: "hype drake", "gym"). <paramref name="Saves"/> is how many
/// people saved it, when the service says; <paramref name="SavedAs"/> is the Noctis playlist it was already saved as.</summary>
public sealed record OnlinePlaylist(string Id, string Name, string Owner, string Url, string ArtworkUrl = "",
    int TrackCount = 0, long? Saves = null, Guid? SavedAs = null, double Score = 0);

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

    /// <summary>Public playlists by name, most relevant and most saved first.</summary>
    public IReadOnlyList<OnlinePlaylist> Playlists { get; init; } = [];
}

/// <summary>Streamed: played and kept in the stream cache, not in the library (owner #87).</summary>
public enum OnlineDownloadState { None, Queued, Downloading, Saved, Failed, Streamed }

/// <summary>What a running download is doing right now (owner #54: show it, with icons, not text).</summary>
public enum OnlineDownloadStep { None, Asking, Switching, Receiving, Saving }

public sealed record OnlineDownloadStatus(OnlineDownloadState State, double Progress = 0, string Message = "",
    OnlineDownloadStep Step = OnlineDownloadStep.None)
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

    /// <summary>The user (or a changed connection) asks to try again: pauses the source set on
    /// its own after a refusal end now; a wait the service itself asked for is kept.</summary>
    void Retry();

    /// <summary>The way out right now (proxy, network). When it changes, a failed search is worth
    /// running again: a VPN was switched on, Wi-Fi came back.</summary>
    string Route { get; }

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
    /// <summary>Owner #95: where the song was saved in Songs (not the stream cache), once it is; else null.</summary>
    string? SavedPathOf(OnlineSong song) => null;
    /// <summary>Owner #95/#100: a download asked for ahead that the queue no longer needs is dropped, also one already
    /// transferring (unless it is being heard). True when one was dropped.</summary>
    bool CancelQueued(OnlineSong song) => false;
    /// <summary>Owner #100: the bot is asked for the song so it is ready in the chat; nothing is downloaded. A later download
    /// starts from there without asking again.</summary>
    Task AskAheadAsync(OnlineSong song, CancellationToken ct) => Task.CompletedTask;
    /// <summary>Owner #100: downloaded ahead in the fast lane; <paramref name="keep"/> false saves it in the stream cache
    /// (a song only played), not in Songs.</summary>
    Task PrefetchAsync(OnlineSong song, bool keep, CancellationToken ct) => DownloadAsync([song], true, ct);

    /// <summary>Raised (any thread) when a download's state or progress changes.</summary>
    event EventHandler? DownloadsChanged;

    /// <summary>Downloads (now = ahead of the queue) or queues the songs. Returns a short message for the user.</summary>
    Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct);

    /// <summary>Explicit Play: reuse the durable download and return a playable local or
    /// buffered source. Cancelling this wait never cancels the saved download request.</summary>
    Task<Noctis.Models.Track> PreparePlaybackAsync(OnlineSong song, CancellationToken ct)
        => Task.FromException<Noctis.Models.Track>(new NotSupportedException("This source does not support online playback."));

    /// <summary>Owner #88: <paramref name="keep"/> = the song belongs to one of the user's playlists, so it joins Songs
    /// instead of only streaming.</summary>
    Task<Noctis.Models.Track> PreparePlaybackAsync(OnlineSong song, bool keep, CancellationToken ct) => PreparePlaybackAsync(song, ct);

    /// <summary>Owner #87: a file that was only played (in the stream cache, not added to Songs).</summary>
    bool IsStreamedFile(string? path) => false;

    /// <summary>Owner #88: a file in the stream cache (played, added or not).</summary>
    bool IsCachedFile(string? path) => false;

    /// <summary>Owner #87: "Add to songs" for a played file. Copies it into the library now, or as soon as it is saved.
    /// <see cref="StreamedAdd.LibraryPath"/> is where its library copy is (or will be), also when it was added before.</summary>
    Task<StreamedAdd> AddStreamedAsync(string path, CancellationToken ct) => Task.FromResult(new StreamedAdd("This song cannot be added here.", null));

    /// <summary>The download queue behind the Downloads page.</summary>
    IDownloadQueue Downloads { get; }

    /// <summary>Opens the native artist page for an online artist.</summary>
    void OpenArtist(OnlineArtist artist);

    /// <summary>Opens the native album page for an online release.</summary>
    Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct);

    /// <summary>The next page of playlists for <paramref name="query"/> after the first <paramref name="offset"/>
    /// (owner #71: more load as the Playlists tab is scrolled). Empty when there are no more.</summary>
    Task<IReadOnlyList<OnlinePlaylist>> MorePlaylistsAsync(string query, int offset, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<OnlinePlaylist>>([]);
}

/// <summary>Owner #87: the outcome of Add to songs: a short message, and the library copy's path.</summary>
public sealed record StreamedAdd(string Message, string? LibraryPath);

/// <summary>One request on the Downloads page. <paramref name="Handle"/> identifies it to the queue.</summary>
public sealed record DownloadEntry(object Handle, string Title, string Artist, OnlineDownloadState State, double Progress,
    string Detail, bool IsNext, bool IsHeld, string? SavedPath = null, double? Seconds = null,
    OnlineDownloadStep Step = OnlineDownloadStep.None);

/// <summary>The download queue (one transfer at a time, a priority lane, a held batch). Calls come
/// from the UI thread; <see cref="Changed"/> may fire on any thread, often (progress).</summary>
public interface IDownloadQueue
{
    /// <summary>In queue order: the running one, then the priority lane, then the rest; failed after;
    /// finished ones last (most recent first).</summary>
    IReadOnlyList<DownloadEntry> Snapshot();
    bool IsPaused { get; }
    event EventHandler? Changed;
    void SetPaused(bool paused);
    void RetryFailed();
    /// <summary>Starts songs staged without starting (a playlist's missing songs).</summary>
    void StartHeld();
    /// <summary>Removes every request and the finished list. Downloaded files stay.</summary>
    void Clear();
    void DownloadNext(DownloadEntry entry);
    void Remove(DownloadEntry entry);
}

/// <summary>Where the running online source is published (set by the feature while it runs).</summary>
public static class OnlineMusicSearch
{
    /// <summary>A new file was committed successfully. Native consumers need no queue polling.
    /// Raised on the transfer thread; callbacks must not block it.</summary>
    internal static event EventHandler<string>? FileSaved;
    internal static void PublishSavedFile(string path)
    {
        if (FileSaved is not { } listeners) return;
        foreach (EventHandler<string> listener in listeners.GetInvocationList())
            try { listener(null, path); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Saved-file observer failed: " + ex.GetType().Name); }
    }
    private static IReadOnlyList<string> _filesInPlayer = [];
    /// <summary>Owner #87: the files the player holds (now playing, up next, history), kept up to date by the player so the
    /// stream cache never deletes a song that is still queued.</summary>
    public static IReadOnlyList<string> FilesInPlayer { get => Volatile.Read(ref _filesInPlayer); set => Volatile.Write(ref _filesInPlayer, value); }
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
