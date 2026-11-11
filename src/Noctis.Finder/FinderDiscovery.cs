using Noctis.Services.Search;

namespace FreeMusicFinder;

/// <summary>Typed phone adapter over the same Plus planner, cache, identity and feedback rules.
/// No phone-specific recommendation algorithm, desktop window or copied profile.</summary>
internal sealed class FinderDiscovery(ForYouSource source, FinderOnlineSearch online, IPlaylistLibrary library) : IOnlineDiscovery
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Page, string Song), DiscoverSong> _picks = new();
    private (string Receipt, string Batch, CatalogSong Song, bool Artist, DateTime Until)? _undo;
    private (string Receipt, List<ForYouCache.Page> Pages, DateTime Until)? _pagesUndo;

    public Task<string?> RemovePageAsync(string page) => Task.Run(() =>
    {
        var index = source.Pages().ToList().FindIndex(p => p.Batch.Length > 0 && p.Batch == page);
        if (index < 0 && page.StartsWith("saved-", StringComparison.Ordinal) && int.TryParse(page.AsSpan(6), out var saved)) index = saved;
        return source.RemovePage(index) is { } removed ? PagesUndo([removed]) : null;
    });

    public Task<string?> ClearPagesAsync() => Task.Run(() => source.ClearPages() is { Count: > 0 } removed ? PagesUndo(removed) : null);

    private string PagesUndo(List<ForYouCache.Page> pages)
    {
        var receipt = Guid.NewGuid().ToString("N");
        lock (_gate) _pagesUndo = (receipt, pages, DateTime.UtcNow.AddSeconds(8));
        return receipt;
    }

    public async Task<IReadOnlyList<RecommendationPage>> LoadAsync(bool refresh, CancellationToken ct)
    {
        var saved = await source.SavedAsync(ct).ConfigureAwait(false);
        DiscoverResult? current = saved;
        try
        {
            if (refresh || saved is null) current = await source.BuildAsync(refresh, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            current = source.LastSaved();
            if (current is null) throw;
        }
        ct.ThrowIfCancellationRequested();
        var songs = await library.SnapshotAsync().ConfigureAwait(false);
        var familiar = await source.FamiliarAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var owned = new PlaylistMatcher(songs, library.Exists);
            var pages = source.Pages().ToList();
            if (current is { Songs.Count: > 0 } && !pages.Any(p => p.Batch == current.Batch)) pages.Insert(0, current);
            var result = new List<RecommendationPage>();
            lock (_gate)
            {
                _picks.Clear();
                foreach (var (page, index) in pages.Select((p, i) => (p, i)))
                {
                    ct.ThrowIfCancellationRequested();
                    var id = page.Batch.Length > 0 ? page.Batch : "saved-" + index;
                    var eligible = page.Songs.Where(p => !source.Hidden.HidesSong(p.Track.Song)
                        && owned.FindSong(p.Track.Song.Request) is null
                        && !familiar.Contains(DiscoverIdentity.Key(p.Track.Song))).Take(Discover.Shelf).ToArray();
                    var cards = eligible.Select(p => online.Remember(p.Track)).ToArray();
                    for (var i = 0; i < eligible.Length; i++) _picks[(id, cards[i].Id)] = eligible[i];
                    if (cards.Length == 0) continue;
                    result.Add(new(id, cards, page.Notice));
                }
                if (result.Count == 0) result.Add(new("", [], current?.Unreachable == true
                    ? "Can't reach the music catalog. Try again" : "No new picks yet"));
            }
            return (IReadOnlyList<RecommendationPage>)result;
        }, ct).ConfigureAwait(false);
    }

    public Task ShownAsync(string page, IReadOnlyList<string> songIds) => Task.Run(() =>
    {
        lock (_gate)
        {
            var picks = _picks.Where(p => p.Key.Page == page).ToArray();
            var cards = picks.Select((p, i) => new DiscoverShown(DiscoverIdentity.Key(p.Value.Track.Song), p.Key.Song,
                DiscoverIdentity.Artist(p.Value.Track.Song.Artists.FirstOrDefault()?.Name), p.Value.Seed, p.Value.Own, i)).ToArray();
            if (cards.Length == 0) return;
            source.Feedback.Shown(page, cards);
            var visible = songIds.ToHashSet(StringComparer.Ordinal);
            source.Feedback.Seen(page, cards.Where(c => visible.Contains(c.SongId)).Select(c => c.Position).ToArray());
        }
    });

    public Task RecordAsync(string page, OnlineSong song, RecommendationAction action) => Task.Run(() =>
    {
        lock (_gate)
        {
            if (!_picks.TryGetValue((page, song.Id), out var pick)) return;
            var catalog = pick.Track.Song;
            source.Feedback.Record(page, DiscoverIdentity.Key(catalog), DiscoverIdentity.Artist(catalog.Artists.FirstOrDefault()?.Name),
                catalog.Title, action switch { RecommendationAction.Play => DiscoverAction.Play,
                    RecommendationAction.Queue => DiscoverAction.Queue, _ => DiscoverAction.Download });
        }
    });

    public Task<string?> HideAsync(string page, OnlineSong song, bool artist = false) => Task.Run(() =>
    {
        lock (_gate)
        {
            if (!_picks.TryGetValue((page, song.Id), out var pick)) return null;
            var track = pick.Track.Song;
            var changed = artist ? source.Hidden.SetArtistBlocked(track.Artists.FirstOrDefault()?.Name ?? "", true) : source.Hidden.Hide(track);
            if (!changed) return null;
            if (!artist) source.Feedback.Record(page, DiscoverIdentity.Key(track), DiscoverIdentity.Artist(track.Artists.FirstOrDefault()?.Name), track.Title, DiscoverAction.Dismiss);
            var receipt = Guid.NewGuid().ToString("N");
            _undo = (receipt, page, track, artist, DateTime.UtcNow.AddSeconds(8));
            return receipt;
        }
    });

    public Task<bool> UndoAsync(string receipt) => Task.Run(() =>
    {
        lock (_gate)
        {
            if (_pagesUndo is { } pages && pages.Receipt == receipt)
            {
                _pagesUndo = null;
                if (pages.Until < DateTime.UtcNow) return false;
                source.RestorePages(pages.Pages);
                return true;
            }
            if (_undo is not { } undo || undo.Receipt != receipt || undo.Until < DateTime.UtcNow) return false;
            _undo = null;
            if (undo.Artist) source.Hidden.SetArtistBlocked(undo.Song.Artists.FirstOrDefault()?.Name ?? "", false);
            else
            {
                source.Hidden.Undo(undo.Song);
                source.Feedback.Retract(undo.Batch, DiscoverIdentity.Key(undo.Song), DiscoverAction.Dismiss);
            }
            return true;
        }
    });
}
