using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Search;
using Xunit;

namespace Noctis.Tests;

public class MobileOnlineQueueTests
{
    private sealed class Downloads : IDownloadQueue
    {
        public IReadOnlyList<DownloadEntry> Snapshot() => [];
        public bool IsPaused => false;
        public event EventHandler? Changed { add { } remove { } }
        public void SetPaused(bool value) { }
        public void RetryFailed() { }
        public void StartHeld() { }
        public void Clear() { }
        public void DownloadNext(DownloadEntry value) { }
        public void Remove(DownloadEntry value) { }
    }
    private sealed class Source(string folder) : IOnlineMusicSearch
    {
        public readonly List<(OnlineSong Song, TaskCompletionSource<Track> Reply)> Requests = [];
        public readonly List<string> Calls = [];
        public readonly Dictionary<string, string> Cached = [];
        public string[] Window = [];
        public bool Hold;
        public Action? RequestAdded;
        public bool CanDownload { get; set; } = true;
        public string Route => "test";
        public IDownloadQueue Downloads { get; } = new Downloads();
        public event EventHandler? DownloadsChanged;
        /// <summary>#284: what every song's download reports (None unless a test sets it).</summary>
        public OnlineDownloadStatus Status = OnlineDownloadStatus.None;
        public void Report(OnlineDownloadStatus status) { Status = status; DownloadsChanged?.Invoke(this, EventArgs.Empty); }
        public void WarmUp() { }
        public void Retry() { }
        public void RequestSignIn() { }
        public void OpenArtist(OnlineArtist artist) { }
        public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => Task.CompletedTask;
        public string? CachedArtwork(string url) => null;
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>(null);
        public OnlineDownloadStatus StatusOf(OnlineSong song) => Status;
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct) => Task.FromResult("Queued");
        public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
        public Track Ready(OnlineSong song)
        {
            var path = Path.Combine(folder, song.Id + ".mp3");
            File.WriteAllBytes(path, [0]); Cached[song.Id] = path;
            return new() { FilePath = path, Title = song.Title, Duration = TimeSpan.FromSeconds(60), IsExternal = true };
        }
        public Task<Track> PreparePlaybackAsync(OnlineSong song, bool keep, CancellationToken ct)
        {
            Assert.False(keep); Calls.Add("play:" + song.Id);
            var reply = new TaskCompletionSource<Track>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((song, reply));
            RequestAdded?.Invoke();
            if (!Hold) reply.SetResult(Ready(song));
            return reply.Task; // deliberately ignores cancellation: stale answers must remain harmless
        }
        public string? CompletedPlaybackPath(OnlineSong song) => Cached.GetValueOrDefault(song.Id);
        public bool RestoreQueueSong(OnlineSong song) => true;
        public void SetPlaybackWindow(string? currentPath, IReadOnlyList<OnlineSong> songs) => Window = songs.Select(s => s.Id).ToArray();
        public Task PrefetchAsync(OnlineSong song, bool keep, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls.Add("fetch:" + song.Id); return Task.CompletedTask; }
        public Task AskAheadAsync(OnlineSong song, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls.Add("ask:" + song.Id); return Task.CompletedTask; }
    }
    private sealed class Rig : IDisposable
    {
        public readonly PersistenceService Data = new(Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N")));
        public readonly FakeAudioPlayer Audio = new();
        public readonly FakeLibraryService Library = new();
        public readonly OnlineAccess Network = new();
        public readonly Source Source;
        public readonly NowPlayingViewModel Vm;
        public Rig()
        {
            Directory.CreateDirectory(Data.DataDirectory);
            Source = new(Data.DataDirectory);
            Vm = new(Audio, Library, Data, marshal: a => a(), onlineAccess: Network)
            { OnlineSearch = Source, OnlineDelay = (_, _) => Task.CompletedTask };
        }
        public void Dispose() { Vm.Dispose(); try { Directory.Delete(Data.DataDirectory, true); } catch { } }
    }
    private static OnlineSong[] Songs(int n = 7) => Enumerable.Range(0, n).Select(i => new OnlineSong(i.ToString(), "Pick " + i, "Artist")).ToArray();

    [Fact]
    public async Task SelectedRowKeepsTheRestOfTheList_CurrentRequestComesBeforeBoundedAheadWork()
    {
        using var r = new Rig(); var songs = Songs();
        r.Vm.PlayOnlineTracks(r.Source, songs, 1, "For You");
        await r.Vm.OnlinePreparation; await r.Vm.OnlineAheadWork;
        Assert.Equal("Pick 1", r.Vm.CurrentTrack!.Title);
        Assert.Equal(songs.Skip(2).Select(s => s.Title), r.Vm.UpNext.Select(t => t.Title));
        Assert.Equal(new[] { "play:1", "fetch:2", "fetch:3", "ask:4", "ask:5" }, r.Source.Calls);
        Assert.Equal(new[] { "1", "2", "3" }, r.Source.Window);
        Assert.All(r.Audio.PlayedPaths.Concat(r.Audio.PreparedPaths), p => Assert.False(string.IsNullOrEmpty(p)));
    }

    /// <summary>Owner #284, the PC's status on the phone: while a song is found, its step (the magnifier) stands in Play's
    /// place; once it plays, how much has arrived is the progress line's lighter part, until all of it is here.</summary>
    [Fact]
    public async Task AStreamedSong_ShowsItsStep_ThenHowMuchHasArrived()
    {
        using var r = new Rig(); r.Source.Hold = true; var songs = Songs(2);
        r.Vm.PlayOnlineTracks(r.Source, songs, 0);
        Assert.True(r.Vm.IsPreparingOnline);
        Assert.Equal(OnlineDownloadStep.Asking, r.Vm.OnlineStep);           // the magnifier
        Assert.Equal(0, r.Vm.DownloadedFraction);
        r.Source.Report(new(OnlineDownloadState.Downloading, 0, Step: OnlineDownloadStep.Switching));
        Assert.Equal(OnlineDownloadStep.Switching, r.Vm.OnlineStep);
        r.Source.Report(new(OnlineDownloadState.Downloading, 0.3, Step: OnlineDownloadStep.Receiving));
        r.Source.Requests[0].Reply.SetResult(r.Source.Ready(songs[0])); await r.Vm.OnlinePreparation;
        Assert.False(r.Vm.IsPreparingOnline);
        Assert.Equal(OnlineDownloadStep.None, r.Vm.OnlineStep);             // Play is back
        Assert.Equal(0.3, r.Vm.DownloadedFraction, 3);
        Assert.True(r.Vm.IsStreamArriving);
        r.Source.Report(new(OnlineDownloadState.Downloading, 0.8, Step: OnlineDownloadStep.Receiving));
        Assert.Equal(0.8, r.Vm.DownloadedFraction, 3);
        r.Source.Report(new(OnlineDownloadState.Streamed, 1));
        Assert.Equal(1, r.Vm.DownloadedFraction);
        Assert.False(r.Vm.IsStreamArriving);
        r.Source.Report(new(OnlineDownloadState.Downloading, 0.1, Step: OnlineDownloadStep.Receiving));
        Assert.Equal(1, r.Vm.DownloadedFraction);                           // arrived: no longer followed
    }

    [Fact]
    public async Task RepeatedTapIsDeduplicated_AndLateOldAnswersCannotPlayOrReportErrors()
    {
        using var r = new Rig(); r.Source.Hold = true; var songs = Songs();
        r.Vm.PlayOnlineTracks(r.Source, songs, 0); var a = r.Vm.OnlinePreparation;
        r.Vm.PlayOnlineTracks(r.Source, songs, 0); Assert.Single(r.Source.Requests);
        r.Vm.PlayOnlineTracks(r.Source, songs, 1); var b = r.Vm.OnlinePreparation;
        r.Vm.PlayOnlineTracks(r.Source, songs, 0); var latest = r.Vm.OnlinePreparation;
        Assert.Empty(r.Audio.PlayedPaths);
        var wanted = r.Source.Ready(songs[0]); r.Source.Requests[2].Reply.SetResult(wanted);
        await latest;
        r.Source.Requests[0].Reply.SetResult(new() { FilePath = "old-a.mp3" });
        r.Source.Requests[1].Reply.SetException(new IOException("old failure"));
        await Task.WhenAll(a, b);
        Assert.Equal(new[] { wanted.FilePath }, r.Audio.PlayedPaths);
        Assert.Equal("", r.Vm.ErrorText);
    }

    [Fact]
    public async Task PauseWhilePreparingHoldsPosition_AndNextUsesTheSameNativeQueue()
    {
        using var r = new Rig(); r.Source.Hold = true; var songs = Songs(3);
        r.Vm.PlayOnlineTracks(r.Source, songs, 0);
        Assert.True(r.Vm.ShowPause);
        r.Vm.TogglePlayPauseCommand.Execute(null); Assert.False(r.Vm.ShowPause);
        r.Audio.RaisePositionChanged(TimeSpan.FromSeconds(30)); Assert.Equal(TimeSpan.Zero, r.Vm.Position);
        r.Source.Requests[0].Reply.SetResult(r.Source.Ready(songs[0])); await r.Vm.OnlinePreparation;
        Assert.Empty(r.Audio.PlayedPaths); Assert.False(r.Vm.IsPlaying);
        r.Vm.TogglePlayPauseCommand.Execute(null); Assert.Single(r.Audio.PlayedPaths);
        r.Vm.NextCommand.Execute(null); Assert.Equal("Pick 1", r.Vm.CurrentTrack!.Title);
        Assert.True(r.Vm.IsPreparingOnline);
        r.Source.Requests[1].Reply.SetResult(r.Source.Ready(songs[1])); await r.Vm.OnlinePreparation;
        Assert.Equal(2, r.Audio.PlayedPaths.Count); Assert.Single(r.Vm.UpNext);
    }

    [Fact]
    public async Task OwnedPositionsCountTowardBothWindows_TheyNeverExtendTheLookahead()
    {
        using var r = new Rig(); var songs = Songs();
        var tracks = songs.Select(s => OnlineStandIn.Create(r.Source, s, false)).ToArray();
        for (var i = 0; i < 3; i++) tracks[i] = new() { Title = "Local " + i, FilePath = "content://music/" + i };
        r.Vm.PlayTracks(tracks, 0); await r.Vm.OnlineAheadWork;
        Assert.Equal(new[] { "ask:3", "ask:4" }, r.Source.Calls);
        Assert.Empty(r.Source.Window);
    }

    [Fact]
    public async Task ANewSelectionDuringTheSettleWindowNeverStartsOldPrefetches()
    {
        using var r = new Rig(); var songs = Songs(10);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Vm.OnlineDelay = (_, ct) => gate.Task.WaitAsync(ct);
        r.Vm.PlayOnlineTracks(r.Source, songs, 0); await r.Vm.OnlinePreparation;
        r.Vm.PlayOnlineTracks(r.Source, songs, 5); await r.Vm.OnlinePreparation;
        Assert.DoesNotContain(r.Source.Calls, c => c.StartsWith("fetch:"));
        gate.SetResult(); await r.Vm.OnlineAheadWork;
        Assert.Equal(new[] { "fetch:6", "fetch:7", "ask:8", "ask:9" }, r.Source.Calls.Where(c => !c.StartsWith("play:")));
        Assert.Equal(new[] { "5", "6", "7" }, r.Source.Window);
    }

    [Fact]
    public async Task RejectingAnUpcomingRecommendationRemovesItFromPlaybackAndRepeat()
    {
        using var r = new Rig(); var songs = Songs(3);
        r.Vm.PlayOnlineTracks(r.Source, songs, 0, "For You"); await r.Vm.OnlinePreparation;
        r.Vm.RemoveOnlineRecommendation(r.Source, songs[1], false);
        Assert.Equal("Pick 2", Assert.Single(r.Vm.UpNext).Title);
        r.Vm.NextCommand.Execute(null); await r.Vm.OnlinePreparation;
        Assert.Equal("Pick 2", r.Vm.CurrentTrack!.Title);
        await r.Vm.SaveStateAsync();
        var saved = await r.Data.LoadQueueStateAsync();
        Assert.DoesNotContain(saved!.MobileOnlineTracks!.Values, s => s.Song.Id == songs[1].Id);
    }

    [Fact]
    public async Task ReopeningGateAndQueuedCurrentStart_DoNotAskAheadTwice()
    {
        using var r = new Rig(); r.Source.Hold = true;
        var posted = new List<Action>();
        using var vm = new NowPlayingViewModel(r.Audio, r.Library, r.Data, marshal: a => posted.Add(a), onlineAccess: r.Network)
        { OnlineDelay = (_, _) => Task.CompletedTask };
        vm.PlayOnlineTracks(r.Source, Songs(), 0);
        r.Network.SetOffline(true); r.Network.SetOffline(false);
        // A recovery notification can arrive before the asynchronous current-start callback.
        var work = posted.ToArray(); posted.Clear();
        foreach (var action in work.Reverse()) action();
        await vm.OnlineAheadWork;
        Assert.Equal(new[] { "play:0", "fetch:1", "fetch:2", "ask:3", "ask:4" }, r.Source.Calls);
        vm.Dispose();
        await vm.OnlinePreparation;
        foreach (var action in posted.ToArray()) action();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineDuringPreparation_KeepsSelectionAndPauseIntent_ThenResumes(bool quickReturn)
    {
        using var r = new Rig(); r.Source.Hold = true; var songs = Songs(1);
        r.Vm.PlayOnlineTracks(r.Source, songs, 0);
        r.Vm.TogglePlayPauseCommand.Execute(null);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Source.RequestAdded = () => retried.TrySetResult();
        r.Network.SetOffline(true);
        if (quickReturn) r.Network.SetOffline(false);
        r.Source.Requests[0].Reply.SetException(new OfflineModeException());
        if (!quickReturn) r.Network.SetOffline(false);
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Pick 0", r.Vm.CurrentTrack!.Title);
        Assert.Equal("", r.Vm.ErrorText);
        r.Source.Requests[1].Reply.SetResult(r.Source.Ready(songs[0]));
        await r.Vm.OnlinePreparation;
        Assert.Empty(r.Audio.PlayedPaths);
        Assert.False(r.Vm.IsPlaying);
        Assert.Equal(TimeSpan.Zero, r.Vm.Position);
        r.Vm.TogglePlayPauseCommand.Execute(null);
        Assert.Single(r.Audio.PlayedPaths);
    }

    [Fact]
    public async Task OfflineHoldsCurrentAndAheadWork_RestartRestoresMetadataPausedWithoutFetching()
    {
        using var r = new Rig(); r.Network.SetOffline(true); var songs = Songs(3);
        r.Vm.PlayOnlineTracks(r.Source, songs, 0);
        Assert.Empty(r.Source.Calls); Assert.True(r.Vm.IsPreparingOnline);
        await r.Vm.SaveStateAsync();
        using var restored = new NowPlayingViewModel(new FakeAudioPlayer(), r.Library, r.Data, marshal: a => a(), onlineAccess: r.Network) { OnlineSearch = r.Source };
        await restored.RestoreStateAsync();
        Assert.Equal("Pick 0", restored.CurrentTrack!.Title); Assert.Equal(2, restored.UpNext.Count);
        Assert.False(restored.IsPlaying); Assert.Empty(r.Source.Calls);
        r.Network.SetOffline(false); await r.Vm.OnlinePreparation;
        Assert.Equal("play:0", r.Source.Calls[0]);
    }
}
