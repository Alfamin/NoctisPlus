using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

internal sealed class SpotifyPlaylist
{
    public string Url { get; set; } = "";
    public Guid LibraryId { get; set; } = Guid.NewGuid();
    public PlaylistManifest? Manifest { get; set; }
    public bool Created { get; set; }
    public List<Guid> Applied { get; set; } = [];
    public Dictionary<int, Guid> Matches { get; set; } = new();
    public Dictionary<int, Guid> References { get; set; } = new();
    public Dictionary<int, string> MissingReasons { get; set; } = new();
    public int Hidden { get; set; }
    public string Status { get; set; } = "Waiting to read playlist";
    public bool FillingStopped { get; set; }
    public DateTimeOffset MetadataRetryAt { get; set; }
    public int Found => Matches.Count;
    public int Total => Manifest?.Total ?? 0;
}

internal sealed record ImportedPlaylistEntry(int Position, CatalogSong? Song, Guid? LocalId, string Issue, string Artwork = "", string Album = "");

/// <summary>Metadata imports create normal playlists of shared local references. The saved
/// manifest fills download gaps, without fetching Spotify again or duplicating the audio.</summary>
internal sealed class SpotifyPlaylists : IDisposable
{
    private readonly string _file;
    private readonly IPlaylistLibrary _library;
    private readonly Downloads _downloads;
    private readonly Func<MusicRequest, BotTrack> _track;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private PlaylistCatalog _catalog;
    private readonly List<SpotifyPlaylist> _jobs;
    private int? _snapshotStamp;
    public event Action? Changed;
    public IReadOnlyList<SpotifyPlaylist> Jobs { get { lock (_gate) return _jobs.ToArray(); } }

    /// <summary>
    /// Owner #36: the imported songs a native playlist's library lacks, in playlist order, so the playlist
    /// itself lists them (not only Download missing). Entries whose metadata never arrived have nothing to show.
    /// </summary>
    internal IReadOnlyList<(int Position, CatalogSong Song)> MissingFor(Guid playlistId)
    {
        var job = Jobs.FirstOrDefault(j => j.LibraryId == playlistId);
        if (job?.Manifest is not { CompleteIds: true } manifest || job.FillingStopped) return [];
        var matches = job.Matches;
        return manifest.Entries.Where(e => e.Request is not null && !matches.ContainsKey(e.Position))
            .OrderBy(e => e.Position).Select(e => (e.Position, Song(e))).ToArray();
    }

    // A snapshot for the native page, including positions with unavailable metadata.
    // A duplicate recording remains two entries; no audio or metadata requests happen here.
    internal IReadOnlyList<ImportedPlaylistEntry>? EntriesFor(Guid playlistId)
    {
        var job = Jobs.FirstOrDefault(j => j.LibraryId == playlistId);
        if (job?.Manifest is not { CompleteIds: true } manifest || job.FillingStopped) return null;
        var matches = job.Matches;
        _ = FillCoversAsync(job);
        return manifest.Entries.OrderBy(e => e.Position).Select(e => new ImportedPlaylistEntry(
            e.Position, e.Request is null ? null : Song(e),
            matches.TryGetValue(e.Position, out var id) ? id : null, e.Issue,
            ArtistCatalog.ArtworkUrl(e.Artwork) ? e.Artwork : "", e.Album ?? "")).ToArray();
    }

    // ── Owner #104: imports saved before covers were kept have none (the owner's three: 6 of 418 songs). Spotify's paged
    // list gives every cover in about a second per 100 songs, so they are filled in once, saved, and the page shows them. ──
    private readonly Dictionary<string, Task> _coverFills = new();
    private readonly Dictionary<string, DateTimeOffset> _coverFillTried = new();
    private static bool LacksCovers(SpotifyPlaylist job) => job.Manifest is { CompleteIds: true } manifest &&
        manifest.Entries.Any(e => e.Request is not null && e.Uri.StartsWith("spotify:track:", StringComparison.Ordinal) && !ArtistCatalog.ArtworkUrl(e.Artwork));

    internal Task FillCoversAsync(SpotifyPlaylist job)
    {
        lock (_gate)
        {
            if (_coverFills.TryGetValue(job.Url, out var running)) return running;
            if (!LacksCovers(job) || !PlaylistLink(job.Url, out var link) || link!.Service != "spotify" ||
                _coverFillTried.TryGetValue(job.Url, out var tried) && DateTimeOffset.UtcNow - tried < TimeSpan.FromMinutes(2)) return Task.CompletedTask;
            var fill = Task.Run(() => FillCoversCore(job, link, _stop.Token));
            _coverFills[job.Url] = fill;
            return fill;
        }
    }

    private async Task FillCoversCore(SpotifyPlaylist job, MusicLink link, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var filled = 0; var read = false;
        try
        {
            if (await _catalog.FullPlaylistAsync(link, ct, lenient: true).ConfigureAwait(false) is not { } paged) return;
            read = true; // once a session: songs Spotify has no cover for are not asked again on every refresh
            var covers = paged.Entries.Where(e => e.Uri.Length > 0 && ArtistCatalog.ArtworkUrl(e.Artwork)).GroupBy(e => e.Uri).ToDictionary(g => g.Key, g => g.First());
            lock (_gate)
            {
                foreach (var entry in job.Manifest?.Entries ?? [])
                    if (!ArtistCatalog.ArtworkUrl(entry.Artwork) && covers.TryGetValue(entry.Uri, out var found))
                    {
                        entry.Artwork = found.Artwork;
                        if (string.IsNullOrEmpty(entry.Album)) entry.Album = found.Album;
                        filled++;
                    }
            }
            if (filled == 0) return;
            try { Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // kept for this session
            Noctis.Services.DebugLog.Write("Playlist", $"covers filled in for {filled} songs of \"{DisplayName(job.Manifest?.Name ?? "")}\" in {clock.ElapsedMilliseconds} ms");
            Publish();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Noctis.Services.DebugLog.Write("Playlist", "covers not filled in: " + ex.Message); }
        finally
        {
            lock (_gate)
            {
                _coverFills.Remove(job.Url);
                _coverFillTried[job.Url] = read ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>Owner #104: the cover of an imported song ("spotify:ID"), as saved; null when it has none yet.</summary>
    internal string? ArtworkFor(string songId)
    {
        if (!songId.StartsWith("spotify:", StringComparison.Ordinal)) return null;
        var uri = "spotify:track:" + songId["spotify:".Length..];
        foreach (var job in Jobs)
            if (job.Manifest?.Entries.FirstOrDefault(e => e.Uri == uri && ArtistCatalog.ArtworkUrl(e.Artwork)) is { } entry) return entry.Artwork;
        return null;
    }

    /// <summary>Owner #104: as <see cref="ArtworkFor"/>, first filling in the covers of the imports that list the song.</summary>
    internal async Task<string?> ArtworkForAsync(string songId, CancellationToken ct)
    {
        if (ArtworkFor(songId) is { } saved) return saved;
        if (!songId.StartsWith("spotify:", StringComparison.Ordinal)) return null;
        var uri = "spotify:track:" + songId["spotify:".Length..];
        var fills = Jobs.Where(j => j.Manifest?.Entries.Any(e => e.Uri == uri) == true).Select(FillCoversAsync).ToArray();
        if (fills.Length == 0) return null;
        await Task.WhenAll(fills).WaitAsync(ct).ConfigureAwait(false);
        return ArtworkFor(songId);
    }

    // "spotify:track:ID" -> "spotify:ID": the catalog id shape (its Request maps it back to the track URI).
    private static CatalogSong Song(CatalogEntry entry)
    {
        var r = entry.Request!;
        var parts = entry.Uri.Split(':');
        var id = parts.Length == 3 ? parts[0] + ":" + parts[2] : entry.Uri;
        var artists = r.Artist.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => new CatalogArtist("", n)).ToList();
        return new(id, r.Title, artists.Count > 0 ? artists : [new("", r.Artist)], r.Seconds, r.Explicit);
    }
    public bool Busy { get; private set; }
    // Empty at rest: the links box's placeholder says what to do (owner 10-18: minimal text).
    public string Status { get; private set; } = "";
    public SpotifyPlaylists(string file, IPlaylistLibrary library, Downloads downloads, Func<MusicRequest, BotTrack> track, PlaylistCatalog? catalog = null)
    {
        _file = file; _library = library; _downloads = downloads; _track = track; _catalog = catalog ?? new();
        if (File.Exists(file))
        {
            if (new FileInfo(file).Length > 32 * 1024 * 1024 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("PLAYLIST_IMPORTS_UNREADABLE: saved imports are oversized or redirected. The original file was kept.");
            try
            {
                _jobs = JsonSerializer.Deserialize<List<SpotifyPlaylist>>(File.ReadAllText(file)) ?? throw new JsonException();
                if (_jobs.Count > 100 || _jobs.Select(j => j.Url).Distinct().Count() != _jobs.Count ||
                    _jobs.Select(j => j.LibraryId).Distinct().Count() != _jobs.Count || _jobs.Any(j => j.LibraryId == Guid.Empty || !PlaylistLink(j.Url, out _) ||
                        j.Applied is null || j.Matches is null || j.References is null || j.MissingReasons is null || j.MetadataRetryAt > DateTimeOffset.UtcNow.AddDays(8) ||
                        j.Applied.Count > 10000 || j.Matches.Count > 10000 || j.References.Count > 10000 || (j.Manifest is {} m &&
                        (m.Url != j.Url || m.Entries is null || m.Total < 0 || m.Total > 10000 || m.Entries.Count > 10000 ||
                        (m.CompleteIds && !Valid(m)))))) throw new JsonException();
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException)
            { throw new InvalidDataException("PLAYLIST_IMPORTS_UNREADABLE: saved imports could not be read; the original file was kept.", ex); }
        }
        else _jobs = [];
        _ = Task.Run(Watch);
    }
    internal static bool PlaylistLink(string input, out MusicLink? link)
    {
        if (Regex.IsMatch(input, "^spotify:playlist:[A-Za-z0-9]{22}$")) input = "https://open.spotify.com/playlist/" + input[17..];
        return MusicLink.TryParse(input, out link) && link!.Service is "spotify" or "deezer" && link.Kind == "playlist";
    }
    internal static IReadOnlyList<string> Links(string text)
    {
        if (text.Length > 32768) throw new FormatException("Paste up to 100 playlist links at once.");
        var values = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length == 0 || values.Length > 100) throw new FormatException("Paste Spotify or Deezer playlist links, one per line (up to 100).");
        var links = new List<string>();
        foreach (var value in values)
        {
            if (!PlaylistLink(value, out var link)) throw new FormatException("Use full Spotify or Deezer playlist links, one per line. Track, album and other website links are not playlist imports.");
            if (!links.Contains(link!.Url)) links.Add(link.Url);
        }
        return links;
    }
    private static bool Valid(PlaylistManifest m) => m.CompleteIds && m.Entries.Count == m.Total &&
        m.Entries.Select(e => e.Position).SequenceEqual(Enumerable.Range(1, m.Total)) &&
        m.Entries.All(e => !string.IsNullOrEmpty(e.Uri) && e.Uri.Length <= 1000 && (e.Request is not {} r ||
            !string.IsNullOrWhiteSpace(r.Title) && !string.IsNullOrWhiteSpace(r.Artist) && r.Title.Length <= 1000 && r.Artist.Length <= 1000));
    private void Save() { lock (_gate) AtomicJson.Write(_file, _jobs); }
    private void Publish(string? status = null) { if (status is not null) Status = status; try { Changed?.Invoke(); } catch { } }

    /// <summary>Owner #69: public playlists by name, each marked with the Noctis playlist it was already saved as.</summary>
    internal async Task<(IReadOnlyList<(FoundPlaylist Found, Guid? SavedAs)> Playlists, string Source)> SearchAsync(string query, int limit, CancellationToken ct, int offset = 0)
    {
        var (found, source) = await _catalog.SearchPlaylistsAsync(query, limit, ct, offset).ConfigureAwait(false);
        var saved = Jobs.Where(j => j.Created && !j.FillingStopped).ToDictionary(j => j.Url, j => j.LibraryId);
        return (found.Select(f => (f, saved.TryGetValue(f.Url, out var id) ? id : (Guid?)null)).ToList(), source);
    }

    internal void WarmUpSearch() => _ = _catalog.WarmUpSearchAsync();
    /// <summary>Owner #155: Spotify's song search (names and lyrics), best first.</summary>
    internal Task<IReadOnlyList<SpotifyTrackHit>> SearchTracksAsync(string term, int limit, CancellationToken ct) => _catalog.SearchTracksAsync(term, limit, ct);

    // Read previews, so Save after a look does not read the playlist again (owner: snappy).
    private readonly Dictionary<string, PlaylistManifest> _previews = new();

    /// <summary>
    /// A found playlist read in full and matched against the library, without creating anything: every entry in
    /// order, with the library song that stands for it. Save turns the same reading into a normal playlist.
    /// </summary>
    internal async Task<(PlaylistManifest Manifest, IReadOnlyList<ImportedPlaylistEntry> Entries)> PreviewAsync(string url, CancellationToken ct)
    {
        if (!PlaylistLink(url, out var link)) throw new FormatException("Not a Spotify or Deezer playlist.");
        PlaylistManifest? manifest;
        lock (_gate) _previews.TryGetValue(link!.Url, out manifest);
        if (manifest is null)
        {
            manifest = await _catalog.ReadAsync(link, null, _ => { }, _ => { }, ct).ConfigureAwait(false);
            if (!Valid(manifest)) throw new InvalidDataException("METADATA_INCOMPLETE: Spotify returned only part of this playlist. Try again.");
            lock (_gate) { if (_previews.Count >= 20) _previews.Clear(); _previews[link.Url] = manifest; }
        }
        var matcher = new PlaylistMatcher(await _library.SnapshotAsync().ConfigureAwait(false));
        return (manifest, manifest.Entries.OrderBy(e => e.Position).Select(e => new ImportedPlaylistEntry(
            e.Position, e.Request is null ? null : Song(e), e.Request is { } r ? matcher.Find(r)?.Id : null, e.Issue,
            ArtistCatalog.ArtworkUrl(e.Artwork) ? e.Artwork : "", e.Album ?? "")).ToArray());
    }

    /// <summary>The Noctis playlist a link was saved as, when it was.</summary>
    internal Guid? SavedAs(string url) => PlaylistLink(url, out var link) && Jobs.FirstOrDefault(j => j.Url == link!.Url && j.Created && !j.FillingStopped) is { } job ? job.LibraryId : null;

    /// <summary>Save (owner #69): the previewed playlist becomes a normal Noctis playlist, as a pasted link would.</summary>
    internal async Task<Guid> SaveAsync(string url, CancellationToken ct)
    {
        if (!PlaylistLink(url, out var link)) throw new FormatException("Not a Spotify or Deezer playlist.");
        await ImportAsync(link!.Url, ct).ConfigureAwait(false);
        var job = Jobs.FirstOrDefault(j => j.Url == link.Url);
        return job is { Created: true } ? job.LibraryId : throw new InvalidOperationException(job?.Status is { Length: > 0 } status ? status : "The playlist could not be saved.");
    }

    public async Task ImportAsync(string text, CancellationToken ct)
    {
        var links = Links(text); // Validate the whole paste before making any changes.
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = scope.Token;
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        Busy = true; Publish("Reading playlists…");
        try
        {
            foreach (var url in links)
            {
                ct.ThrowIfCancellationRequested();
                SpotifyPlaylist job;
                lock (_gate)
                {
                    job = _jobs.FirstOrDefault(j => j.Url == url)!;
                    if (job is null)
                    {
                        if (_jobs.Count >= 100) throw new InvalidOperationException("IMPORT_LIST_FULL: Remove a finished import from this list before adding more. Its Noctis playlist is kept.");
                        job = new() { Url = url }; _jobs.Add(job);
                        try { Save(); } catch { _jobs.Remove(job); throw; }
                    }
                }
                try
                {
                    if (job.Manifest is not { CompleteIds: true } || job.Manifest.Entries.Any(e => e.Request is null && e.Uri.StartsWith("spotify:track:")))
                    {
                        // Provider limits are independent: a blocked Spotify batch must not
                        // hold up a Deezer playlist in the same paste (or vice versa).
                        PlaylistLink(url, out var target);
                        var retry = Jobs.Where(j => PlaylistLink(j.Url, out var source) && source!.Service == target!.Service)
                            .Max(j => j.MetadataRetryAt);
                        if (retry > DateTimeOffset.UtcNow)
                        {
                            job.Status = $"METADATA_LIMIT: The catalog asked for a wait until {retry.ToLocalTime():ddd HH:mm}. Saved progress is kept; retry these links after that.";
                            Save(); Publish(); continue;
                        }
                        PlaylistLink(url, out var link);
                        job.Status = "Reading full metadata…"; Publish();
                        PlaylistManifest? previewed;
                        lock (_gate) _previews.TryGetValue(url, out previewed);
                        if (previewed is not null && job.Manifest is not { CompleteIds: true }) { job.Manifest = previewed; Save(); }
                        job.Manifest = await _catalog.ReadAsync(link!, job.Manifest,
                            m => { job.Manifest = m; Save(); }, p => { job.Status = p; Publish(); }, ct).ConfigureAwait(false);
                        if (!Valid(job.Manifest)) throw new InvalidDataException("METADATA_INCOMPLETE: No playlist was created from a partial preview.");
                    }
                    await Reconcile(job, new PlaylistMatcher(await _library.SnapshotAsync().ConfigureAwait(false))).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (SourcesWaitingException ex)
                { job.MetadataRetryAt = ex.Until; job.Status = $"METADATA_LIMIT: The catalog asked for a wait until {ex.Until.ToLocalTime():ddd HH:mm}. Saved progress is kept; retry after that."; Save(); Publish(); }
                catch (Exception ex) { job.Status = Explain(ex); Save(); Publish(); }
            }
            var selected = Jobs.Where(j => links.Contains(j.Url)).ToArray();
            Publish($"Import finished: {selected.Count(j => j.Created)} of {links.Count} playlists created · {selected.Sum(j => j.Found)} of {selected.Sum(j => j.Total)} entries found. Review missing songs or errors below.");
        }
        finally { Busy = false; _operation.Release(); Publish(); }
    }
    internal static string Explain(Exception ex)
    {
        while (ex is System.Reflection.TargetInvocationException && ex.InnerException is {} inner) ex = inner;
        return ex is UnauthorizedAccessException ? "PLAYLIST_SAVE_DENIED: Noctis cannot save playlists here. Check access to its data folder, then retry; music files were kept." : ex.Message;
    }
    public async Task CheckLibraryAsync(CancellationToken ct = default)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _operation.WaitAsync(scope.Token).ConfigureAwait(false);
        Busy = true;
        try { await CheckCore(scope.Token).ConfigureAwait(false); Publish("Library checked. Missing downloads will fill their original playlist positions."); }
        finally { Busy = false; _operation.Release(); Publish(); }
    }
    private async Task CheckCore(CancellationToken ct, IReadOnlyList<LocalSong>? snapshot = null)
    {
        var songs = snapshot ?? await _library.SnapshotAsync().ConfigureAwait(false);
        var matcher = new PlaylistMatcher(songs);
        foreach (var job in Jobs.Where(j => j.Manifest is { CompleteIds: true } && !j.FillingStopped))
        {
            ct.ThrowIfCancellationRequested();
            try { await Reconcile(job, matcher).ConfigureAwait(false); }
            catch (Exception ex) { job.Status = Explain(ex); Save(); Publish(); }
        }
        _snapshotStamp = Stamp(songs);
    }
    private static int Stamp(IEnumerable<LocalSong> songs)
    {
        var hash = new HashCode();
        foreach (var s in songs) { hash.Add(s.Id); hash.Add(s.Path); hash.Add(s.Title); hash.Add(s.Artist); hash.Add(s.Duration); hash.Add(s.Explicit); hash.Add(s.Visible); hash.Add(s.AlbumArtist); }
        return hash.ToHashCode();
    }
    private async Task Reconcile(SpotifyPlaylist job, PlaylistMatcher matcher)
    {
        var manifest = job.Manifest!;
        var oldCreated = job.Created; var oldApplied = job.Applied; var oldMatches = job.Matches;
        var oldReferences = job.References; var oldReasons = job.MissingReasons; var oldHidden = job.Hidden;
        var matches = new Dictionary<int, Guid>();
        var reasons = new Dictionary<int, string>();
        var hidden = 0;
        foreach (var entry in manifest.Entries)
            if (entry.Request is {} request)
            {
                if (matcher.Find(request) is {} found) { matches[entry.Position] = found.Id; if (!found.Visible) hidden++; }
                else reasons[entry.Position] = matcher.MissingReason(request);
            }
        // Keep references when a drive is temporarily disconnected. Only explicit user
        // edits remove songs; a new matching file can replace a stale ID in its original position.
        var previous = job.References.Count > 0 ? job.References : job.Matches;
        var references = new Dictionary<int, Guid>(previous);
        foreach (var match in matches) references[match.Key] = match.Value;
        var ordered = references.OrderBy(m => m.Key).Select(m => m.Value).ToList(); // duplicates retain their playlist positions
        var remapped = previous.OrderBy(m => m.Key).Select(m => matches.GetValueOrDefault(m.Key, m.Value)).ToList();
        var unchanged = job.Created && ordered.SequenceEqual(job.Applied) && job.Hidden == hidden && matches.Count == job.Matches.Count && matches.All(m => job.Matches.GetValueOrDefault(m.Key) == m.Value);
        // The native adapter also verifies that the playlist still exists and was not
        // manually edited; equal IDs return without a native disk write.
        {
            try { await _library.ApplyAsync(job.LibraryId, DisplayName(manifest.Name), job.Url, ordered, !job.Created, job.Applied, remapped).ConfigureAwait(false); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("PLAYLIST_EDITED:") || ex.Message.StartsWith("PLAYLIST_REMOVED:"))
            { job.FillingStopped = true; job.Status = ex.Message; Save(); Publish(); return; }
            job.Created = true; job.Applied = ordered;
        }
        if (unchanged) return;
        job.Matches = matches; job.References = references; job.Hidden = hidden; job.MissingReasons = reasons;
        job.Status = $"{job.Found} of {job.Total} songs matched locally · {job.Total - job.Found} missing" +
            (hidden > 0 ? $" · {hidden} in hidden Noctis folders: unhide them to see those entries; no duplicate download needed" : "");
        try { Save(); }
        catch
        {
            // Native save may already have succeeded. Retry reconciles that same stable
            // playlist ID; do not falsely mark an unsaved plugin checkpoint complete.
            job.Created = oldCreated; job.Applied = oldApplied; job.Matches = oldMatches;
            job.References = oldReferences; job.MissingReasons = oldReasons; job.Hidden = oldHidden; throw;
        }
        Publish();
    }
    internal static string DisplayName(string name)
    {
        var text = new string(name.Where(c => !char.IsControl(c)).Take(200).ToArray()).Trim();
        return text.Length > 0 ? text : "Spotify playlist"; // emoji names remain valid; never used as paths
    }
    /// <param name="only">The one import whose missing songs are wanted; null for those of every import.</param>
    public async Task<int> QueueMissingAsync(string folder, CancellationToken ct = default, SpotifyPlaylist? only = null)
    {
        folder = Path.GetFullPath(folder);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _operation.WaitAsync(scope.Token).ConfigureAwait(false);
        Busy = true;
        try
        {
            await CheckCore(scope.Token).ConfigureAwait(false);
            // Removed from the list meanwhile, never created, or edited in Noctis: its status says which.
            if (only is not null && (!Jobs.Contains(only) || !only.Created || only.FillingStopped)) { Publish(only.Status); return 0; }
            var queued = 0;
            var known = _downloads.History.Where(d => d.Track.Request is not null &&
                (d.IsActive || d.State == DownloadState.Failed || (d.State is DownloadState.Saved or DownloadState.AlreadyThere &&
                Downloader.FindExisting(d.Folder, d.Track.FileName) is not null))).Select(d => d.Track.Request!).ToList();
            // Owner 10-20: a song that was only played (stream cache) joins Songs from its cached copy, without a new download.
            var played = _downloads.History.Where(d => d.Track.Request is not null && d.KeepIn is null && _downloads.IsCached(d) &&
                d.State is DownloadState.Saved or DownloadState.AlreadyThere && Downloader.FindExisting(d.Folder, d.Track.FileName) is not null).ToList();
            foreach (var job in Jobs.Where(j => j.Created && !j.FillingStopped && (only is null || ReferenceEquals(j, only))))
                foreach (var entry in job.Manifest!.Entries.Where(e => !job.Matches.ContainsKey(e.Position)))
                {
                    scope.Token.ThrowIfCancellationRequested();
                    if (entry.Request is not {} request) continue;
                    if (played.FirstOrDefault(d => SameRecording(request, d.Track.Request!)) is { } cached)
                    { _downloads.Keep(cached, folder); played.Remove(cached); known.Add(request); queued++; continue; }
                    if (known.Any(k => SameRecording(request, k))) continue;
                    _downloads.Start(_track(request), folder, held: true);
                    known.Add(request); queued++;
                }
            var name = only?.Manifest is {} named ? DisplayName(named.Name) : "";
            Publish(only is null ? $"{queued} missing recordings added to the queue, without duplicates. Press Start queued downloads when ready; unavailable metadata entries stay listed."
                : queued > 0 ? $"{queued} missing recordings from \"{name}\" added to the queue. Press Start queued downloads when ready; unavailable metadata entries stay listed."
                : $"Nothing new to queue for \"{name}\": its missing songs are already in the queue or saved, or Spotify gave no usable metadata for them.");
            return queued;
        }
        finally { Busy = false; _operation.Release(); Publish(); }
    }
    internal static bool SameRecording(MusicRequest a, MusicRequest b) =>
        !(a.Explicit is {} explicitA && b.Explicit is {} explicitB && explicitA != explicitB) &&
        RecordingMatch.Fits(a, b.Artist, b.Title, b.Duration) && RecordingMatch.Fits(b, a.Artist, a.Title, a.Duration);
    public async Task RemoveAsync(SpotifyPlaylist job)
    {
        await _operation.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                var index = _jobs.IndexOf(job); if (index < 0) return;
                _jobs.RemoveAt(index); try { Save(); } catch { _jobs.Insert(index, job); throw; }
            }
            Publish("Import removed from this list. Its Noctis playlist, music and existing download requests were kept.");
        }
        finally { _operation.Release(); }
    }
    public async Task UpdateProxyAsync(string setting, Action<string>? log = null)
    {
        var replacement = new PlaylistCatalog(PlaylistCatalog.MetadataHandler(setting, log));
        await _operation.WaitAsync(_stop.Token).ConfigureAwait(false);
        try { _catalog.Dispose(); _catalog = replacement; }
        finally { _operation.Release(); }
    }
    private async Task Watch()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false);
                if (!Jobs.Any(j => j.Created && !j.FillingStopped && j.Found < j.Total) || !_operation.Wait(0)) continue;
                try
                {
                    var snapshot = await _library.SnapshotAsync().ConfigureAwait(false);
                    // Do not repeat filesystem probes/recording comparisons while Telegram is waiting.
                    if (_snapshotStamp != Stamp(snapshot)) await CheckCore(_stop.Token, snapshot).ConfigureAwait(false);
                }
                finally { _operation.Release(); }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) { Publish("PLAYLIST_FILL_RETRY: " + Explain(ex)); }
        }
    }
    public void Dispose() { _stop.Cancel(); /* catalog disposed after any active request releases the gate */ _ = CloseCatalog(); }
    private async Task CloseCatalog() { await _operation.WaitAsync().ConfigureAwait(false); try { _catalog.Dispose(); } finally { _operation.Release(); } }
}
