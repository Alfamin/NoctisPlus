using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeMusicFinder;
using static FreeMusicFinder.NativeCatalogBridge;

namespace FreeMusicFinder.Tests;

internal static class NativeCatalogTests
{
    public static void Run(string? root, string? pictures)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        Check.Section("native catalog integration: existing artist grids and existing album page, isolated library");
        var app = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Noctis");
        var core = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Noctis.Core");
        // Load the shipped theme resources into the isolated TEST application. No Noctis App
        // instance/services, settings, watchers, Telegram workers or real audio are started.
        Application.Current!.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        Application.Current.Resources["InterSemiBold"] = new Avalonia.Media.FontFamily("avares://Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf#Inter SemiBold");
        Application.Current.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
        Application.Current.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis/Controls/EqVisualizer.axaml") });
        var folder = Check.NewFolder("native-catalog"); var trackType = core.GetType("Noctis.Models.Track", true)!; var albumType = core.GetType("Noctis.Models.Album", true)!;
        Check.True(AlbumNavigation(app.GetType("Noctis.ViewModels.MainWindowViewModel", true)!, albumType).IsPublic, "album navigation resolves the installed host's public method and optional arguments");
        var track = Activator.CreateInstance(trackType)!; var path = Path.Combine(folder, "Owned.flac"); File.WriteAllText(path, "Synthetic test marker");
        Set(track, "Id", Guid.NewGuid()); Set(track, "Title", "Owned"); Set(track, "Artist", "North Star"); Set(track, "AlbumArtist", "North Star"); Set(track, "Album", "Orbit"); Set(track, "FilePath", path); Set(track, "Duration", TimeSpan.FromSeconds(180)); Set(track, "TrackNumber", 1); Set(track, "TrackCount", 3);
        var localAlbum = Activator.CreateInstance(albumType)!; Set(localAlbum, "Id", Guid.NewGuid()); Set(localAlbum, "Name", "Orbit (Int'l Version)"); Set(localAlbum, "Artist", "North Star"); Set(localAlbum, "Year", 2026); Set(localAlbum, "TrackCount", 1); Set(localAlbum, "Tracks", List(trackType, [track])); Set(track, "AlbumId", Get(localAlbum, "Id"));
        var originalTracks = List(trackType, [track]); var originalAlbums = List(albumType, [localAlbum]);
        var libraryType = core.GetType("Noctis.Services.ILibraryService", true)!;
        var library = DispatchProxy.Create(libraryType, typeof(NativePlaylistTests.Stub));
        ((NativePlaylistTests.Stub)library).Custom = (m, args) => m.Name switch
        {
            "get_Tracks" or "get_AllTracks" => originalTracks,
            "get_Albums" => originalAlbums,
            "get_Artists" => List(core.GetType("Noctis.Models.Artist", true)!, []),
            "GetTrackById" => Equals(Get(track, "Id"), args![0]) ? track : null,
            _ => NativePlaylistTests.Stub.Unhandled,
        };
        var persistence = Activator.CreateInstance(core.GetType("Noctis.Services.PersistenceService", true)!, BindingFlags.Instance | BindingFlags.NonPublic, null, [folder], null)!;
        object Proxy(Type type) => DispatchProxy.Create(type, typeof(NativePlaylistTests.Stub));
        var playerType = app.GetType("Noctis.ViewModels.PlayerViewModel", true)!;
        var playerCtor = playerType.GetConstructors().Single();
        var player = playerCtor.Invoke(playerCtor.GetParameters().Select(p => p.Name switch { "library" => library, "persistence" => persistence, _ => p.HasDefaultValue ? p.DefaultValue : Proxy(p.ParameterType) }).ToArray());
        var artistType = app.GetType("Noctis.ViewModels.ArtistDetailViewModel", true)!;
        var artistVm = artistType.GetConstructors().Single().Invoke(new object?[] { "North Star", library, player, null, null, null, null, null, null, null });
        using var catalog = DiscoveryTests.Catalog(Path.Combine(folder, "catalog"), new DiscoveryTests.Http());
        var profile = Finish(catalog.ArtistAsync(DiscoveryTests.Star, false, default)).Value;
        var metadata = Finish(catalog.AlbumAsync(DiscoveryTests.Orbit, false, default)).Value;
        var duplicateProfile = profile with { Releases = profile.Releases.Append(profile.Releases.First(r => r.Kind == "album") with { Id = "deezer:555", Explicit = false }).ToList() };
        var tiles = ApplyArtist(artistVm, duplicateProfile);
        Check.Equal(2, tiles.Count, "native artist merges clean/explicit catalog duplicates into one tile per edition");
        Check.Equal(2, Values(Get(artistVm, "Releases")).Count, "local Int'l tag does not add an extra one-track album tile");
        Check.Equal(1, (int)Get(artistVm, "AlbumCount")!, "native Albums section uses catalog release type");
        Check.Equal(1, (int)Get(artistVm, "SingleCount")!, "native Singles & EPs section is filled, including missing releases");
        Check.Equal(1, originalAlbums.Count, "global library album list is unchanged");
        Check.Equal(1, originalTracks.Count, "global track list contains no catalog-only placeholders");
        Check.True(ReferenceEquals(((IList)Get(localAlbum, "Tracks")!)[0], track), "original album and track objects retain their data");
        Check.True(Values(artistType.GetMethod("GetAllTracks", Private)!.Invoke(artistVm, null)).All(t => Get(t, "FilePath") is string file && File.Exists(file)), "artist header playback, queue and file commands enumerate real local tracks only");
        var reused = ApplyArtist(artistVm, profile, tiles);
        Check.True(reused.Keys.All(t => tiles.ContainsKey(t)), "artist re-sorting/rebuilds reuse existing catalog tiles and loaded artwork");
        Set(artistVm, "SelectedTab", "singles");
        Check.Until(() => Values(Get(artistVm, "SingleReleases")).Count == 1, "native Singles tab navigation shows fetched release");
        var artistView = (UserControl)Activator.CreateInstance(app.GetType("Noctis.Views.ArtistDetailView", true)!)!; artistView.DataContext = artistVm;
        var artistWindow = new Window { Content = artistView, Width = 1280, Height = 950 }; artistWindow.Show(); Save(artistWindow, pictures, "30 native artist singles"); artistWindow.Content = null; artistWindow.Close();
        var featured = profile with { Releases = profile.Releases.Concat(new[] { profile.Releases[0] with { Id = "deezer:999", Artist = new CatalogArtist("deezer:9", "Another artist"), Title = "Featured release" } }).ToList() };
        ApplyArtist(artistVm, featured);
        Check.True(Values(Get(artistVm, "AppearsOn")).Any(a => (string?)Get(a, "Name") == "Featured release"), "featured release fills the existing Appears On section");
        RestoreArtist(artistVm);
        Check.Equal(1, Values(Get(artistVm, "Releases")).Count, "restoring artist returns to local releases only");

        var sidebar = Activator.CreateInstance(app.GetType("Noctis.ViewModels.SidebarViewModel", true)!, persistence, library)!;
        var albumVmType = app.GetType("Noctis.ViewModels.AlbumDetailViewModel", true)!;
        var ctor = albumVmType.GetConstructors().Single();
        var albumVm = ctor.Invoke(ctor.GetParameters().Select(p => p.Name switch { "album" => localAlbum, "player" => player, "persistence" => persistence, "library" => library, "sidebar" => sidebar, _ => p.HasDefaultValue ? p.DefaultValue : Proxy(p.ParameterType) }).ToArray());
        var albumView = (UserControl)Activator.CreateInstance(app.GetType("Noctis.Views.AlbumDetailView", true)!)!; albumView.DataContext = albumVm;
        var list = albumView.FindControl<ItemsControl>("DiscGroupList")!;
        var originalItems = list.ItemsSource; var originalTemplate = list.ItemTemplate;
        var isolated = new SpotifyPlaylistTests.Library(); isolated.Songs.Add(new((Guid)Get(track, "Id")!, "North Star", "Owned", path, TimeSpan.FromSeconds(180)));
        int fetched = 0; bool loggedIn = true;
        BotTrack Track(MusicRequest r) => new(r.Title, r.Artist, r.Duration, r.Title, async (output, _, ct) => { Interlocked.Increment(ref fetched); await output.WriteAsync(new byte[] { 1, 2 }, ct); return "mp3"; }) { Request = r };
        using var downloads = new Downloads(_ => { }); downloads.SetPaused(true);
        var actions = new DiscoveryActions(isolated, downloads, Track, () => Path.Combine(folder, "saved"), () => loggedIn);
        var shell = new Shell(library, player);
        var pageAlbum = AlbumForPage(shell, tiles.First(t => t.Value.Kind == "album").Key, metadata with { Songs = metadata.Songs.Skip(1).ToList() }, Finish(actions.MatchAsync(metadata.Songs, default)));
        Check.True(Values(Get(pageAlbum, "Tracks")).Contains(track), "opening a merged catalog tile preserves local edition tracks absent from provider listing");
        var previous = CatalogAlbumLibrary.Attach(albumVm);
        Check.True(ReferenceEquals(previous, library), "album-only library overlay retains the original service");
        using var rows = new NativeAlbumRows(shell, albumVm, metadata, actions, list.ItemTemplate, _ => { });
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((_, _) => rows); list.ItemsSource = new object[] { metadata };
        Finish(rows.RefreshAsync());
        var albumWindow = new Window { Content = albumView, Width = 1280, Height = 1000 }; albumWindow.Show(); Dispatcher.UIThread.RunJobs();
        Check.True(rows.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Missing"), "missing song uses Noctis's own track template inside the existing album list");
        Check.Equal(1, Values(Get(albumVm, "Tracks")).Count, "native Play/Shuffle still receive only verified local tracks");
        Check.Equal(1, originalTracks.Count, "full album display does not fabricate global library tracks");
        Check.Equal(0, downloads.History.Count, "opening enriched native album downloads no audio");
        Check.True(!ReferenceEquals(Get(albumVm, "Album"), localAlbum) && (int)Get(localAlbum, "TrackCount")! == 1, "native album header gets a transient clone; original local metadata is unchanged");
        int back = 0; var backField = albumVmType.GetField("BackRequested", Private)!; backField.SetValue(albumVm, new EventHandler((_, _) => back++));
        albumVmType.GetMethod("RefreshFromLibrary", Private)!.Invoke(albumVm, null);
        Check.Equal(0, back, "a library update does not kick a catalog album out of its native page");
        Save(albumWindow, pictures, "31 native album owned and missing");
        Button Find(string title) => albumView.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, title));
        Check.Until(() => albumView.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Download all missing…")), "album-wide download action sits with Noctis's original header buttons");
        Check.Equal(0, rows.GetVisualDescendants().OfType<CheckBox>().Count(), "native album selection has no checkboxes");
        albumWindow.Width = 900; Dispatcher.UIThread.RunJobs();
        var headerDownload = Find("Download all missing…");
        var headerPoint = headerDownload.TranslatePoint(default, albumWindow)!.Value;
        Check.True(headerPoint.X >= 0 && headerPoint.X + headerDownload.Bounds.Width <= albumWindow.Bounds.Width, "native album download action wraps within a narrow page");
        Save(albumWindow, pictures, "32 native album narrow"); albumWindow.Width = 1280; Dispatcher.UIThread.RunJobs();
        Find("Download all missing…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Equal(0, downloads.Active.Count, "Download all asks for its album-wide confirmation before queueing");
        Find("Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void CtrlTap(string id)
        {
            Dispatcher.UIThread.RunJobs();
            var item = rows.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext is CatalogSong s && s.Id == id);
            var at = item.TranslatePoint(new Point(125, item.Bounds.Height / 2), albumWindow)!.Value;
            albumWindow.MouseDown(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
            albumWindow.MouseUp(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
        }
        CtrlTap("deezer:102"); CtrlTap("deezer:103");
        Check.Equal(2, rows.GetVisualDescendants().OfType<ListBoxItem>().Count(c => c.Classes.Contains("ctrl-selected")), "real Ctrl+click selects missing songs with native row highlighting");
        Find("Queue 2 downloads").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => downloads.Active.Count == 2, "native album queues exactly the selected missing songs");
        Check.Equal(0, fetched, "batch selection preserves paused downloads");
        Find("Download now").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => downloads.History.Any(d => d.State == DownloadState.Saved), "one native row's Download now promotes only its song");
        Check.True(downloads.Paused && downloads.Active.Count == 1 && fetched == 1, "the rest of the album remains queued and paused");
        Check.Equal("Synthetic test marker", File.ReadAllText(path), "owned audio marker untouched by all native UI actions");
        albumWindow.Content = null; albumWindow.Close(); rows.Dispose();
        albumVmType.GetField("_library", Private)!.SetValue(albumVm, library); Set(albumVm, "Album", localAlbum);
        list.ItemsSource = originalItems; list.ItemTemplate = originalTemplate;

        Check.Section("native page lifecycle: Back navigation, click guards and disable restoration");
        bool enabled = true; var logs = new List<string>(); var originalSingleCount = (int)Get(artistVm, "SingleCount")!;
        shell.CurrentView = artistVm;
        var lifecycleWindow = new Window { Content = artistView, Width = 1280, Height = 950 }; lifecycleWindow.Show();
        using var pages = new NativeCatalogPages(() => catalog, actions, logs.Add, () => enabled, () => shell, window: () => lifecycleWindow);
        var pageReady = System.Diagnostics.Stopwatch.StartNew();
        Check.Until(() => artistView.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("online releases") == true), "page adapter fills the actual native artist view");
        Console.WriteLine($"  PERF  cached native artist page ready: {pageReady.Elapsed.TotalMilliseconds:F1} ms");
        var catalogStatus = artistView.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text?.Contains("online releases") == true);
        void Guard(UserControl view)
        {
            var tile = Values(Get(artistVm, "SingleReleases")).Single();
            var button = new Button { DataContext = tile, Content = "Synthetic tile action" };
            int nativeHandler = 0; button.Click += (_, _) => nativeHandler++;
            var panel = view.FindControl<StackPanel>("PageContent")!; panel.Children.Add(button); Dispatcher.UIThread.RunJobs();
            var click = new RoutedEventArgs(Button.ClickEvent, button); button.RaiseEvent(click);
            Check.True(click.Handled, "catalog tile click is intercepted before native marker playback");
            Check.Equal(0, nativeHandler, "catalog guard runs before native tile code-behind handlers");
            panel.Children.Remove(button);
        }
        Guard(artistView);
        var catalogOrbit = Values(Get(artistVm, "Releases")).First(a => Equals(Get(a, "Name"), "Orbit"));
        var ownedTileButton = new Button { DataContext = catalogOrbit }; ownedTileButton.Classes.Add("album-tile");
        var dots = new Button { Content = "…" }; dots.Classes.Add("tile-more"); ownedTileButton.Content = dots;
        var artistPanel = artistView.FindControl<StackPanel>("PageContent")!; artistPanel.Children.Add(ownedTileButton); Dispatcher.UIThread.RunJobs();
        bool realMenuAlbum = false; dots.Click += (_, e) => { realMenuAlbum = ReferenceEquals(ownedTileButton.DataContext, localAlbum); e.Handled = true; };
        dots.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, dots));
        Check.True(realMenuAlbum, "owned artist tile keeps native menu handling with its original real local album");
        Dispatcher.UIThread.RunJobs(); Check.True(ReferenceEquals(ownedTileButton.DataContext, catalogOrbit), "catalog tile display is restored after its native owned-album menu opens");
        artistPanel.Children.Remove(ownedTileButton);
        var backArtist = (UserControl)Activator.CreateInstance(artistView.GetType())!; backArtist.DataContext = artistVm; lifecycleWindow.Content = backArtist;
        Dispatcher.UIThread.RunJobs(); pages.Scan(); Guard(backArtist);
        Check.True(!artistView.GetVisualDescendants().Contains(catalogStatus) && backArtist.GetVisualDescendants().Contains(catalogStatus), "Back reattaches catalog status to the newly created native artist view");

        shell.CurrentView = albumVm; lifecycleWindow.Content = albumView; Dispatcher.UIThread.RunJobs(); pages.Scan();
        Check.Until(() => albumView.GetVisualDescendants().OfType<NativeAlbumRows>().Any(), "adapter enriches an already open local album");
        Check.Until(() => Get(albumVm, "HeaderArtPath") is string cover && File.Exists(cover), "native album header receives the actual cached catalog artwork binding");
        var headerArt = Get(albumVm, "HeaderArtPath") as string;
        Check.True((bool)Get(albumVm, "CanViewArtwork")!, "native artwork viewer is enabled for the catalog cover");
        Check.Until(() => albumView.GetVisualDescendants().OfType<Control>().Any(c => c.GetType().Name == "CachedImage" && Equals(Get(c, "SourcePath"), headerArt) && c.IsVisible), "native cover image control receives the catalog artwork path");
        using (var decoded = new Avalonia.Media.Imaging.Bitmap(headerArt!)) Check.True(decoded.PixelSize.Width > 0, "cached cover decodes through Noctis's native image backend");
        albumVmType.GetMethod("RefreshFromLibrary", Private)!.Invoke(albumVm, null); pages.Scan();
        Check.Equal(headerArt, Get(albumVm, "HeaderArtPath") as string, "native rescan restores the same cover from cache instead of leaving Album placeholder");
        var customArt = Path.Combine(folder, "custom-cover.png"); File.WriteAllBytes(customArt, DiscoveryTests.Cover); Set(albumVm, "HeaderArtPath", customArt); pages.Scan();
        Check.Equal(customArt, Get(albumVm, "HeaderArtPath") as string, "catalog respects an existing native or user-selected cover"); headerArt = customArt;
        var backAlbum = (UserControl)Activator.CreateInstance(albumView.GetType())!; backAlbum.DataContext = albumVm; lifecycleWindow.Content = backAlbum;
        Dispatcher.UIThread.RunJobs(); pages.Scan();
        Check.Until(() => backAlbum.GetVisualDescendants().OfType<NativeAlbumRows>().Any(r => r.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Missing")), "Back recreates the full native album rows in the new view");
        Check.Equal(headerArt, Get(albumVm, "HeaderArtPath") as string, "album cover survives Back navigation");
        enabled = false; pages.Scan(); Dispatcher.UIThread.RunJobs();
        Check.True(!backAlbum.GetVisualDescendants().OfType<NativeAlbumRows>().Any(), "disabling catalog restores the original native album template");
        Check.True(ReferenceEquals(albumVmType.GetField("_library", Private)!.GetValue(albumVm), library) && ReferenceEquals(Get(albumVm, "Album"), localAlbum), "disabling restores the original album and library service");
        Check.Equal(1, Values(Get(albumVm, "Tracks")).Count, "disabling retains the original local playback list");
        Check.True(Get(albumVm, "HeaderArtPath") is null, "disabling restores the original native header artwork field");
        Check.Equal(originalSingleCount, (int)Get(artistVm, "SingleCount")!, "disabling returns artist sections to original local data");
        Check.True(logs.Count == 0, "native lifecycle has no unsupported-host or restoration errors");
        enabled = true; isolated.ReadError = new IOException("Synthetic library scan unavailable"); pages.Scan();
        Check.Until(() => backAlbum.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Synthetic library scan unavailable") == true), "library snapshot failure shows a precise retryable catalog error");
        Check.True(!backAlbum.GetVisualDescendants().OfType<NativeAlbumRows>().Any() && ReferenceEquals(Get(albumVm, "Album"), localAlbum), "failed enrichment rolls back the native template and original album");
        Check.True(ReferenceEquals(albumVmType.GetField("_library", Private)!.GetValue(albumVm), library) && Values(Get(albumVm, "Tracks")).Count == 1, "failed enrichment keeps native service and playback tracks intact");
        isolated.ReadError = null;
        backAlbum.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Retry catalog")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => backAlbum.GetVisualDescendants().OfType<NativeAlbumRows>().Any(), "Retry catalog recovers without leaving or recreating the native page");
        lifecycleWindow.Close(); pages.Dispose();
        Check.Section("native loading: saved releases stay usable during a stalled refresh and navigation cancels it");
        var staleFolder = Check.NewFolder("native-stale-catalog");
        foreach (var file in Directory.GetFiles(Path.Combine(folder, "catalog"), "*.json")) File.Copy(file, Path.Combine(staleFolder, Path.GetFileName(file)));
        var artistKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("artist:" + DiscoveryTests.Star.Id))).ToLowerInvariant() + ".json";
        File.WriteAllText(Path.Combine(staleFolder, artistKey), System.Text.Json.JsonSerializer.Serialize(new { Version = 1, SavedAt = DateTimeOffset.UtcNow.AddDays(-10), Value = profile }));
        int refreshing = 0, cancelled = 0;
        var slowHttp = new DiscoveryTests.Http { Override = async (request, ct) =>
        {
            if (ArtistCatalog.ArtworkUrl(request.RequestUri?.AbsoluteUri))
            { var image = new ByteArrayContent(DiscoveryTests.Cover); image.Headers.ContentType = new("image/png"); return new(System.Net.HttpStatusCode.OK) { Content = image }; }
            Interlocked.Exchange(ref refreshing, 1);
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { Interlocked.Exchange(ref cancelled, 1); throw; }
            return new();
        } };
        using var slowCatalog = DiscoveryTests.Catalog(staleFolder, slowHttp);
        var savedView = (UserControl)Activator.CreateInstance(artistView.GetType())!; savedView.DataContext = artistVm; shell.CurrentView = artistVm;
        var savedWindow = new Window { Content = savedView, Width = 1280, Height = 950 }; savedWindow.Show();
        using var savedPages = new NativeCatalogPages(() => slowCatalog, actions, logs.Add, () => true, () => shell, window: () => savedWindow);
        Check.Until(() => Volatile.Read(ref refreshing) == 1 && Values(Get(artistVm, "Releases")).Count == 2, "stale cached releases render before slow provider response completes");
        Check.True(savedView.GetVisualDescendants().OfType<ProgressBar>().Any(p => p.IsVisible && p.IsIndeterminate), "native artist page shows a subtle loading indicator during background refresh");
        shell.CurrentView = new object(); savedWindow.Content = new UserControl();
        Check.Until(() => Volatile.Read(ref cancelled) == 1, "navigating away cancels the old artist request and frees the network gate");
        var returnView = (UserControl)Activator.CreateInstance(artistView.GetType())!; returnView.DataContext = artistVm; shell.CurrentView = artistVm; savedWindow.Content = returnView;
        Check.Until(() => returnView.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("cached catalog") == true) && Values(Get(artistVm, "Releases")).Count == 2, "returning to a cancelled artist immediately restores its saved releases and resumes refresh");
        savedWindow.Close(); savedPages.Dispose();

        Check.Section("native artist identity: legacy namesake repair and scoped search visits");
        var secondAlbum = Activator.CreateInstance(albumType)!; Set(secondAlbum, "Id", Guid.NewGuid()); Set(secondAlbum, "Name", "Orbit - Single"); Set(secondAlbum, "Artist", "North Star"); Set(secondAlbum, "TrackCount", 1); Set(secondAlbum, "Tracks", List(trackType, [track])); originalAlbums.Add(secondAlbum);
        var identityFolder = Check.NewFolder("native-artist-identity");
        foreach (var file in Directory.GetFiles(Path.Combine(folder, "catalog"), "*.json")) File.Copy(file, Path.Combine(identityFolder, Path.GetFileName(file)));
        var wrongArtist = new CatalogArtist("deezer:404", "North Star", "Dance");
        ArtistIdentityTests.Seed(identityFolder, "search:NORTH STAR", new[] { wrongArtist, DiscoveryTests.Star });
        ArtistIdentityTests.Seed(identityFolder, "artist:" + wrongArtist.Id, new CatalogProfile(wrongArtist, [], [new("deezer:505", "Late Night Sessions", wrongArtist, "2026-01-01", "album", 10)]));
        var choicesPath = Path.Combine(identityFolder, "artist-choices.json");
        File.WriteAllText(choicesPath, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, CatalogArtist> { ["North Star"] = wrongArtist }));
        using var identityCatalog = DiscoveryTests.Catalog(identityFolder, new() { Offline = true });
        object NewArtist(string name) => artistType.GetConstructors().Single().Invoke(new object?[] { name, library, player, null, null, null, null, null, null, null });
        var correctVm = NewArtist("North Star"); shell.CurrentView = correctVm; shell.ArtistFactory = NewArtist;
        var correctView = (UserControl)Activator.CreateInstance(artistView.GetType())!; correctView.DataContext = correctVm;
        var identityWindow = new Window { Content = correctView, Width = 1280, Height = 950 }; identityWindow.Show();
        using var identityPages = new NativeCatalogPages(() => identityCatalog, actions, logs.Add, () => true, () => shell, choicesPath, () => identityWindow);
        Check.Until(() => correctView.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("online releases") == true), "native local artist page revalidates an old namesake selection");
        var confirmedChoices = File.ReadAllText(choicesPath);
        Check.True(confirmedChoices.Contains("deezer:42") && !confirmedChoices.Contains("deezer:404"), "local album evidence repairs the wrong legacy pin without user editing settings");
        Check.True(!Values(Get(correctVm, "Releases")).Any(a => Equals(Get(a, "Name"), "Late Night Sessions")), "wrong namesake album stays out of native local artist sections");
        identityPages.OpenArtist(wrongArtist); var namesakeVm = shell.CurrentView!;
        Check.Equal(confirmedChoices, File.ReadAllText(choicesPath), "opening a same-name search result never overwrites the local artist selection");
        var namesakeView = (UserControl)Activator.CreateInstance(artistView.GetType())!; namesakeView.DataContext = namesakeVm; identityWindow.Content = namesakeView;
        Check.Until(() => Values(Get(namesakeVm, "Releases")).Any(a => Equals(Get(a, "Name"), "Late Night Sessions")), "explicit namesake search still opens that particular artist for this visit");
        Check.Equal(confirmedChoices, File.ReadAllText(choicesPath), "visited namesake remains separate after its catalog finishes loading");
        shell.CurrentView = correctVm; identityWindow.Content = correctView; identityPages.Scan();
        Check.True(!Values(Get(correctVm, "Releases")).Any(a => Equals(Get(a, "Name"), "Late Night Sessions")), "returning to the library artist keeps the corrected catalog");
        identityWindow.Close(); identityPages.Dispose(); ((IDisposable)correctVm).Dispose(); ((IDisposable)namesakeVm).Dispose(); shell.ArtistFactory = null; originalAlbums.Remove(secondAlbum);

        Check.Section("album navigation: live-reported stale Opening status after leaving the artist");
        var openingFolder = Check.NewFolder("native-opening-cancel");
        ArtistIdentityTests.Seed(openingFolder, "search:NORTH STAR", new[] { DiscoveryTests.Star }); ArtistIdentityTests.Seed(openingFolder, "artist:" + DiscoveryTests.Star.Id, profile);
        int opening = 0, openingCancelled = 0;
        var openingHttp = new DiscoveryTests.Http { Override = async (request, ct) =>
        {
            if (ArtistCatalog.ArtworkUrl(request.RequestUri?.AbsoluteUri)) return ArtworkQualityTests.Image(DiscoveryTests.Cover);
            Interlocked.Exchange(ref opening, 1);
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { Interlocked.Exchange(ref openingCancelled, 1); throw; }
            return new();
        } };
        using var openingCatalog = DiscoveryTests.Catalog(openingFolder, openingHttp); var openingVm = NewArtist("North Star"); shell.CurrentView = openingVm;
        var openingView = (UserControl)Activator.CreateInstance(artistView.GetType())!; openingView.DataContext = openingVm;
        var openingWindow = new Window { Content = openingView, Width = 1280, Height = 950 }; openingWindow.Show();
        using var openingPages = new NativeCatalogPages(() => openingCatalog, actions, logs.Add, () => true, () => shell, window: () => openingWindow);
        Check.Until(() => openingView.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("online releases") == true), "artist is ready before navigation regression check");
        var openTile = Values(Get(openingVm, "Releases")).First(a => Equals(Get(a, "Name"), "Orbit")); var openButton = new Button { DataContext = openTile, Content = "Open synthetic album" };
        openingView.FindControl<StackPanel>("PageContent")!.Children.Add(openButton); Dispatcher.UIThread.RunJobs(); openButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, openButton));
        Check.Until(() => Volatile.Read(ref opening) == 1, "album request begins from the native artist tile");
        shell.CurrentView = new object(); openingWindow.Content = new UserControl(); openingPages.Scan();
        Check.Until(() => Volatile.Read(ref openingCancelled) == 1, "leaving the artist cancels its unfinished album request");
        shell.CurrentView = openingVm; openingWindow.Content = openingView; openingPages.Scan();
        Check.True(openingView.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("online releases") == true) && !openingView.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("Opening ") == true), "Back shows ready catalog status instead of stale Opening text");
        openingWindow.Close(); openingPages.Dispose(); ((IDisposable)openingVm).Dispose();
        ((IDisposable)albumVm).Dispose(); ((IDisposable)artistVm).Dispose();
        if (player is IDisposable disposable) disposable.Dispose();
    }
    private static T Finish<T>(Task<T> task) { Check.Until(() => task.IsCompleted, "native catalog operation completed"); return task.GetAwaiter().GetResult(); }
    private static void Finish(Task task) { Check.Until(() => task.IsCompleted, "native catalog operation completed"); task.GetAwaiter().GetResult(); }
    private sealed class Shell(object library, object player) : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly object _library = library; public object Player { get; } = player;
        private object? _current; public object? CurrentView { get => _current; set { _current = value; PropertyChanged?.Invoke(this, new(nameof(CurrentView))); } }
        public Func<string, object>? ArtistFactory { get; set; }
        private void OpenArtistDiscography(string name) => CurrentView = ArtistFactory!(name);
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
    private static void Save(Window window, string? folder, string name)
    {
        if (string.IsNullOrWhiteSpace(folder)) return; Directory.CreateDirectory(folder); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var file = File.Create(Path.Combine(folder, name + ".png")); window.CaptureRenderedFrame()?.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
