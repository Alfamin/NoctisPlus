namespace FreeMusicFinder;

internal sealed record DiscoveryMatch(LocalSong? Local, Download? Download, string Detail);
internal sealed record DiscoveryQueued(int Added, int Existing, int Owned);

/// <summary>Shared by the native artist panel and Finder. Browsing never calls this writer.</summary>
internal sealed class DiscoveryActions(IPlaylistLibrary library, Downloads downloads, Func<MusicRequest, BotTrack> track,
    Func<string> folder, Func<bool> loggedIn)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (long Key, DateTimeOffset At, PlaylistMatcher Matcher)? _matcher;
    public Downloads Downloads => downloads;

    /// <summary>The matcher checks every library file still exists, ~2,000 disk lookups. Search
    /// asks on every query, so one matcher serves while the library is unchanged (same songs,
    /// same paths) for up to two minutes.</summary>
    private PlaylistMatcher Matcher(IReadOnlyList<LocalSong> snapshot)
    {
        long key = snapshot.Count;
        foreach (var s in snapshot) key = key * 31 + s.Id.GetHashCode() + StringComparer.OrdinalIgnoreCase.GetHashCode(s.Path);
        var now = DateTimeOffset.UtcNow;
        if (_matcher is { } cached && cached.Key == key && now - cached.At < TimeSpan.FromMinutes(2)) return cached.Matcher;
        var matcher = new PlaylistMatcher(snapshot);
        _matcher = (key, now, matcher);
        return matcher;
    }

    /// <param name="reuse">Browsing (Search) may reuse a recent matcher; anything that decides
    /// what to download checks the disk afresh.</param>
    public async Task<Dictionary<string, DiscoveryMatch>> MatchAsync(IEnumerable<CatalogSong> songs, CancellationToken ct, bool reuse = false)
    {
        var rows = songs.ToArray(); var snapshot = await library.SnapshotAsync(); ct.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            var matcher = reuse ? Matcher(snapshot) : new PlaylistMatcher(snapshot); var history = downloads.History;
            return rows.DistinctBy(s => s.Id).ToDictionary(s => s.Id, s =>
            {
                var local = matcher.Find(s.Request);
                var queued = history.LastOrDefault(d => d.Track.Request is {} r && SpotifyPlaylists.SameRecording(s.Request, r) &&
                    (d.IsActive || d.State == DownloadState.Failed || d.State is DownloadState.Saved or DownloadState.AlreadyThere && Downloader.FindExisting(d.Folder, d.Track.FileName) is not null));
                return new DiscoveryMatch(local, queued, local is not null ? (local.Visible ? "In your library" : "In your library · hidden folder") : queued is not null ? queued.State switch
                {
                    DownloadState.Saved or DownloadState.AlreadyThere => "Saved · waiting for Noctis to scan",
                    DownloadState.Running => "Downloading · " + queued.Progress.ToString("0") + "%",
                    DownloadState.Waiting => queued.Error,
                    DownloadState.Failed => "Needs retry · " + queued.Error,
                    _ => queued.Held ? "Queued · press Start queued downloads" : "In download queue",
                } : matcher.MissingReason(s.Request).Replace("Spotify ", "Catalog "));
            });
        }, ct);
    }
    public async Task<DiscoveryQueued> QueueAsync(IEnumerable<CatalogSong> songs, bool priority, CancellationToken ct)
    {
        if (!loggedIn()) throw new InvalidOperationException("Log in to Telegram in Free Music Finder, then choose the songs again. Browsing does not need a login.");
        await _gate.WaitAsync(ct);
        try
        {
            var unique = new List<CatalogSong>();
            foreach (var song in songs) if (!unique.Any(s => SpotifyPlaylists.SameRecording(s.Request, song.Request))) unique.Add(song);
            // Always recheck: a download or library rescan may have finished since this page was drawn.
            var matches = await MatchAsync(unique, ct); var target = folder(); int added = 0, existing = 0, owned = 0;
            foreach (var song in unique)
            {
                ct.ThrowIfCancellationRequested(); var match = matches[song.Id];
                if (match.Local is not null) { owned++; continue; }
                if (match.Download is {} current)
                {
                    if (priority && (current.IsActive || current.State == DownloadState.Failed)) downloads.DownloadNext(current);
                    existing++; continue;
                }
                try { downloads.Start(track(song.Request), target, priority); added++; }
                catch (Exception ex) { throw new IOException($"Queued {added} song(s) before the queue could not be saved. Those requests are kept; open Queue to review them. " + ex.Message, ex); }
            }
            return new(added, existing, owned);
        }
        finally { _gate.Release(); }
    }
}
