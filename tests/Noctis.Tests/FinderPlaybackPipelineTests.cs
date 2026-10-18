using FreeMusicFinder;
using Noctis.Features.ProgressivePlayback;
using Noctis.Services.Search;
using Xunit;

namespace Noctis.Tests;

/// <summary>Native catalog action -> durable queue -> real part file -> quality routing ->
/// early playable path -> complete saved file. No Telegram, HTTP or real profile.</summary>
[Collection("MetadataServiceStatics")] // emits the same saved-download event consumed by native timing/import tests
public class FinderPlaybackPipelineTests
{
    private sealed class EmptyLibrary : IPlaylistLibrary
    {
        public Task<IReadOnlyList<LocalSong>> SnapshotAsync() => Task.FromResult<IReadOnlyList<LocalSong>>([]);
        public Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null) => Task.CompletedTask;
    }
    private sealed class Source(string id, string extension, bool wait = true) : IMusicSource, IQualityMusicSource
    {
        public string Id => id;
        internal int Calls, Configured;
        internal CancellationToken TransferToken;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Tail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> SetQualityAsync(AudioQuality quality, CancellationToken ct) { Configured++; return Task.FromResult(false); }
        public bool QualityConfirmed(AudioQuality quality) => false;
        public void ForgetQuality() { }
        public Task<IReadOnlyList<MusicRequest>> SearchAsync(string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<MusicRequest>>([]);
        public async Task<string> FetchAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
        {
            Calls++; TransferToken = ct;
            ((IProgressiveAudioWriter)output).BeginAudio(1024 * 1024, extension);
            var bytes = new byte[512 * 1024];
            if (extension == "flac") "fLaC"u8.CopyTo(bytes); else { bytes[0] = 0xff; bytes[1] = 0xfb; bytes[2] = 0x90; }
            await output.WriteAsync(bytes, ct); Started.TrySetResult();
            if (wait) await Tail.Task.WaitAsync(ct);
            await output.WriteAsync(new byte[512 * 1024], ct); return extension;
        }
    }
    private sealed class Rig : IDisposable
    {
        internal readonly TestPersistenceService Data = new();
        internal readonly Downloads Downloads;
        internal readonly FinderOnlineSearch Search;
        internal readonly OnlineSong Song;
        internal readonly TaskCompletionSource Saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CatalogPages _pages;
        internal Rig(Source first, Source backup, AudioQuality quality)
        {
            var sources = new MusicSources(first, backup, _ => { }, allowMp3: () => true, quality: () => quality);
            Downloads = new(_ => { }, Path.Combine(Data.DataDirectory, "queue.json"), sources.Track);
            Downloads.Changed += d => { if (d.State == DownloadState.Saved) Saved.TrySetResult(); };
            var actions = new DiscoveryActions(new EmptyLibrary(), Downloads, sources.Track, () => Data.DataDirectory, () => true);
            _pages = new(() => throw new InvalidOperationException("No catalog network in this test"), actions, _ => { }, () => false);
            Search = new(() => null, actions, _pages, () => true, () => throw new InvalidOperationException("No login in this test"));
            Song = Search.Remember(new(new CatalogSong("deezer:123", "Generated", [new("deezer:456", "Synthetic")], 120, false), null));
        }
        public void Dispose() { Search.Detach(); Downloads.Dispose(); _pages.Dispose(); Data.Dispose(); }
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    public async Task ExplicitPlayUsesTheExistingTransferBeforeCompletion_AndNeverImportsAPartFile(string extension)
    {
        var first = new Source("DeezLoad", extension); var backup = new Source("Music Hunters", extension);
        using var rig = new Rig(first, backup, extension == "mp3" ? AudioQuality.MP3 : AudioQuality.FLAC);
        try
        {
            await rig.Search.DownloadAsync([rig.Song], now: true, default);
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var track = await rig.Search.PreparePlaybackAsync(rig.Song, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.EndsWith("." + extension, track.FilePath); Assert.False(File.Exists(track.FilePath));
            Assert.False(track.IsRemoteStream); Assert.True(track.IsExternal);
            using var lease = ProgressiveTransfer.Acquire(track.FilePath); Assert.NotNull(lease); Assert.False(lease.Buffer.Complete);
            Assert.Equal(1, first.Calls); Assert.Equal(0, backup.Calls);
            Assert.Equal(1, first.Configured); Assert.Equal(1, backup.Configured);
            first.Tail.TrySetResult(); await rig.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(track.FilePath)); Assert.True(lease.Buffer.Complete);
            Assert.Empty(Directory.GetFiles(rig.Data.DataDirectory, "*.fmf.part"));
        }
        finally { first.Tail.TrySetResult(); backup.Tail.TrySetResult(); }
    }

    [Fact]
    public async Task FlacPreferenceNeverPlaysTheProvisionalMp3_WhileTheBackupIsStillBeingChecked()
    {
        var mp3 = new Source("DeezLoad", "mp3"); var flac = new Source("Music Hunters", "flac");
        using var rig = new Rig(mp3, flac, AudioQuality.FLAC);
        try
        {
            var pending = rig.Search.PreparePlaybackAsync(rig.Song, default);
            await mp3.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(700);
            Assert.False(pending.IsCompleted);
            mp3.Tail.TrySetResult();
            var ready = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.EndsWith(".flac", ready.FilePath); Assert.False(File.Exists(ready.FilePath));
            Assert.Equal(1, mp3.Calls); Assert.Equal(1, flac.Calls);
            flac.Tail.TrySetResult(); await rig.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(ready.FilePath)); Assert.Empty(Directory.GetFiles(rig.Data.DataDirectory, "*.mp3"));
        }
        finally { mp3.Tail.TrySetResult(); flac.Tail.TrySetResult(); }
    }

    [Fact]
    public async Task CancelledPlayWaitLeavesTheSameQueuedDownloadRunningToCompletion()
    {
        var first = new Source("DeezLoad", "flac"); var backup = new Source("Music Hunters", "flac");
        using var rig = new Rig(first, backup, AudioQuality.FLAC); using var stop = new CancellationTokenSource();
        try
        {
            var ready = rig.Search.PreparePlaybackAsync(rig.Song, stop.Token);
            await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);
            Assert.False(first.TransferToken.IsCancellationRequested); Assert.Single(rig.Downloads.Active);
            first.Tail.TrySetResult(); await rig.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, first.Calls); Assert.Equal(DownloadState.Saved, Assert.Single(rig.Downloads.History).State);
        }
        finally { first.Tail.TrySetResult(); }
    }
}
