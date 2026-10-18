#if DEBUG
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using FreeMusicFinder;
using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;

namespace Noctis.Features.ProgressivePlayback;

/// <summary>Real native views/player/queue/part files with generated audio only. No provider
/// account, network or external library. Every fixture must be inside the explicit test profile.</summary>
internal static class ProgressivePlaybackPreview
{
    internal static async Task ShowAsync(Window owner, string mode)
    {
        if (owner.DataContext is not MainWindowViewModel main) return;
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NOCTIS_PLUS_DATA_DIR") ?? throw new InvalidOperationException());
        var audio = Path.GetFullPath(Environment.GetEnvironmentVariable("NOCTISPLUS_PREVIEW_AUDIO") ?? "missing");
        if (!audio.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(audio) || new FileInfo(audio).Length > 10 * 1024 * 1024) return;
        var ext = Path.GetExtension(audio).TrimStart('.').ToLowerInvariant();
        if (ext is not "mp3" and not "flac") return;
        var folder = Path.Combine(root, "generated-downloads"); Directory.CreateDirectory(folder);
        var source = new PreviewSource(folder, File.ReadAllBytes(audio), ext, new PlaylistLibrary(() => main.Library, () => main.Sidebar));
        OnlineMusicSearch.Current = source;
        main.Player.IsMuted = true; main.Player.Volume = 0;
        var trace = Path.Combine(root, "progressive-preview.csv");
        File.AppendAllText(trace, "utc,state,seconds,duration,buffering,canSeek,external,library,download\n");
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            try { File.AppendAllText(trace, FormattableString.Invariant($"{DateTime.UtcNow:O},{main.Player.State},{main.Player.Position.TotalSeconds:0.000},{main.Player.Duration.TotalSeconds:0.000},{main.Player.IsBuffering},{main.Player.CanSeekCurrent},{main.Player.CurrentTrack?.IsExternal},{main.Library.Tracks.Count},{source.TransferState}\n")); }
            catch (IOException) { }
        });
        timer.Start();
        owner.Closed += (_, _) => { timer.Stop(); if (ReferenceEquals(OnlineMusicSearch.Current, source)) OnlineMusicSearch.Current = null; source.Dispose(); };
        if (mode == "progressive-album")
        {
            var artist = new CatalogArtist("deezer:456", "Synthetic Artist");
            var release = new CatalogRelease("deezer:789", "Night Drive", artist, "2026-10-18", "album", 1);
            main.OpenAlbumDetail(new Album { Name = release.Title, Artist = artist.Name, CatalogReleaseId = release.Id,
                TrackCount = 1, TotalDuration = TimeSpan.FromSeconds(24), Year = 2026, Genre = "" });
            if (main.CurrentView is AlbumDetailViewModel album)
                album.ShowCatalog(new(new(release, [source.CatalogSong]), new Dictionary<string, DiscoveryMatch>()));
        }
        else { main.OpenSearchPage(); main.TopBar.SearchText = "Night Drive"; }
        if (Environment.GetEnvironmentVariable("NOCTISPLUS_PREVIEW_AUTOPLAY") == "1")
        {
            await Task.Delay(2000);
            if (main.CurrentView is SearchViewModel search && search.Songs.FirstOrDefault() is { } row)
                search.PlaySongCommand.Execute(row);
            else if (main.CurrentView is AlbumDetailViewModel album && album.CatalogMissing.FirstOrDefault() is { } missing)
                album.PlayCatalogSongCommand.Execute(missing);
        }
    }

    private sealed class AudioSource(byte[] audio, string ext) : IMusicSource, IQualityMusicSource
    {
        public string Id => "Synthetic source";
        public Task SetQualityAsync(AudioQuality quality, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MusicRequest>> SearchAsync(string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MusicRequest>>([]);
        public async Task<string> FetchAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
        {
            ((IProgressiveAudioWriter)output).BeginAudio(audio.Length, ext);
            await AudioFileTransfer.CopyAsync(output, audio.Length, async (offset, count, token) =>
            {
                // Withhold the tail long enough to inspect active playback and buffering.
                if (offset >= audio.Length * .6) await Task.Delay(TimeSpan.FromSeconds(45), token);
                return audio.AsSpan((int)offset, Math.Min(count, audio.Length - (int)offset)).ToArray();
            }, progress, ct);
            return ext;
        }
    }
    private sealed class PreviewSource : IOnlineMusicSearch, IDisposable
    {
        private readonly FinderOnlineSearch _inner;
        private readonly FreeMusicFinder.Downloads _downloads;
        private readonly CatalogPages _pages;
        private readonly DiscoveryActions _actions;
        internal readonly CatalogSong CatalogSong = new("deezer:123", "Night Drive", [new("deezer:456", "Synthetic Artist")], 24, false);
        private readonly OnlineSong _song;
        internal string TransferState => string.Join('+', _downloads.History.Select(d => d.State.ToString()));
        internal PreviewSource(string folder, byte[] audio, string ext, IPlaylistLibrary library)
        {
            var sources = new MusicSources(new AudioSource(audio, ext), new AudioSource(audio, ext), _ => { }, quality: () => ext == "mp3" ? AudioQuality.MP3 : AudioQuality.FLAC);
            _downloads = new(_ => { }, Path.Combine(folder, "synthetic-queue.json"), sources.Track);
            _actions = new DiscoveryActions(library, _downloads, sources.Track, () => folder, () => true);
            _pages = new(() => throw new InvalidOperationException("Synthetic preview never opens a network catalog"), _actions, _ => { }, () => false);
            _inner = new(() => null, _actions, _pages, () => true, () => throw new InvalidOperationException("Synthetic preview never signs in"));
            _song = _inner.Remember(new(CatalogSong, null)) with { Score = 97 };
        }
        public void WarmUp() { }
        public void Retry() { }
        public string Route => "synthetic";
        public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, [EnumeratorCancellation] CancellationToken ct)
        {
            var match = (await _actions.MatchAsync([CatalogSong], ct, reuse: true))[CatalogSong.Id];
            yield return new(query, [_song with { LocalTrackId = match.Local?.Id }], [], [], true) { Top = ("song", _song.Id) };
        }
        public string? CachedArtwork(string url) => null;
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>(null);
        public bool CanDownload => true;
        public void RequestSignIn() => throw new InvalidOperationException("Synthetic preview never signs in");
        public OnlineDownloadStatus StatusOf(OnlineSong song) => _inner.StatusOf(song);
        public event EventHandler? DownloadsChanged { add => _inner.DownloadsChanged += value; remove => _inner.DownloadsChanged -= value; }
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct) => _inner.DownloadAsync(songs, now, ct);
        public Task<Track> PreparePlaybackAsync(OnlineSong song, CancellationToken ct) => _inner.PreparePlaybackAsync(song, ct);
        public IDownloadQueue Downloads => _inner.Downloads;
        public void OpenArtist(OnlineArtist artist) { }
        public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { _inner.Detach(); _downloads.Dispose(); _pages.Dispose(); }
    }
}
#endif
