using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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
        public int Loads;
        public Task<IReadOnlyList<RecommendationPage>> LoadAsync(bool refresh, CancellationToken ct)
        {
            Loads++;
            return Load?.Invoke(refresh, ct) ?? Task.FromResult<IReadOnlyList<RecommendationPage>>(Pages.Select(p => p with { Songs = p.Songs.Where(s => !Hidden.Contains(s.Id)).ToArray() }).ToArray());
        }
        public Task RecordAsync(string page, OnlineSong song, RecommendationAction action) => Task.CompletedTask;
        public Task ShownAsync(string page, IReadOnlyList<string> ids) { Shown.AddRange(ids); return Task.CompletedTask; }
        public Task<string?> HideAsync(string page, OnlineSong song, bool artist = false) { Hidden.Add(song.Id); return Task.FromResult<string?>(song.Id); }
        public Task<bool> UndoAsync(string receipt)
        {
            if (receipt == "pages" && Before is { } before) { Pages = before; Before = null; return Task.FromResult(true); }
            return Task.FromResult(Hidden.Remove(receipt));
        }
        public IReadOnlyList<RecommendationPage>? Before;
        public Task<string?> RemovePageAsync(string page) { Before = Pages; Pages = Pages.Where(p => p.Id != page).ToArray(); return Task.FromResult<string?>("pages"); }
        public Task<string?> ClearPagesAsync() { Before = Pages; Pages = []; return Task.FromResult<string?>("pages"); }
    }

    private static RecommendationPage PageOf(string id, string name) =>
        new(id, [new(id + "1", name + " 1", name + " artist"), new(id + "2", name + " 2", name + " artist")]);

    private static async Task Until(Func<bool> done) { for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10); }

    /// <summary>Owner #283 (the computer's #286): a For You page that runs out goes on with the next page, from its first song;
    /// with no next page a fresh one plays. Any other queue running out just stops.</summary>
    [Fact]
    public async Task ThePageRunningOut_PlaysTheNextPage_ThenAFreshOne_OtherQueuesJustStop()
    {
        using var rig = MobileFixtures.MakeRig();
        var source = new Picks { Pages = [PageOf("first", "First"), PageOf("second", "Second")] }; var online = new Online();
        using var vm = new ForYouViewModel(rig.Shell, source, online, a => a());
        var player = rig.Shell.Player;
        await vm.LoadAsync();
        await vm.Actions.PlayCommand.ExecuteAsync(vm.Actions.Songs[1]); await player.OnlinePreparation;
        Assert.Equal("First 2", player.CurrentTrack?.Title);
        player.NextCommand.Execute(null);
        await Until(() => player.CurrentTrack?.Title == "Second 1"); await player.OnlinePreparation;
        Assert.Equal("Second 1", player.CurrentTrack?.Title);
        Assert.Equal(new[] { "Second 2" }, player.UpNext.Select(t => t.Title));
        Assert.Equal("2 / 2", vm.PageLabel);

        source.Load = (refresh, _) => Task.FromResult<IReadOnlyList<RecommendationPage>>(refresh ? [PageOf("fresh", "Fresh"), .. source.Pages] : source.Pages);
        player.NextCommand.Execute(null); await player.OnlinePreparation;   // Second 2
        player.NextCommand.Execute(null);
        await Until(() => player.CurrentTrack?.Title == "Fresh 1"); await player.OnlinePreparation;
        Assert.Equal("Fresh 1", player.CurrentTrack?.Title);   // no kept page after it: a fresh page

        var loads = source.Loads;
        var song = MobileFixtures.Song("Library song"); rig.Library.TrackList.Add(song);
        player.PlayTracks([song], 0, "Songs");
        player.NextCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(loads, source.Loads);
        Assert.False(player.IsPlaying);
    }

    /// <summary>Owner #283: a fresh page that could not be built is not the same page played again.</summary>
    [Fact]
    public async Task TheLastPageRunningOut_WithNoNewPage_DoesNotReplayIt()
    {
        using var rig = MobileFixtures.MakeRig();
        var source = new Picks { Pages = [PageOf("only", "Only")] }; var online = new Online();
        using var vm = new ForYouViewModel(rig.Shell, source, online, a => a());
        var player = rig.Shell.Player;
        await vm.LoadAsync();
        await vm.Actions.PlayCommand.ExecuteAsync(vm.Actions.Songs[1]); await player.OnlinePreparation;
        var loads = source.Loads;
        player.NextCommand.Execute(null);
        await Until(() => source.Loads > loads); await Task.Delay(50);
        Assert.False(player.IsPlaying);
        Assert.Equal(1, online.Audio);
    }

    /// <summary>Owner #283 (the computer's #248): Remove this page and Clear all pages, each with Undo.</summary>
    [Fact]
    public async Task RemoveThisPage_AndClearAllPages_EachUndo()
    {
        using var rig = MobileFixtures.MakeRig();
        var source = new Picks { Pages = [PageOf("first", "First"), PageOf("second", "Second")] };
        using var vm = new ForYouViewModel(rig.Shell, source, new Online(), a => a());
        await vm.LoadAsync();
        vm.NextPageCommand.Execute(null);
        Assert.Equal("Second 1", vm.Actions.Songs[0].Title);
        vm.PageOptionsCommand.Execute(null);
        Assert.Equal(new[] { "Remove this page", "Clear all pages" }, rig.Shell.OptionsMenu!.Items.Select(i => i.Label));
        await vm.RemovePagesAsync(all: false);
        Assert.Equal("First 1", vm.Actions.Songs[0].Title);
        Assert.False(vm.HasPages); Assert.True(vm.HasUndo);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.True(vm.HasPages); Assert.False(vm.HasUndo);
        await vm.RemovePagesAsync(all: true);
        Assert.False(vm.HasSongs); Assert.True(vm.HasUndo);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.True(vm.HasSongs); Assert.Equal("1 / 2", vm.PageLabel);
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
        public int Audio, Art, Retries;
        public readonly List<bool> Saves = [];
        public bool CanDownload => true;
        public string Route => "test";
        public IDownloadQueue Downloads { get; } = new Queue();
        public event EventHandler? DownloadsChanged { add { } remove { } }
        public void WarmUp() { }
        public void Retry() { Retries++; }
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
        Assert.Equal(new[] { "Download now", "Add to downloads", "Not for me", "Hide artist" }, rig.Shell.OptionsMenu!.Items.Select(i => i.Label));
        rig.Shell.OptionsMenu.Items[0].PickCommand.Execute(null);
        Assert.True(Assert.Single(online.Saves));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, online.Retries); // a changed VPN/proxy gets a new metadata attempt
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
        Assert.NotEqual(row.Song.Title, rig.Shell.Player.CurrentTrack?.Title); // the native list continues, never the rejected pick
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
        var page = new ForYouPage { DataContext = vm };
        window.Content = page; window.UpdateLayout();
        var full = page.GetVisualDescendants().OfType<ForYouView>().Single().FindControl<ItemsControl>("FullPicks")!;
        Assert.True(full.Bounds.Width > 0 && full.Bounds.Width <= window.ClientSize.Width);
        Assert.All(full.GetVisualDescendants().OfType<Button>().Where(b => b.Command == vm.MoreCommand),
            b => Assert.InRange(b.TranslatePoint(default, page)!.Value.X + b.Bounds.Width, 1, window.ClientSize.Width));
        Assert.Empty(online.Saves); Assert.Equal(0, online.Audio);
        window.Close();
    }

    [Fact]
    public async Task FirstScanFillsAnEmptyShelfOnce_WithoutRefetchingOnEveryLibraryEvent()
    {
        using var rig = MobileFixtures.MakeRig(); var source = new Picks { Pages = [] }; var online = new Online();
        using var vm = new ForYouViewModel(rig.Shell, source, online, a => a());
        await vm.LoadAsync(); Assert.False(vm.HasSongs);
        source.Pages = [new("new", [new("1", "A pick", "An artist")])];
        rig.Library.TrackList.Add(MobileFixtures.Song("Local song"));
        rig.Library.RaiseLibraryUpdated();
        for (var i = 0; i < 100 && !vm.HasSongs; i++) await Task.Delay(10);
        Assert.True(vm.HasSongs); Assert.Equal(2, source.Loads);
        rig.Library.RaiseLibraryUpdated(); rig.Library.RaiseLibraryUpdated();
        Assert.Equal(2, source.Loads); Assert.Equal(0, online.Audio); Assert.Empty(online.Saves);
    }
}
