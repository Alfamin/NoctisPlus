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
        new ForYouCache(Path.Combine(folder, "for-you-last.json")).Write(new(1, now, now.AddDays(1), new(songs, [], "", Batch: "page-1")));
        var http = new DiscoveryTests.Http { Offline = true };
        using var catalog = DiscoveryTests.Catalog(Path.Combine(folder, "catalog"), http);
        using var downloads = new Downloads(_ => { });
        var loginRequests = 0;
        var signedIn = false;
        var actions = new DiscoveryActions(library, downloads, _ => throw new Exception("No transfer is allowed in this fixture"), () => folder, () => false);
        using var pages = new CatalogPages(() => catalog, actions, _ => { }, () => true, library: () => null);
        var online = new FinderOnlineSearch(() => catalog, actions, pages, () => signedIn, () => loginRequests++);
        using var source = new ForYouSource(() => catalog, actions, _ => [], library, null, Path.Combine(folder, "hidden.json"));
        using var vm = new ForYouShelfViewModel(source, online, actions, TimeSpan.FromMilliseconds(120));
        vm.SetActive(true);
        Check.Until(() => !vm.IsLoading && vm.Picks.Count == 2, "native shelf loads cached picks and excludes already-owned recordings");
        Check.Equal(0, http.Calls.Count, "showing saved picks does not perform catalog searches");
        Check.Equal(0, downloads.History.Count, "opening For you never queues or downloads audio");
        Check.True(source.Feedback.Batches is [{ Id: "page-1", Shown.Count: 2 }] && source.Feedback.Batches[0].Shown[0].Position == 0,
            "the page on screen is recorded as shown once, with each card's place");
        vm.InView([vm.Picks[1]]);
        Check.True(source.Feedback.Batches[0].Seen is [1], "only the cards the view reports in sight count as seen, by their place on the page");
        var first = vm.Picks[0];
        var signIn = vm.DownloadCommand.ExecuteAsync(first);
        Check.Until(() => signIn.IsCompleted, "download click completes the sign-in offer");
        signIn.GetAwaiter().GetResult();
        Check.Equal(1, loginRequests, "signed-out Download now opens the native sign-in flow");
        Check.Equal(0, downloads.History.Count, "sign-in offer does not create a fake pending download");
        var played = new List<string>();
        vm.PlayRequested = (source, song) =>
        {
            Check.True(ReferenceEquals(online, source), "For you uses its own online source for progressive playback");
            played.Add(song.Id); return Task.CompletedTask;
        };
        var signedOutPlay = vm.PlayCommand.ExecuteAsync(first);
        Check.Until(() => signedOutPlay.IsCompleted, "signed-out Play completes the sign-in offer");
        signedOutPlay.GetAwaiter().GetResult();
        Check.Equal(2, loginRequests, "signed-out Play uses the existing sign-in flow");
        Check.Equal(0, played.Count, "signed-out Play cannot start a hidden transfer");
        signedIn = true;
        var play = vm.PlayCommand.ExecuteAsync(first);
        Check.Until(() => play.IsCompleted, "Play forwards to the native player");
        play.GetAwaiter().GetResult();
        Check.True(played.SequenceEqual(new[] { first.Song.Online!.Id }), "Play forwards exactly the chosen song to the existing progressive player");
        Check.Equal(0, downloads.History.Count, "the shelf does not also issue a duplicate direct-download request");
        var again = vm.PlayCommand.ExecuteAsync(first);
        Check.Until(() => again.IsCompleted, "a second Play click completes");
        Check.Equal(1, source.Feedback.Interactions.Count(i => i.Action == DiscoverAction.Play), "clicking Play twice counts as one play of that card");
        vm.NotForMeCommand.Execute(first);
        Check.True(vm.HasUndo && vm.Picks.Count == 1 && source.Hidden.HidesSong(first.Pick.Track.Song), "Not for me hides just the chosen song immediately");
        Check.False(source.Hidden.HidesSong(vm.Picks[0].Pick.Track.Song), "another song by the same artist stays recommended");
        vm.UndoCommand.Execute(null);
        Check.True(!vm.HasUndo && vm.Picks[0] == first && !source.Hidden.HidesSong(first.Pick.Track.Song), "Undo restores the same card and removes the persistent preference");
        Check.False(source.Feedback.Interactions.Any(i => i.Action == DiscoverAction.Dismiss), "Undo also takes back what the dismissal taught");
        vm.NotForMeCommand.Execute(first);
        Check.Until(() => !vm.HasUndo, "Undo expires after its short window");
        Check.True(source.Hidden.HidesSong(first.Pick.Track.Song), "expiry leaves only that song dismissed");
        // Owner #177: the lyrics page's Not for me for a playing For you song is the card's own.
        var other = vm.Picks[0];
        var skipped = new List<string>();
        vm.DismissRequested = s => skipped.Add(s.Id);
        Check.True(vm.HasSong(other.Pick.Track.Song.Id) && !vm.HasSong("deezer:999"), "the lyrics page knows which playing song is a For you pick");
        vm.DismissSong(other.Pick.Track.Song.Id);
        Check.True(source.Hidden.HidesSong(other.Pick.Track.Song) && vm.HasUndo && skipped.SequenceEqual([other.Pick.Track.Song.Id]),
            "Not for me from the lyrics page hides the song, offers Undo and moves the player on");
        vm.UndoCommand.Execute(null);
        Check.True(vm.Picks.Count == 1 && !source.Hidden.HidesSong(other.Pick.Track.Song), "its Undo works like the card's");
        // Owner #166: Don't recommend artist from a card: their cards go at once, with Undo; nothing else is touched.
        vm.BlockArtistCommand.Execute(other.Artists[0]);
        Check.True(vm.Picks.Count == 0 && vm.HasUndo && vm.BlockedArtists.Count == 1 && source.Hidden.HidesArtist(other.Artists[0]),
            "Don't recommend artist removes that artist's cards at once and offers Undo");
        vm.UndoCommand.Execute(null);
        Check.True(vm.Picks.Count == 1 && vm.BlockedArtists.Count == 0 && !vm.HasUndo, "Undo recommends the artist again and brings the cards back");
        var guestSong = new CatalogSong("deezer:other-artist", "Other song", [new("deezer:other", "Other artist")], 180, true);
        var guest = new ForYouPick(new(new(guestSong, null), 1, "Test"), new(new Noctis.Services.Search.OnlineSong(guestSong.Id, guestSong.Title, "Other artist")), "page-1", 2);
        vm.Picks.Add(guest);
        vm.BlockArtistCommand.Execute(other.Artists[0]);
        source.Hidden.SetArtistBlocked("Other artist", true);
        Dispatcher.UIThread.RunJobs();
        vm.UndoCommand.Execute(null);
        Check.True(vm.Picks.Count == 1 && !vm.Picks.Contains(guest) && source.Hidden.HidesArtist("Other artist"),
            "Undo of one block cannot bring back an artist blocked later through another menu");
        source.Hidden.SetArtistBlocked("Other artist", false);
        Check.Until(() => vm.Picks.Contains(guest), "unblocking that other artist restores only their card");
        vm.Picks.Remove(guest);
        source.Hidden.SetArtistBlocked(other.Artists[0], true);
        Check.Until(() => vm.Picks.Count == 0, "blocking through another menu removes the shelf's cards too");
        source.Hidden.SetArtistBlocked(other.Artists[0], false);
        Check.Until(() => vm.Picks.Count == 1, "another menu's Undo restores the cards on their original page");
        vm.SetActive(false);
        vm.SetActive(true);
        Check.Equal(1, vm.Picks.Count, "leaving and returning preserves the page instead of moving cards");
        Check.Equal(1, source.Feedback.Batches.Count, "returning to the same page does not count it as shown again");
        Check.Equal(1, source.Feedback.Interactions.Count(i => i.Action == DiscoverAction.Dismiss), "the expired dismissal stays recorded once");
        Check.Equal(0, http.Calls.Count, "returning to the shelf does not rebuild recommendations or delay Search");
        var outage = vm.RefreshCommand.ExecuteAsync(null);
        Check.Until(() => outage.IsCompleted && !vm.IsLoading, "Refresh with no catalog reachable finishes");
        Check.True(vm.Picks.Count == 1 && vm.Status.StartsWith("Couldn't refresh right now", StringComparison.Ordinal),
            "a catalog outage on Refresh keeps the picks on screen instead of blanking the shelf, and says so");
        Check.False(vm.HasNewPicks, "an outage is not announced as new picks");
        int calls = http.Calls.Count;
        http.Override = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new Exception("unreachable"); };
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        Check.Until(() => http.Calls.Count > calls, "explicit Refresh starts metadata work");
        vm.SetActive(false);
        Check.Until(() => refresh.IsCompleted, "typing or leaving Search cancels recommendation requests promptly");
        refresh.GetAwaiter().GetResult();
        Check.False(vm.IsLoading, "cancelled shelf work cannot leave a permanent loading indicator");
        http.Override = null;
        source.Hidden.SetArtistBlocked(other.Artists[0], true);
        vm.IsExpanded = false;
        var reset = vm.ResetLearningCommand.ExecuteAsync(null);
        Check.Equal("For you was reset", vm.ResetNotice, "Reset gives immediate feedback before the new page finishes");
        Check.True(vm.IsExpanded, "Reset opens a folded shelf so its confirmation and new picks are visible");
        Check.Until(() => reset.IsCompleted, "Reset finishes without waiting forever on the old cancelled request");
        Check.True(source.Hidden.HidesArtist(other.Artists[0]), "Reset keeps artists the listener explicitly blocked");
        online.Detach();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Owner #145: a page saved by 2.2.0 has no name (Batch). As the oldest kept page it could not be opened: the
    /// pager took it for page 1, so its number and the arrow showed the same page again and again.</summary>
    internal static void RunPager()
    {
        Check.Section("native For you shelf: every kept page opens, a 2.2.0 page too");
        var folder = Check.NewFolder("native-for-you-pager");
        var library = new SpotifyPlaylistTests.Library();
        var now = DateTimeOffset.UtcNow;
        ForYouCache.Page Page(string batch, int first, int ageHours) => new(1, now.AddHours(-ageHours), now.AddDays(1),
            new(Enumerable.Range(first, 3).Select(i => new DiscoverSong(new(DiscoveryTests.Song("deezer:" + i, "Song " + i), DiscoveryTests.Orbit), 1, "Because you play North Star")).ToList(), [], "", Batch: batch));
        new ForYouCache(Path.Combine(folder, "for-you-last.json")).Write(Page("page-new", 300, 0));
        new ForYouCache(Path.Combine(folder, "for-you-pages.json")).WriteEarlier([Page("page-mid", 200, 1), Page("", 100, 2)]);
        var http = new DiscoveryTests.Http { Offline = true };
        using var catalog = DiscoveryTests.Catalog(Path.Combine(folder, "catalog"), http);
        using var downloads = new Downloads(_ => { });
        var actions = new DiscoveryActions(library, downloads, _ => throw new Exception("No transfer is allowed in this fixture"), () => folder, () => false);
        using var pages = new CatalogPages(() => catalog, actions, _ => { }, () => true, library: () => null);
        var online = new FinderOnlineSearch(() => catalog, actions, pages, () => true, () => { });
        using var source = new ForYouSource(() => catalog, actions, _ => [], library, null, Path.Combine(folder, "hidden.json"));
        using var vm = new ForYouShelfViewModel(source, online, actions);
        vm.SetActive(true);
        Check.Until(() => !vm.IsLoading && vm.Picks.Count == 3 && vm.PageTabs.Count == 3, "three kept pages give three page numbers");
        string Shown() => vm.Picks[0].Song.Online!.Id;
        string Current() => string.Join(",", vm.PageTabs.Where(t => t.IsCurrent).Select(t => t.Number));
        vm.ShowPageCommand.Execute(vm.PageTabs[2]);
        Check.Until(() => Shown() == "deezer:100", "page 3 (saved by 2.2.0, no name) opens");
        Check.Equal("3", Current(), "the pager marks page 3 as the one on screen");
        Check.False(vm.CanNextPage, "there is no page after the oldest");
        vm.PreviousPageCommand.Execute(null);
        Check.Until(() => Shown() == "deezer:200", "the arrow goes from page 3 to page 2");
        Check.Equal("2", Current(), "the pager follows to page 2");
        vm.NextPageCommand.Execute(null);
        Check.Until(() => Shown() == "deezer:100", "and back to page 3");
        vm.ShowPageCommand.Execute(vm.PageTabs[0]);
        Check.Until(() => Shown() == "deezer:300", "page 1 opens again");
        online.Detach();
        Dispatcher.UIThread.RunJobs();
    }
}
