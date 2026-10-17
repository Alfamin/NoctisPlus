using Avalonia.Threading;
using FreeMusicFinder;
using Noctis.ViewModels;

namespace FreeMusicFinder.Tests;

internal static class NativeForYouTests
{
    internal static void Run()
    {
        Check.Section("native For you shelf: saved picks, downloads, song-only dismissal and Undo");
        var folder = Check.NewFolder("native-for-you");
        var library = new SpotifyPlaylistTests.Library();
        library.Add(folder, "Already owned", "North Star");
        var songs = new[] { "Already owned", "New first song", "New second song" }.Select((title, i) =>
            new DiscoverSong(new(DiscoveryTests.Song("deezer:" + (100 + i), title), DiscoveryTests.Orbit), 1, "Because you play North Star")).ToList();
        var now = DateTimeOffset.UtcNow;
        new ForYouCache(Path.Combine(folder, "for-you-last.json")).Write(new(1, now, now.AddDays(1), new(songs, [], "")));
        var http = new DiscoveryTests.Http { Offline = true };
        using var catalog = DiscoveryTests.Catalog(Path.Combine(folder, "catalog"), http);
        using var downloads = new Downloads(_ => { });
        var loginRequests = 0;
        var actions = new DiscoveryActions(library, downloads, _ => throw new Exception("No transfer is allowed in this fixture"), () => folder, () => false);
        using var pages = new CatalogPages(() => catalog, actions, _ => { }, () => true, library: () => null);
        var online = new FinderOnlineSearch(() => catalog, actions, pages, () => false, () => loginRequests++);
        using var source = new ForYouSource(() => catalog, actions, _ => [], library, null, Path.Combine(folder, "hidden.json"));
        using var vm = new ForYouShelfViewModel(source, online, actions, TimeSpan.FromMilliseconds(120));
        vm.SetActive(true);
        Check.Until(() => !vm.IsLoading && vm.Picks.Count == 2, "native shelf loads cached picks and excludes already-owned recordings");
        Check.Equal(0, http.Calls.Count, "showing saved picks does not perform catalog searches");
        Check.Equal(0, downloads.History.Count, "opening For you never queues or downloads audio");
        var first = vm.Picks[0];
        var signIn = vm.DownloadCommand.ExecuteAsync(first);
        Check.Until(() => signIn.IsCompleted, "download click completes the sign-in offer");
        signIn.GetAwaiter().GetResult();
        Check.Equal(1, loginRequests, "signed-out Download now opens the native sign-in flow");
        Check.Equal(0, downloads.History.Count, "sign-in offer does not create a fake pending download");
        vm.NotForMeCommand.Execute(first);
        Check.True(vm.HasUndo && vm.Picks.Count == 1 && source.Hidden.HidesSong(first.Pick.Track.Song), "Not for me hides just the chosen song immediately");
        Check.False(source.Hidden.HidesSong(vm.Picks[0].Pick.Track.Song), "another song by the same artist stays recommended");
        vm.UndoCommand.Execute(null);
        Check.True(!vm.HasUndo && vm.Picks[0] == first && !source.Hidden.HidesSong(first.Pick.Track.Song), "Undo restores the same card and removes the persistent preference");
        vm.NotForMeCommand.Execute(first);
        Check.Until(() => !vm.HasUndo, "Undo expires after its short window");
        Check.True(source.Hidden.HidesSong(first.Pick.Track.Song), "expiry leaves only that song dismissed");
        vm.SetActive(false);
        vm.SetActive(true);
        Check.Equal(1, vm.Picks.Count, "leaving and returning preserves the page instead of moving cards");
        Check.Equal(0, http.Calls.Count, "returning to the shelf does not rebuild recommendations or delay Search");
        http.Override = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new Exception("unreachable"); };
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        Check.Until(() => http.Calls.Count > 0, "explicit Refresh starts metadata work");
        vm.SetActive(false);
        Check.Until(() => refresh.IsCompleted, "typing or leaving Search cancels recommendation requests promptly");
        refresh.GetAwaiter().GetResult();
        Check.False(vm.IsLoading, "cancelled shelf work cannot leave a permanent loading indicator");
        online.Detach();
        Dispatcher.UIThread.RunJobs();
    }
}
