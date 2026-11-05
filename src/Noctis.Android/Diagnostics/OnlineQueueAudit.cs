#if DEBUG && NOCTIS_ONLINE_QUEUE_AUDIT
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Search;

namespace Noctis.Android.Diagnostics;

/// <summary>Opt-in synthetic transport for exercising the production phone pages/player.
/// Restricted to an isolated audit package. Never logs in, messages bots or downloads audio.</summary>
internal sealed class OnlineQueueAudit(string audio) : IOnlineMusicSearch, IOnlineDiscovery
{
    private readonly OnlineSong[] _songs = Enumerable.Range(1, 7).Select(i => new OnlineSong("deezer:" + (900000 + i), "Queue Audit " + i, "Noctis Audit", Seconds: 40)).ToArray();
    private readonly HashSet<string> _cached = [];
    private string AudioPath(OnlineSong song)
    {
        var index = Array.FindIndex(_songs, s => s.Id == song.Id);
        if (index < 0) throw new ArgumentException("Unknown audit song", nameof(song));
        var path = Path.Combine(Path.GetDirectoryName(audio)!, $"queue-audit-{index + 1}.mp3");
        if (!File.Exists(path)) File.Copy(audio, path);
        return path;
    }
    public bool CanDownload => true;
    public string Route => "synthetic audit";
    public IDownloadQueue Downloads { get; } = new EmptyQueue();
    public event EventHandler? DownloadsChanged;
    public void WarmUp() { }
    public void Retry() { }
    public void RequestSignIn() { }
    public string? CachedArtwork(string url) => null;
    public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>(null);
    public void OpenArtist(OnlineArtist artist) { }
    public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => Task.CompletedTask;
    public OnlineDownloadStatus StatusOf(OnlineSong song) => _cached.Contains(song.Id) ? new(OnlineDownloadState.Streamed, 1) : OnlineDownloadStatus.None;
    public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct) => Task.FromResult("Synthetic audit: no downloads");
    public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    { await Task.CompletedTask; yield return new(query, _songs, [], [], true); }
    public async Task<Track> PreparePlaybackAsync(OnlineSong song, bool keep, CancellationToken ct)
    {
        DebugLog.Write("QueueAudit", "PREPARE " + song.Id);
        await Task.Delay(1500, ct);
        if (!File.Exists(audio)) throw new IOException("Copy the synthetic probe.mp3 into this audit package first");
        _cached.Add(song.Id); DownloadsChanged?.Invoke(this, EventArgs.Empty);
        DebugLog.Write("QueueAudit", "READY " + song.Id);
        return new() { FilePath = AudioPath(song), Title = song.Title, Artist = song.ArtistDisplay, Duration = TimeSpan.FromSeconds(40), IsExternal = true };
    }
    public string? CompletedPlaybackPath(OnlineSong song) => _cached.Contains(song.Id) && File.Exists(audio) ? AudioPath(song) : null;
    public bool RestoreQueueSong(OnlineSong song) => _songs.Any(s => s.Id == song.Id);
    public Task PrefetchAsync(OnlineSong song, bool keep, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); DebugLog.Write("QueueAudit", "FETCH " + song.Id); _cached.Add(song.Id); return Task.CompletedTask; }
    public Task AskAheadAsync(OnlineSong song, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); DebugLog.Write("QueueAudit", "ASK " + song.Id); return Task.CompletedTask; }
    public void SetPlaybackWindow(string? currentPath, IReadOnlyList<OnlineSong> songs) => DebugLog.Write("QueueAudit", "WINDOW " + string.Join(",", songs.Select(s => s.Id)));
    public Task<IReadOnlyList<RecommendationPage>> LoadAsync(bool refresh, CancellationToken ct) => Task.FromResult<IReadOnlyList<RecommendationPage>>([new("audit", _songs)]);
    public Task RecordAsync(string page, OnlineSong song, RecommendationAction action) => Task.CompletedTask;
    public Task ShownAsync(string page, IReadOnlyList<string> ids) => Task.CompletedTask;
    public Task<string?> HideAsync(string page, OnlineSong song, bool artist = false) => Task.FromResult<string?>(null);
    public Task<bool> UndoAsync(string receipt) => Task.FromResult(false);
    private sealed class EmptyQueue : IDownloadQueue
    {
        public IReadOnlyList<DownloadEntry> Snapshot() => [];
        public bool IsPaused => false;
        public event EventHandler? Changed { add { } remove { } }
        public void SetPaused(bool value) { }
        public void RetryFailed() { }
        public void StartHeld() { }
        public void Clear() { }
        public void DownloadNext(DownloadEntry entry) { }
        public void Remove(DownloadEntry entry) { }
    }
}
#endif
