using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services.Search;
using Xunit;

namespace Noctis.Tests;

public class MobileCatalogAlbumTests
{
    internal sealed class Source : IOnlineMusicSearch, IOnlineCatalog
    {
        public IOnlineCatalog Browser => this;
        public readonly OnlineAlbum Release = new("deezer:111", "Orbit", "Artist", "2026", TrackCount: 3);
        public Task<OnlineAlbumPage> Metadata = Task.FromResult(new OnlineAlbumPage(new("deezer:111", "Orbit", "Artist"), []));
        public int Reads, Searches, Prepares;
        public readonly List<(IReadOnlyList<OnlineSong> Songs, bool Now)> Saves = [];
        public bool CanDownload => true;
        public string Route => "synthetic";
        public IDownloadQueue Downloads { get; } = new EmptyQueue();
        public event EventHandler? DownloadsChanged { add { } remove { } }
        public void WarmUp() { }
        public void Retry() { }
        public void RequestSignIn() { }
        public void OpenArtist(OnlineArtist artist) { }
        public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => throw new Exception("Desktop navigation must not be used");
        public string? CachedArtwork(string url) => null;
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>(null);
        public OnlineDownloadStatus StatusOf(OnlineSong song) => OnlineDownloadStatus.None;
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct)
        { Saves.Add((songs, now)); return Task.FromResult("Queued"); }
        public Task<Track> PreparePlaybackAsync(OnlineSong song, bool keep, CancellationToken ct)
        { Assert.False(keep); Prepares++; return Task.FromResult(new Track { FilePath = "synthetic-stream.mp3", Title = song.Title }); }
        public Task<OnlineAlbumPage> ReadAlbumAsync(OnlineAlbum album, CancellationToken ct) { Reads++; return Metadata; } // deliberately ignores cancellation
        public Task<OnlineAlbumPage?> FindLibraryAlbumAsync(string artist, string title, IReadOnlyList<string> titles, CancellationToken ct)
            => Task.FromResult<OnlineAlbumPage?>(Metadata.IsCompletedSuccessfully ? Metadata.Result : null);
        public TaskCompletionSource? AfterFirst, Continue;
        public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, [EnumeratorCancellation] CancellationToken ct)
        {
            Searches++;
            yield return new(query, [], [], [Release], Continue is null);
            if (Continue is not null)
            {
                AfterFirst!.TrySetResult(); await Continue.Task;
                yield return new(query, [], [], [Release with { TrackCount = 4 }], true);
            }
        }
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

    [AvaloniaFact]
    public async Task ActualShellSearch_SeesHostServices_AndOpensNativeAlbumSkeletonWithoutAudio()
    {
        var source = new Source();
        var answer = new TaskCompletionSource<OnlineAlbumPage>(); source.Metadata = answer.Task;
        using var rig = MobileFixtures.MakeRig(onlineSearch: source);
        var online = Assert.IsType<OnlineResultsViewModel>(rig.Shell.Search.Online);
        Assert.Same(rig.Shell.Search, rig.Shell.Search);
        rig.Shell.Search.Query = "Orbit"; await online.SearchWork;
        var item = Assert.Single(online.Albums);
        online.OpenAlbumCommand.Execute(item);
        var page = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        Assert.True(page.ShowCatalogSkeleton); Assert.False(page.HasPlayableRows);
        Assert.Empty(rig.Library.Albums); Assert.Empty(rig.Library.TrackList);
        Assert.Empty(source.Saves); Assert.Equal(0, source.Prepares);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            MobileFixtures.Land(window);
            Assert.True(MobileFixtures.Named<ItemsControl>(MobileFixtures.Find<AlbumPage>(view), "CatalogSkeleton").IsVisible);
            answer.SetResult(new(source.Release, [new(new("deezer:1", "One", "Artist")), new(new("deezer:2", "Two", "Artist"))]));
            await page.CatalogLoad; window.UpdateLayout();
            Assert.False(page.ShowCatalogSkeleton); Assert.Equal(2, page.Tracks.Count);
            Assert.Empty(page.Album.Tracks); Assert.Empty(rig.Library.TrackList);
            Assert.All(page.Tracks, r => Assert.True(r.IsRemote));
        }
        finally { window.Close(); page.OnClosed(); }
    }

    [AvaloniaFact]
    public async Task MixedAlbum_PreservesAllRowsAndProviderOrder_PlayAndDownloadRemainExplicit()
    {
        var owned = MobileFixtures.Song("Owned"); owned.DiscNumber = 1; owned.TrackNumber = 7;
        var source = new Source();
        source.Metadata = Task.FromResult(new OnlineAlbumPage(source.Release, [
            new(new("deezer:1", "First", "Artist", Seconds: 90), 1, 1),
            new(new("deezer:2", "Owned", "Artist", Seconds: 90, LocalTrackId: owned.Id), 2, 1),
            new(new("deezer:3", "Last", "Artist", Seconds: 90), 2, 2)]));
        using var rig = MobileFixtures.MakeRig(tracks: [owned], onlineSearch: source);
        var page = new AlbumPageViewModel(rig.Shell, AlbumPageViewModel.OnlinePlaceholder(source.Release, source), source.Release);
        try
        {
            await page.CatalogLoad;
            Assert.Equal(new[] { "First", "Owned", "Last" }, page.Tracks.Select(r => r.Track.Title));
            Assert.Equal(new[] { 1, 2, 2 }, page.Tracks.Select(r => r.Disc));
            Assert.Equal(1, owned.DiscNumber); Assert.Equal(7, owned.TrackNumber);
            Assert.Same(owned, Assert.Single(page.Album.Tracks));
            Assert.Equal(1, rig.Library.TrackList.Count); Assert.Empty(source.Saves); Assert.Equal(0, source.Prepares);
            page.PlayCommand.Execute(null); await rig.Shell.Player.OnlinePreparation;
            Assert.Equal("First", rig.Shell.Player.CurrentTrack!.Title);
            Assert.Equal(new[] { "Owned", "Last" }, rig.Shell.Player.UpNext.Select(t => t.Title));
            Assert.Equal(1, source.Prepares); Assert.Empty(source.Saves);
            page.ShowTrackMenuCommand.Execute(page.Tracks[1]);
            Assert.Same(owned, Assert.Single(rig.Shell.Sheet!.Tracks));
            page.ShowTrackMenuCommand.Execute(page.Tracks[2]);
            Assert.Equal(new[] { "Play", "Download now", "Add to downloads" }, rig.Shell.OptionsMenu!.Items.Select(i => i.Label));
            rig.Shell.OptionsMenu.Items[1].PickCommand.Execute(null);
            Assert.Equal("deezer:3", Assert.Single(Assert.Single(source.Saves).Songs).Id);
            Assert.True(source.Saves[0].Now);
            Assert.Single(rig.Library.TrackList);
        }
        finally { page.OnClosed(); }
    }

    [AvaloniaFact]
    public async Task FailedAlbumCanRetry_AndAnAnswerAfterBackCannotResurrectItsRows()
    {
        var source = new Source { Metadata = Task.FromException<OnlineAlbumPage>(new IOException("No network")) };
        using var rig = MobileFixtures.MakeRig(onlineSearch: source);
        var page = new AlbumPageViewModel(rig.Shell, AlbumPageViewModel.OnlinePlaceholder(source.Release, source), source.Release);
        await page.CatalogLoad;
        Assert.True(page.HasCatalogError); Assert.Equal("Couldn't load songs", page.CatalogMessage);
        var late = new TaskCompletionSource<OnlineAlbumPage>(); source.Metadata = late.Task;
        page.RetryCatalogCommand.Execute(null); Assert.True(page.IsCatalogLoading);
        page.OnClosed();
        late.SetResult(new(source.Release, [new(new("deezer:1", "Late", "Artist"))]));
        await page.CatalogLoad;
        Assert.Empty(page.Tracks); Assert.Equal(2, source.Reads); Assert.Empty(source.Saves);
    }

    [Fact]
    public async Task ProgressiveSearchKeepsAlbumTileIdentity_AndScopeChangesNeverRepeatMetadataSearch()
    {
        var source = new Source { AfterFirst = new(), Continue = new() };
        using var vm = new OnlineResultsViewModel(source, a => a(), _ => { }, () => { }, (_, _) => Task.CompletedTask) { OpenAlbumPage = _ => { } };
        vm.Search("Orbit"); await source.AfterFirst.Task;
        var first = Assert.Single(vm.Albums);
        source.Continue.SetResult(); await vm.SearchWork;
        Assert.Same(first, Assert.Single(vm.Albums)); Assert.Equal(4, first.Album.TrackCount);
        vm.Scope = SearchScope.Songs; Assert.Empty(vm.Albums);
        vm.Scope = SearchScope.Albums; Assert.Single(vm.Albums); Assert.True(vm.HasResults);
        Assert.Equal(1, source.Searches); Assert.Empty(source.Saves); Assert.Equal(0, source.Prepares);
    }
}
