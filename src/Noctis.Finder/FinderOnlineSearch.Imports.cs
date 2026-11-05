using Noctis.Services.Search;

namespace FreeMusicFinder;

internal sealed partial class FinderOnlineSearch
{
    private NativePlaylistImports? _nativeImports;
    public IOnlinePlaylistImports? PlaylistImports => _playlists() is { } imports
        ? _nativeImports ??= new(this, imports) : null;

    private sealed class NativePlaylistImports : IOnlinePlaylistImports, IDisposable
    {
        private readonly FinderOnlineSearch _source;
        private readonly SpotifyPlaylists _imports;
        public NativePlaylistImports(FinderOnlineSearch source, SpotifyPlaylists imports)
        { _source = source; _imports = imports; imports.Changed += Forward; }
        public event Action? Changed;
        private void Forward() => Changed?.Invoke();

        private static OnlineImportJob Row(SpotifyPlaylist job) => new(job.LibraryId,
            job.Manifest?.Name is { Length: > 0 } name ? name : "Playlist", job.Url, job.Found, job.Total,
            job.Status, job.Created && job.Manifest?.CompleteIds == true, job.FillingStopped,
            job.Manifest?.Entries.FirstOrDefault(e => ArtistCatalog.ArtworkUrl(e.Artwork))?.Artwork ?? "");

        public IReadOnlyList<OnlineImportJob> Snapshot() => _imports.Jobs.Select(Row).ToArray();
        public OnlineImportedPlaylist? Read(Guid playlistId)
        {
            var job = _imports.Jobs.FirstOrDefault(j => j.LibraryId == playlistId);
            if (job is null || _imports.EntriesFor(playlistId) is not { } entries) return null;
            return new(Row(job), entries.Select(e => new OnlinePlaylistEntry(e.Position,
                e.Song is { } song ? _source.Remember(new(song, null, e.Artwork)) with { Album = e.Album, LocalTrackId = e.LocalId } : null, e.Issue)).ToArray());
        }
        public Task ImportAsync(string links, CancellationToken ct) => _imports.ImportAsync(links, ct);
        public Task CheckLibraryAsync(CancellationToken ct) => _imports.CheckLibraryAsync(ct);
        public Task<int> DownloadMissingAsync(Guid playlistId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_source.CanDownload) throw new InvalidOperationException("Sign in to Telegram to download music");
            var job = _imports.Jobs.FirstOrDefault(j => j.LibraryId == playlistId)
                ?? throw new InvalidOperationException("Playlist import no longer exists");
            // Only this playlist, and the shared importer deduplicates overlaps and checks ownership again.
            return _imports.QueueMissingAsync(_source._actions.LibraryFolder, ct, job);
        }
        public Task RemoveAsync(Guid playlistId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var job = _imports.Jobs.FirstOrDefault(j => j.LibraryId == playlistId);
            return job is null ? Task.CompletedTask : _imports.RemoveAsync(job);
        }
        public void Dispose() => _imports.Changed -= Forward;
    }
}
