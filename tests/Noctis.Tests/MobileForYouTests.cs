using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services.Search;
using Xunit;

namespace Noctis.Tests;

public class MobileForYouTests
{
    private sealed class Picks : IOnlineDiscovery
    {
        public IReadOnlyList<RecommendationPage> Pages = [new("first", Enumerable.Range(0, 8).Select(i => new OnlineSong(i.ToString(), "Pick " + i, "Artist " + i, ArtworkUrl: "https://example.test/" + i)).ToArray())];
        public readonly HashSet<string> Hidden = [];
        public readonly List<string> Shown = [];
        public Func<bool, CancellationToken, Task<IReadOnlyList<RecommendationPage>>>? Load;
        public Task<IReadOnlyList<RecommendationPage>> LoadAsync(bool refresh, CancellationToken ct) => Load?.Invoke(refresh, ct)
            ?? Task.FromResult<IReadOnlyList<RecommendationPage>>(Pages.Select(p => p with { Songs = p.Songs.Where(s => !Hidden.Contains(s.Id)).ToArray() }).ToArray());
        public Task RecordAsync(string page, OnlineSong song, RecommendationAction action) => Task.CompletedTask;
        public Task ShownAsync(string page, IReadOnlyList<string> ids) { Shown.AddRange(ids); return Task.CompletedTask; }
        public Task<string?> HideAsync(string page, OnlineSong song, bool artist = false) { Hidden.Add(song.Id); return Task.FromResult<string?>(song.Id); }
        public Task<bool> UndoAsync(string receipt) => Task.FromResult(Hidden.Remove(receipt));
    }
    private sealed class Queue : IDownloadQueue
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
    private sealed class Online : IOnlineMusicSearch
    {
        public int Audio, Art;
        public readonly List<bool> Saves = [];
        public bool CanDownload => true;
        public string Route => "test";
        public IDownloadQueue Downloads { get; } = new Queue();
        public event EventHandler? DownloadsChanged { add { } remove { } }
        public void WarmUp() { }
        public void Retry() { }
        public void RequestSignIn() { }
        public void OpenArtist(OnlineArtist artist) { }
        public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => Task.CompletedTask;
        public string? CachedArtwork(string url) => null;
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) { Art++; return Task.FromResult<string?>(null); }
        public OnlineDownloadStatus StatusOf(OnlineSong song) => OnlineDownloadStatus.None;
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct) { Saves.Add(now); return Task.FromResult("Queued"); }
        public Task<Track> PreparePlaybackAsync(OnlineSong song, bool keep, CancellationToken ct)
        { Audio++; Assert.False(keep); return Task.FromResult(MobileFixtures.Song(song.Title)); }
        public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
    }

    [Fact]
    public async Task HomePicksAreMetadataOnly_ArtworkLoadsOnlyForVisibleRows_AndActionsAreExplicit()
    {
        using var rig = MobileFixtures.MakeRig();
        var source = new Picks(); var online = new Online();
        using var vm = new ForYouViewModel(rig.Shell, source, online, a => a());
        await vm.LoadAsync();
        Assert.Equal(5, vm.PreviewRows.Count()); Assert.Equal(8, vm.Actions.Songs.Count);
        Assert.Equal(0, online.Audio); Assert.Empty(online.Saves); Assert.Equal(0, online.Art);
        vm.RecordVisible("0"); vm.RecordVisible("0");
        Assert.Equal(["0"], source.Shown);
        await vm.Actions.PlayCommand.ExecuteAsync(vm.Actions.Songs[0]);
        Assert.Equal(1, online.Audio);
        vm.MoreCommand.Execute(vm.Actions.Songs[1]);
        Assert.Equal(new[] { "Download now", "Add to downloads", "Not for me", "Don't recommend artist" }, rig.Shell.OptionsMenu!.Items.Select(i => i.Label));
        rig.Shell.OptionsMenu.Items[0].PickCommand.Execute(null);
        Assert.True(Assert.Single(online.Saves));
    }

    [Fact]
    public async Task NotForMeSurvivesRefresh_StopsThePlayingPick_AndUndoRestoresIt()
    {
        using var rig = MobileFixtures.MakeRig();
        var source = new Picks(); var online = new Online();
        using var vm = new ForYouViewModel(rig.Shell, source, online, a => a());
        await vm.LoadAsync(); var row = vm.Actions.Songs[0];
        await vm.Actions.PlayCommand.ExecuteAsync(row);
        Assert.True(rig.Shell.Player.IsPlaying);
        await vm.HideAsync(row, false);
        Assert.False(rig.Shell.Player.IsPlaying);
        Assert.DoesNotContain(vm.Actions.Songs, s => s.Song.Id == row.Song.Id);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Actions.Songs, s => s.Song.Id == row.Song.Id);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Contains(vm.Actions.Songs, s => s.Song.Id == row.Song.Id);
        Assert.False(vm.HasUndo);
    }

    [Fact]
    public async Task FailedRefreshKeepsSavedRows_AndLateOldAnswersCannotReplaceTheNewPage()
    {
        using var rig = MobileFixtures.MakeRig(); var source = new Picks();
        using var vm = new ForYouViewModel(rig.Shell, source, new Online(), a => a());
        await vm.LoadAsync(); var first = vm.Actions.Songs[0];
        source.Load = (_, _) => throw new IOException("network gone");
        await vm.LoadAsync(true);
        Assert.Same(first, vm.Actions.Songs[0]); Assert.Equal("Showing saved picks", vm.Status);
        var old = new TaskCompletionSource<IReadOnlyList<RecommendationPage>>();
        source.Load = (_, _) => old.Task;
        var waiting = vm.LoadAsync(true);
        source.Load = (_, _) => Task.FromResult<IReadOnlyList<RecommendationPage>>([new("new", [new("new", "New song", "Artist")])]);
        await vm.LoadAsync(true);
        old.SetResult(source.Pages); await waiting;
        Assert.Equal("new", Assert.Single(vm.Actions.Songs).Song.Id);
        Assert.False(vm.IsLoading);
    }

    [AvaloniaFact]
    public async Task NativeShelfAndFullPageBindRealCommandsAndDoNotDuplicateDownloads()
    {
        using var rig = MobileFixtures.MakeRig(); var source = new Picks(); var online = new Online();
        using var vm = new ForYouViewModel(rig.Shell, source, online, a => a());
        await vm.LoadAsync(); MobileFixtures.EnsurePhonePalette();
        var view = new ForYouView { DataContext = vm, PreviewOnly = true };
        var window = new Window { Content = view, Width = 412, Height = 500 }; window.Show(); window.UpdateLayout();
        Assert.True(view.FindControl<ItemsControl>("Preview")!.IsVisible);
        Assert.False(view.FindControl<ItemsControl>("FullPicks")!.IsVisible);
        window.Content = new ForYouPage { DataContext = vm }; window.UpdateLayout();
        Assert.Empty(online.Saves); Assert.Equal(0, online.Audio);
        window.Close();
    }
}
