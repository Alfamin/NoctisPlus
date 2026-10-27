using System.Net.Sockets;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeMusicFinder;
using Noctis.Plugins;

namespace FreeMusicFinder.Tests;

/// <summary>
/// The search window and the plugin's entry point, with a stand-in for Noctis and a stand-in
/// for the bot. Runs on the UI thread of Avalonia's headless platform: no window is shown.
/// </summary>
internal static class WindowTests
{
    private static string? _pictures;

    /// <param name="pictures">A folder to save pictures of the window into, or null for none.</param>
    public static void Run(string? pictures)
    {
        _pictures = pictures;
        if (pictures is not null) Directory.CreateDirectory(pictures);
        var music = Check.NewFolder("window/music");
        var folder = Path.Combine(music, "Noctis Free Music");
        var host = new FakeHost(Check.NewFolder("window/noctis"), music);
        host.Tracks.Add(new TrackInfo("1", "In Library", "Artist", "Album", "Artist", TimeSpan.FromSeconds(201), 2020, "", 1, @"X:\elsewhere\song.flac", false, 0, 0));

        var bot = new FakeBot();
        var downloads = new Downloads(host.Log);
        try
        {
            NotLoggedIn(host, downloads, bot);
            SimpleLogin(host);

            var account = LoggedIn(host);
            ResultsAndDownloads(host, account, downloads, bot, folder);
            ReopenedWindow(host, account, downloads, bot, folder);
            FailureAndRetry(host, account, downloads, bot);
            BotTrouble(host, account, downloads, bot);
            NoLibraryPermission(host, account, downloads, bot);
        }
        finally
        {
            downloads.Dispose();
        }

        Plugin(music, folder);
        QueueControls(host, LoggedIn(host), bot);
        QueueStart(host, bot);
        PlaylistTab(host, LoggedIn(host), bot);
        UnifiedSearch(host, bot);
        CurrentDownloadStatus(host, bot);
    }

    private static void CurrentDownloadStatus(FakeHost host, FakeBot bot)
    {
        Check.Section("download status describes current work, not saved history");
        var folder = Check.NewFolder("current-download-status");
        using var downloads = new Downloads(host.Log);
        using var account = LoggedIn(host);
        for (int i = 1; i <= 9; i++) downloads.Start(LogicTests.Track("Previously saved " + i), folder);
        Check.Until(() => downloads.Active.Count == 0 && downloads.History.Count == 9, "nine earlier downloads have finished");
        var window = new SearchWindow(host, account, downloads, bot.SearchAsync); window.Show();
        var status = Field<TextBlock>(window, "_queueStatus");
        var progress = Field<ProgressBar>(window, "_queueProgress");
        var card = Field<Border>(window, "_queueCard");
        Check.False(card.IsVisible, "completed history alone does not show a download queue");
        var report = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        downloads.Start(new BotTrack("Tenth song", "Artist", null, "Artist - Tenth song", async (output, p, ct) =>
        {
            await report.Task.WaitAsync(ct);
            p?.Report(.25);
            await finish.Task.WaitAsync(ct);
            await output.WriteAsync(new byte[5000], ct);
            return "mp3";
        }), folder, priority: true);
        Check.Until(() => status.Text!.Contains("Tenth song") && progress.IsIndeterminate, "the new song shows Starting until its file transfer begins");
        Check.True(card.IsVisible && progress.Value == 0 && !status.Text!.Contains("queued") && !status.Text.Contains("saved"), "one immediate download has no fabricated backlog or 9-of-10 progress");
        report.SetResult();
        Check.Until(() => progress.Value == 25 && !progress.IsIndeterminate, "the progress bar follows the current file's actual 25 percent");
        downloads.Start(LogicTests.Track("Eleventh song"), folder, held: true);
        Check.Until(() => status.Text!.Contains("1 ready") && status.Text.Contains("Tenth song"), "a genuinely staged song is counted separately from the current transfer");
        downloads.SetPaused(true);
        downloads.Start(LogicTests.Track("Twelfth song"), folder);
        Check.Until(() => status.Text!.Contains("1 queued · paused") && status.Text.Contains("1 ready"), "paused backlog and songs awaiting Start retain their own counts");
        finish.SetResult();
        Check.Until(() => downloads.History.Count(d => d.State == DownloadState.Saved) == 10 && !progress.IsVisible, "after the current download finishes a paused queue has no fake file progress");
        downloads.ReleaseHeld(); downloads.SetPaused(false);
        Check.Until(() => downloads.Active.Count == 0 && !card.IsVisible, "finishing the remaining songs hides the download panel again");
        Check.Equal(12, downloads.History.Count(d => d.State == DownloadState.Saved), "all saved history remains available for duplicate checks");
        window.Close();
    }

    /// <summary>Songs queued from a playlist wait for a start of their own. The queue's one button gives it.</summary>
    private static void QueueStart(FakeHost host, FakeBot bot)
    {
        Check.Section("the queue's button starts everything: a paused queue and songs staged from a playlist alike");
        var folder = Check.NewFolder("queue-start");
        using (var downloads = new Downloads(host.Log))
        {
            downloads.SetPaused(true);
            downloads.Start(LogicTests.Track("Staged one"), folder, held: true); downloads.Start(LogicTests.Track("Staged two"), folder, held: true);
            var window = new SearchWindow(host, LoggedIn(host), downloads, bot.SearchAsync); window.Show();
            var toggle = Field<Button>(window, "_pauseButton");
            Check.Until(() => Equals(toggle.Content, "Start queued downloads"), "with songs staged from a playlist the button says Start queued downloads, not Resume queue");
            Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Manage queue")));
            Check.True(((TextBlock)((StackPanel)QueueRows(window)[0].Children[0]).Children[1]).Text!.EndsWith("Ready · press Start queued downloads"), "and each staged song names that button");
            Click(toggle);
            Check.Until(() => downloads.Active.Count == 0 && downloads.History.Count(d => d.State == DownloadState.Saved) == 2, "one press downloads them, although the queue was paused as well");
            Check.False(downloads.Paused, "the pause is lifted with it");
            Check.Until(() => Equals(toggle.Content, "Pause queue"), "after which the button offers to pause");
            Click(toggle);
            Check.True(downloads.Paused, "which pauses");
            downloads.Start(LogicTests.Track("Plain one"), folder);
            Check.Until(() => Equals(toggle.Content, "Resume queue"), "a paused queue without staged songs says Resume queue");
            Click(toggle);
            Check.Until(() => downloads.Active.Count == 0 && !downloads.Paused, "which resumes it");
            window.Close();
        }
        using (var downloads = new Downloads(host.Log))
        using (var nobody = new TelegramAccount(Path.Combine(host.DataDirectory, "nobody-queue.dat"), () => ""))
        {
            downloads.Start(LogicTests.Track("Staged three"), folder, held: true);
            var window = new SearchWindow(host, nobody, downloads, bot.SearchAsync); window.Show();
            var toggle = Field<Button>(window, "_pauseButton");
            Check.Until(() => Equals(toggle.Content, "Start queued downloads"), "not logged in, the button still offers the start");
            Click(toggle);
            Check.True(downloads.Active.Single().Held && Status(window).Contains("Log in to Telegram"), "but starts nothing and says to log in first");
            window.Close();
        }
    }

    private static void NotLoggedIn(FakeHost host, Downloads downloads, FakeBot bot)
    {
        Check.Section("the search window, not logged in");
        var window = new SearchWindow(host, new TelegramAccount(Path.Combine(host.DataDirectory, "nobody.dat"), () => ""), downloads, bot.SearchAsync);
        window.Show();
        window.Search("artist");
        Check.Until(() => Status(window) == "Log in to Telegram to search.", "asks to log in");
        Check.Equal(0, bot.Searches, "the bot is not asked");
        Check.Equal("Log in to Telegram…", (string?)TelegramButton(window).Content, "the Telegram button offers the login");
        window.Close();
    }

    private static TelegramAccount LoggedIn(FakeHost host)
    {
        var path = Path.Combine(host.DataDirectory, "telegram-test.dat");
        var store = new TelegramStore(path);
        store.Begin(12345, "not a real hash");
        store.SetAccount("@tester");
        return new TelegramAccount(path, () => "");
    }

    private static void SimpleLogin(FakeHost host)
    {
        Check.Section("QR-first Telegram login (owner #185), phone number and optional Advanced settings");
        var folder = Check.NewFolder("simple-login");
        File.WriteAllText(Path.Combine(folder, "telegram-defaults.private.json"), "{\"api_id\":12345,\"api_hash\":\"0123456789abcdef0123456789abcdef\"}");
        // The QR code starts as the dialog opens; a dead local proxy keeps this check off the network.
        using var account = new TelegramAccount(Path.Combine(folder, "telegram.dat"), () => "socks5://127.0.0.1:9");
        var window = new Noctis.Views.TelegramSignInDialog(account, host.Log);
        window.Show();
        var vm = (Noctis.ViewModels.TelegramSignInViewModel)window.DataContext!;
        Check.True(vm.IsQr && vm.Description.Contains("Link Desktop Device"), "a ready app opens on the QR code with where to scan it");
        Check.True(vm.ApiHash.Length == 32 && vm.ApiId == "12345", "the built-in API pair is filled in (the hash field is masked)");
        Picture(window, "7a QR login");
        vm.UsePhoneCommand.Execute(null);
        var advanced = window.FindControl<StackPanel>("AdvancedFields")!;
        var hash = window.FindControl<TextBox>("ApiHashField")!;
        Check.False(advanced.IsVisible, "API settings are collapsed when defaults are ready");
        Check.True(hash.PasswordChar != '\0', "the API hash is masked");
        Check.True(vm.Description.Contains("phone number"), "Use phone number shows the phone instructions");
        Check.Equal("+989123456789", Noctis.ViewModels.TelegramSignInViewModel.Digits("+۹۸۹۱۲۳۴۵۶۷۸۹"), "Persian phone/code digits are accepted");
        var rejected = Noctis.ViewModels.TelegramSignInViewModel.Explain("API_ID_INVALID");
        Check.True(rejected.Contains("Advanced") && rejected.Contains("API_CREDENTIALS_REJECTED"), "rejected defaults explain how to override them");
        Picture(window, "7 phone-first login");
        window.Close();
    }

    private static void ResultsAndDownloads(FakeHost host, TelegramAccount account, Downloads downloads, FakeBot bot, string folder)
    {
        Check.Section("the search window: results and downloads");
        var gate = new TaskCompletionSource();
        bot.Answers["artist"] = () => new[] { LogicTests.Track("First", before: () => gate.Task), LogicTests.Track("Second"), LogicTests.Track("In Library") };

        var window = new SearchWindow(host, account, downloads, bot.SearchAsync);
        window.Show();
        Check.Equal("Telegram: @tester", (string?)TelegramButton(window).Content, "the Telegram button says who is logged in");
        window.Search("artist");
        Check.Until(() => Rows(window).Count == 3, "three results are listed");
        var rows = Rows(window);
        Check.Equal("3 tracks found, 1 already in your library.", Status(window), "the status counts them");
        Check.Equal("Artist · 3:20 | Artist · 3:20 | Artist · 3:20 · in your library", string.Join(" | ", rows.Select(r => r.Details)), "the one the library has is marked");
        Check.Equal("Download now+ Download now+ Download now+", Buttons(rows), "all three can be downloaded");
        Check.True(DownloadAll(window).IsEnabled, "\"Download all\" is on");

        Click(rows[0].Button);
        Check.Until(() => Buttons(rows) == "Starting…- Download now+ Download now+", "the first one starts");
        Click(DownloadAll(window));
        Check.Until(() => downloads.Active.Count==2 && downloads.Active.Any(d=>d.Track.Title=="Second" && !d.Priority), "Queue all adds the second and leaves out the one in the library");
        Check.False(DownloadAll(window).IsEnabled, "\"Download all\" is off: nothing is left for it");
        Picture(window, "1 results, one running, one queued, one in the library");

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Check.Equal(2, downloads.Active.Count, "closing the window does not stop the downloads");
        gate.SetResult();
        Check.Until(() => downloads.Active.Count == 0, "they finish with the window closed");
        Check.Equal("Artist - First.mp3,Artist - Second.mp3", string.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName).Order()), "both songs are in the download folder");
    }

    private static void ReopenedWindow(FakeHost host, TelegramAccount account, Downloads downloads, FakeBot bot, string folder)
    {
        Check.Section("the search window, opened again while a download runs");
        var gate = new TaskCompletionSource();
        var third = downloads.Start(LogicTests.Track("Third", before: () => gate.Task), folder);
        Check.Until(() => third.State == DownloadState.Running, "a download is running");

        var window = new SearchWindow(host, account, downloads, bot.SearchAsync);
        window.Show();
        Check.Equal(0, Rows(window).Count, "reopened search does not mix in queued downloads");
        Check.Equal(1, QueueRows(window).Count, "queue tab shows the running song");
        Check.Equal("1 pending requests saved in the Queue tab.", Status(window), "status points to saved queue");

        window.Search("artist");
        Check.Until(() => Rows(window).Count == 3, "search lists only results; running download stays in queue tab");
        var rows = Rows(window);
        Check.Equal("First,Second,In Library", string.Join(",", rows.Select(r => r.Title)), "search results kept separate");
        Check.Equal("Downloaded- Downloaded- Download now+", Buttons(rows), "saved search results marked");
        Picture(window, "2 opened again, saved ones marked, a download still running");

        gate.SetResult();
        Check.Until(() => third.State==DownloadState.Saved && QueueRows(window).Count==0, "completed running request leaves queue tab");
        Check.Until(() => Status(window) == "Saved to library", "the save status stays compact");
        window.Close();
    }

    private static void FailureAndRetry(FakeHost host, TelegramAccount account, Downloads downloads, FakeBot bot)
    {
        Check.Section("the search window: a download that fails");
        var tries = 0;
        var broken = new BotTrack("Broken", "Artist", null, "Artist - Broken", async (output, _, ct) =>
        {
            if (++tries == 1) throw new TimeoutException("The bot sent another file (Somebody - Something) and not this track.");
            await output.WriteAsync(new byte[100], ct);
            return "flac";
        });
        bot.Answers["broken"] = () => new[] { broken };

        var window = new SearchWindow(host, account, downloads, bot.SearchAsync);
        window.Show();
        window.Search("broken");
        Check.Until(() => Rows(window).Count == 1, "one result");
        var row = Rows(window)[0];
        Click(row.Button);
        Check.Until(() => Buttons(new[] { row }) == "Retry+", "the button turns into \"Retry\"");
        Check.Equal("Could not download \"Broken\": The bot sent another file (Somebody - Something) and not this track.", Status(window), "the status says why");
        Check.Equal("The bot sent another file (Somebody - Something) and not this track.", ToolTip.GetTip(row.Button) as string, "so does the button's tip");
        Picture(window, "3 a download that failed");
        Click(row.Button);
        Check.Until(() => Buttons(new[] { row }) == "Downloaded-", "the second try saves it");
        Check.Equal(null, ToolTip.GetTip(row.Button), "the tip is gone");
        window.Close();
    }

    private static void BotTrouble(FakeHost host, TelegramAccount account, Downloads downloads, FakeBot bot)
    {
        Check.Section("the search window: the bot or Telegram misbehaves");
        const string unreadable = "@MusicsHuntersbot answered, but its list could not be read. The bot may have changed how it writes its results.";
        bot.Answers["unreadable"] = () => throw new BotAnswerException(unreadable);
        bot.Answers["silent"] = () => throw new TimeoutException("The bot did not answer.");
        bot.Answers["offline"] = () => throw new SocketException(10060);
        bot.Answers["proxy"] = () => throw new FormatException("The proxy setting is not understood. " + ProxyChoice.Help);
        bot.Answers["nothing"] = () => Array.Empty<BotTrack>();

        var window = new SearchWindow(host, account, downloads, bot.SearchAsync);
        window.Show();
        foreach (var (query, expected) in new[]
        {
            ("unreadable", unreadable),
            ("silent", "The bot did not answer."),
            ("offline", "Telegram could not be reached. Where it is blocked, turn on a VPN or its system proxy, or set a proxy in the plugin's settings."),
            ("proxy", "The proxy setting is not understood. " + ProxyChoice.Help),
            ("nothing", "Nothing found."),
        })
        {
            window.Search(query);
            Check.Until(() => Status(window) == expected, $"\"{query}\": {expected}");
            Check.True(SearchButton(window).IsEnabled && Rows(window).Count == 0, "  and the window is ready for the next search");
        }
        window.Close();
    }

    private static void NoLibraryPermission(FakeHost host, TelegramAccount account, Downloads downloads, FakeBot bot)
    {
        Check.Section("the search window, without the library permission");
        host.LibraryGranted = false;
        host.Logs.Clear();
        bot.Answers["two"] = () => new[] { LogicTests.Track("In Library"), LogicTests.Track("Also In Library") };
        var window = new SearchWindow(host, account, downloads, bot.SearchAsync);
        window.Show();
        window.Search("two");
        Check.Until(() => Rows(window).Count == 2, "the results are listed all the same");
        Check.Equal("2 tracks found.", Status(window), "none is marked");
        Check.Equal(1, host.Logs.Count(l => l.StartsWith("results are not checked against the library")), "the log says so once");
        host.LibraryGranted = true;
        window.Close();
    }

    private static void Plugin(string music, string folder)
    {
        Check.Section("native Finder lifecycle and Search routing");
        var host = new FakeHost(Check.NewFolder("plugin/noctis"), music);
        host.Values["shortcut"] = Shortcut.Default;
        var dead = Path.Combine(folder, "Artist - Dead.fmf.part");
        File.WriteAllText(dead, "x"); File.SetLastWriteTimeUtc(dead, DateTime.UtcNow.AddHours(-1));
        var requests = new List<string?>();
        EventHandler<string?> opened = (_, query) => requests.Add(query);
        Noctis.Services.Search.OnlineMusicSearch.OpenRequested += opened;
        var plugin = new FreeMusicPlugin((data, _) => DiscoveryTests.Catalog(data, new DiscoveryTests.Http()));
        var noctis = new Window { Width = 400, Height = 300 };
        try
        {
            plugin.Initialize(host);
            Check.Equal("2.11.1", plugin.Info.Version, "feature settings keep their original version and identity");
            Check.Equal("Find more by this artist…", string.Join(",", host.Commands.Select(c => c.Label)), "one native track menu entry");
            Check.Until(() => !File.Exists(dead), "abandoned partial download is cleaned at startup");
            Check.Until(() => host.Logs.Any(l => l.StartsWith("removed 1 unfinished download left in")), "startup cleanup is logged");
            host.Raise("openSearch");
            Check.Equal(1, requests.Count, "feature setting opens native Search once");
            host.Raise("telegramAccount");
            Check.Equal(1, requests.Count, "the Telegram account setting opens the sign-in window, not Search");
            host.Commands[0].Handler(new TrackInfo("7", "Some Song", "Somebody", "", "", TimeSpan.Zero, 0, "", 0, "", false, 0, 0));
            Check.Equal("Somebody", requests.Last(), "song menu searches the artist on the native Search page");
            Check.False(typeof(FreeMusicPlugin).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Any(f => f.FieldType.Name == "SearchWindow"), "feature has no old Finder window or fallback-window owner");
            Check.True(typeof(FreeMusicPlugin).Assembly.GetType("FreeMusicFinder.SearchWindow") is null &&
                typeof(FreeMusicPlugin).Assembly.GetType("FreeMusicFinder.FinderTheme") is null,
                "old Finder window and theme are absent from the shipped native assembly");
            var downloads = (Downloads)typeof(FreeMusicPlugin).GetField("_downloads", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(plugin)!;
            downloads.Start(LogicTests.Track("Announced"), folder);
            Check.Until(() => host.Notices.Count == 1, "finished download is announced independently of pages");
            Check.Equal("Saved to library", host.Notices.FirstOrDefault(), "success notice has no app name, title or folder path");
            noctis.Show();
            var before = requests.Count;
            Check.False(Press(noctis, Key.F, KeyModifiers.Control), "Ctrl+F remains Noctis's own command");
            Press(noctis, Key.F, KeyModifiers.Control | KeyModifiers.Shift, handledAlready: true);
            Check.Equal(before, requests.Count, "an already handled shortcut does not navigate");
            Check.True(Press(noctis, Key.F, KeyModifiers.Control | KeyModifiers.Shift), "Finder shortcut is handled");
            Check.Equal(before + 1, requests.Count, "shortcut routes directly to native Search");
            host.Values["shortcut"] = "Ctrl+Alt+M"; host.Raise("shortcut");
            Press(noctis, Key.F, KeyModifiers.Control | KeyModifiers.Shift);
            Check.Equal(before + 1, requests.Count, "changing shortcut removes the old binding");
            Press(noctis, Key.M, KeyModifiers.Control | KeyModifiers.Alt);
            Check.Equal(before + 2, requests.Count, "new shortcut opens native Search");
            host.Values["shortcut"] = "Shift+F"; host.Raise("shortcut");
            Check.Until(() => host.Notices.Any(n => n.Contains("would get in the way of typing")), "typing-conflicting shortcut is refused");
            Press(noctis, Key.F, KeyModifiers.Shift);
            Check.Equal(before + 2, requests.Count, "refused shortcut does not navigate");
            host.Values["shortcut"] = ""; host.Raise("shortcut");
            Press(noctis, Key.F, KeyModifiers.Control | KeyModifiers.Shift);
            Check.Equal(before + 2, requests.Count, "empty shortcut setting disables navigation");
            host.Values["shortcut"] = Shortcut.Default; host.Raise("shortcut");
            host.Values["proxy"] = "nonsense"; host.Raise("proxy");
            Check.Until(() => host.Notices.Any(n => n.Contains("The proxy setting is not understood")), "invalid proxy produces a native notice");
            plugin.Shutdown();
            Check.Equal(1, host.CommandsRemoved, "feature shutdown removes its song action");
            Check.False(Press(noctis, Key.F, KeyModifiers.Control | KeyModifiers.Shift), "feature shutdown removes its shortcut");
            Check.True(Noctis.Services.Search.OnlineMusicSearch.Current is null, "feature shutdown detaches the online source");
        }
        finally
        {
            plugin.Shutdown(); noctis.Close();
            Noctis.Services.Search.OnlineMusicSearch.OpenRequested -= opened;
        }
        var again = new FakeHost(Check.NewFolder("plugin/again"), music);
        again.Values["shortcut"] = Shortcut.Default; again.Values["proxy"] = "nonsense";
        var restarted = new FreeMusicPlugin((data, _) => DiscoveryTests.Catalog(data, new DiscoveryTests.Http()));
        string? stopped = null;
        try { restarted.Initialize(again); } catch (Exception ex) { stopped = ex.Message; }
        Check.Equal(null, stopped, "an invalid remembered proxy does not prevent feature startup");
        restarted.Shutdown();
    }
    private static QueueView Queue(SearchWindow window) => (QueueView)typeof(SearchWindow).GetField("_queueView",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
    private static List<Grid> QueueRows(SearchWindow window) => ((StackPanel)((ScrollViewer)Queue(window).Children[2]).Content!).Children.OfType<Border>().Select(b => b.Child).OfType<Grid>().ToList();
    private static Button QueueButton(SearchWindow window,string label) => Queue(window).GetVisualDescendants().OfType<Button>().First(b=>Equals(b.Content,label));
    private static void QueueControls(FakeHost host,TelegramAccount account,FakeBot bot)
    {
        Check.Section("queue tab: separate search, remove, confirm clear, priority and paging");
        using var downloads=new Downloads(host.Log);downloads.SetPaused(true);
        var folder=Check.NewFolder("queue-window");
        for(int i=0;i<205;i++)downloads.Start(LogicTests.Track("Batch "+i),folder);
        var window=new SearchWindow(host,account,downloads,bot.SearchAsync);window.Show();
        Click(window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Manage queue")));
        Check.Equal(100,QueueRows(window).Count,"first 100 requests visible");
        Click(QueueButton(window,"Next"));
        Check.Equal("Batch 100",((TextBlock)((StackPanel)QueueRows(window)[0].Children[0]).Children[0]).Text,"paging reaches requests beyond first 100");
        Click(QueueButton(window,"Next"));Check.Equal(5,QueueRows(window).Count,"final page exposes remaining requests");
        var remove=((StackPanel)QueueRows(window)[0].Children[1]).Children.OfType<Button>().Single(b=>Equals(b.Content,"Remove"));
        Click(remove);Check.Equal(204,downloads.Active.Count,"remove deletes only selected song");
        Check.False(downloads.Active.Any(d=>d.Track.Title=="Batch 200"),"correct selected song removed");
        ((TextBox)((Grid)Queue(window).Children[0]).Children[0]).Text="Batch 204";
        Check.Until(()=>QueueRows(window).Count==1,"queue search reaches a song on any page");
        var promote=((StackPanel)QueueRows(window)[0].Children[1]).Children.OfType<Button>().Single(b=>Equals(b.Content,"Download now"));
        Click(promote);
        Check.Until(()=>downloads.History.Any(d=>d.Track.Title=="Batch 204" && d.State==DownloadState.Saved),"download now promotes existing song despite paused batch");
        Check.True(downloads.Paused && downloads.Active.Count==203,"remaining batch stays paused");
        Click(QueueButton(window,"Clear queue…"));
        Check.Equal(203,downloads.Active.Count,"opening clear confirmation changes nothing");
        Click(QueueButton(window,"Keep queue"));Check.Equal(203,downloads.Active.Count,"cancel clear preserves queue");
        Click(QueueButton(window,"Clear queue…"));Click(QueueButton(window,"Yes, clear queue"));
        Check.Equal(0,downloads.History.Count,"confirmed clear erases backlog and request history");
        Check.True(File.Exists(Path.Combine(folder,"Artist - Batch 204.mp3")),"clear keeps downloaded audio");
        bot.Answers["queue demo"]=()=>new[]{LogicTests.Track("New queue demo")};
        window.Search("queue demo");Check.Until(()=>Rows(window).Count==1,"search usable after clearing");
        var result=((StackPanel)typeof(SearchWindow).GetField("_results",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Children.OfType<Border>().Select(b => b.Child).OfType<Grid>().First();
        var add=result.Children.OfType<Button>().Single(b=>Equals(b.Content,"Queue download"));
        Picture(window,"8 search and queue actions");Click(add);
        Check.Equal(1,downloads.Active.Count,"add to queue uses background lane");
        Check.False(downloads.Active.Single().Priority,"add to queue respects paused batch");
        Click(Rows(window)[0].Button);
        Check.Until(()=>downloads.Active.Count==0 && downloads.History.Last().State==DownloadState.Saved,"search Download now promotes a song already added to queue");
        Check.True(downloads.Paused,"manual search download leaves batch pause intact");
        downloads.Start(LogicTests.Track("Visible pending example"),folder);
        ((TabControl)Root(window).Children[2]).SelectedIndex=1;
        ((TextBox)((Grid)Queue(window).Children[0]).Children[0]).Text="";Dispatcher.UIThread.RunJobs();
        Queue(window).Refresh();Picture(window,"9 separate queue management");
        window.Width=480;Picture(window,"10 narrow queue management");
        window.Close();
    }

    // ── looking at the window ──

    private static void PlaylistTab(FakeHost host, TelegramAccount account, FakeBot bot)
    {
        Check.Section("complete Finder window: playlist tab and uncluttered navigation");
        var folder = Check.NewFolder("integrated-playlist-window"); var library = new SpotifyPlaylistTests.Library();
        library.Add(folder, "Song 0"); library.Add(Path.Combine(folder, "second/sub"), "Song 2");
        var sources = new MusicSources(new TransferTests.Source("DeezLoad"), new TransferTests.Source("Music Hunters"), _ => { });
        using var downloads = new Downloads(_ => { });
        using var imports = new SpotifyPlaylists(Path.Combine(folder, "imports.json"), library, downloads, sources.Track, new PlaylistCatalog(new SpotifyPlaylistTests.Http()));
        var task = imports.ImportAsync(SpotifyPlaylistTests.Url(1) + "\n" + SpotifyPlaylistTests.Url(2), default);
        Check.Until(() => task.IsCompleted, "isolated playlist data prepared"); task.GetAwaiter().GetResult();
        var window = new SearchWindow(host, account, downloads, bot.SearchAsync, playlists: imports); window.Show();
        Field<TabControl>(window, "_tabs").SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
        Check.False(Query(window).IsEffectivelyVisible, "search box is hidden on playlist tab");
        Check.False(Field<Border>(window, "_queueCard").IsVisible, "duplicate global queue controls are hidden on playlist tab");
        Picture(window, "14 complete Spotify playlist screen");
        window.Width = 480; window.Height = 740; Picture(window, "15 complete narrow Spotify playlist screen");
        Field<TabControl>(window, "_tabs").SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
        Check.True(Query(window).IsVisible, "returning to Search restores search input");
        window.Close();
    }

    private static void UnifiedSearch(FakeHost host, FakeBot bot)
    {
        Check.Section("complete unified Finder: one search box, catalog/bot switching and no duplicate Artists tab");
        using var catalog = DiscoveryTests.Catalog(Check.NewFolder("integrated-unified"), UnifiedSearchTests.Http());
        using var downloads = new Downloads(host.Log);
        using var discovery = new DiscoveryView(() => catalog, _ => { });
        var account = LoggedIn(host); var window = new SearchWindow(host, account, downloads, bot.SearchAsync, discovery: discovery); window.Show();
        var tabs = Field<TabControl>(window, "_tabs");
        Check.True(!tabs.Items.OfType<TabItem>().Any(t => Equals(t.Header, "Artists")), "Artists is a result filter rather than a separate top-level page");
        window.Search("North Star");
        Check.Until(() => discovery.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("2 artists") == true), "main search fills the integrated catalog results");
        Check.Equal("North Star", Query(window).Text, "one visible search box retains the query for every filter");
        Check.False(Field<Border>(window, "_queueCard").IsVisible, "empty queue does not consume search space");
        bot.Answers["North Star"] = () => new[] { LogicTests.Track("Unreleased") };
        discovery.SelectFilter("Download bots");
        Check.Until(() => Rows(window).Count == 1 && Status(window).StartsWith("1 track"), "explicit bot filter uses the existing exact-selection result and download code");
        bot.Answers["New bot query"] = () => new[] { LogicTests.Track("Another unreleased") };
        Query(window).Text = "New bot query"; SearchButton(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => Rows(window).Count == 1 && Rows(window)[0].Title == "Another unreleased", "next manual search retains the selected bot filter");
        Check.Equal("New bot query", discovery.Query, "bot search updates the shared query rather than retaining an older artist name");
        discovery.SelectFilter("All");
        Check.Until(() => !discovery.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Another unreleased"), "returning to All removes the bot list without a second search window");
        Check.Equal("New bot query", discovery.Query, "returning to catalog searches the query visible in the single search box");
        Check.True(Field<Button>(window, "_searchButton").IsEnabled && !DownloadAll(window).IsVisible, "catalog controls are restored without a hidden bot Queue all action");
        Picture(window, "51 unified full window");
        window.Close();
    }

    private sealed record RowView(string Title, string Details, Button Button);

    private static Grid Root(SearchWindow window) => (Grid)window.Content!;

    private static List<RowView> Rows(SearchWindow window)
    {
        var panel = (StackPanel)typeof(SearchWindow).GetField("_results",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        return panel.Children.OfType<Border>().Select(b => b.Child).OfType<Grid>().Select(row =>
        {
            var text = (StackPanel)row.Children[0];
            return new RowView(((TextBlock)text.Children[0]).Text ?? "", ((TextBlock)text.Children[1]).Text ?? "", (Button)row.Children[1]);
        }).ToList();
    }

    /// <summary>Each row's button: its label, then "+" when it can be pressed and "-" when not.</summary>
    private static string Buttons(IEnumerable<RowView> rows) => string.Join(" ", rows.Select(r => r.Button.Content + (r.Button.IsEnabled ? "+" : "-")));

    private static T Field<T>(object window, string name) => (T)window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static string Status(SearchWindow window) => Field<TextBlock>(window, "_status").Text ?? "";

    private static Button DownloadAll(SearchWindow window) => Field<Button>(window, "_downloadAllButton");

    private static Button TelegramButton(SearchWindow window) => Field<Button>(window, "_telegramButton");

    private static Button SearchButton(SearchWindow window) => Field<Button>(window, "_searchButton");

    private static TextBox Query(SearchWindow window) => Field<TextBox>(window, "_query");

    private static void Picture(Window window, string name)
    {
        if (_pictures is null) return;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var file = File.Create(Path.Combine(_pictures, name + ".png"));
        window.CaptureRenderedFrame()?.Save(file,Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    /// <summary>A key press in a window; returns whether someone took it.</summary>
    private static bool Press(Window window, Key key, KeyModifiers modifiers, bool handledAlready = false)
    {
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Handled = handledAlready };
        window.RaiseEvent(e);
        Dispatcher.UIThread.RunJobs();
        return e.Handled && !handledAlready;
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    // ── stand-ins ──

    /// <summary>The bot: answers a search text with a prepared list, or the way it was told to fail.</summary>
    private sealed class FakeBot
    {
        public Dictionary<string, Func<IReadOnlyList<BotTrack>>> Answers { get; } = new();
        public int Searches;

        public Task<IReadOnlyList<BotTrack>> SearchAsync(string query, CancellationToken ct)
        {
            Interlocked.Increment(ref Searches);
            return Task.FromResult(Answers[query]());
        }
    }

    /// <summary>Noctis, as far as the plugin uses it.</summary>
    internal sealed class FakeHost : IPluginHost, IPluginSettings, ILibraryReader
    {
        public FakeHost(string noctisData, string musicFolder)
        {
            File.WriteAllText(Path.Combine(noctisData, "settings.json"), "{ \"musicFolders\": [ " + System.Text.Json.JsonSerializer.Serialize(musicFolder) + " ] }");
            DataDirectory = Path.Combine(noctisData, "plugin-data", "dev.moshi.freemusicfinder");
            Directory.CreateDirectory(DataDirectory);
        }

        public List<string> Logs { get; } = new();
        public List<string> Notices { get; } = new();
        public List<(string Label, Action<TrackInfo> Handler)> Commands { get; } = new();
        public int CommandsRemoved;
        public Dictionary<string, string> Values { get; } = new();
        public List<TrackInfo> Tracks { get; } = new();
        public bool LibraryGranted = true;

        public void Raise(string key)
        {
            Changed?.Invoke(this, key);
            Dispatcher.UIThread.RunJobs();
        }

        // IPluginHost
        public string AppVersion => "1.5.8";
        public string DataDirectory { get; }
        public INowPlaying NowPlaying => throw new NotSupportedException();
        public IBeatSource Beat => throw new NotSupportedException();
        public ISpectrumSource Spectrum => throw new NotSupportedException();
        public void Log(string message) { lock (Logs) Logs.Add(message); }
        public void RegisterVisualLayer(IVisualLayerProvider provider) { }
        public IPluginSettings Settings => this;
        public ILibraryReader Library => LibraryGranted ? this : throw new PluginPermissionException(PluginPermissions.LibraryRead);
        public void Notify(string message) => Notices.Add(message);

        public IDisposable RegisterTrackCommand(string label, string? icon, Action<TrackInfo> handler)
        {
            Commands.Add((label, handler));
            return new Removal(() => CommandsRemoved++);
        }

        // IPluginSettings
        public event EventHandler<string>? Changed;
        public string? GetString(string key) => Values.GetValueOrDefault(key, "");
        public bool GetBool(string key, bool fallback = false) => Values.TryGetValue(key, out var value) ? value == "true" : fallback;
        public double GetNumber(string key, double fallback = 0) => fallback;

        // ILibraryReader, the way Noctis searches: every word of the query is in the title, artist or album.
        public int TrackCount => Tracks.Count;

        public IReadOnlyList<TrackInfo> Search(string query, int limit = 50)
        {
            var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return Tracks.Where(t => words.All(w => (t.Title + " " + t.Artist + " " + t.Album).ToLowerInvariant().Contains(w))).Take(limit).ToList();
        }

        private sealed class Removal : IDisposable
        {
            private readonly Action _removed;

            public Removal(Action removed) => _removed = removed;

            public void Dispose() => _removed();
        }
    }
}

