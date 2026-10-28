using System.Net;
using System.Text.Json;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class UnifiedSearchTests
{
    private static readonly CatalogArtist Main = new("deezer:42", "North Star", "Alternative", DiscoveryTests.CoverUrl);
    private static readonly CatalogArtist Other = new("deezer:43", "North Star", "Dance", DiscoveryTests.CoverUrl);
    private static readonly CatalogRelease Orbit = DiscoveryTests.Orbit with { Artwork = DiscoveryTests.CoverUrl };
    private static CatalogSearchSong Song(int id, string title, CatalogArtist? by = null, bool? explicitFlag = true) => new(new("deezer:" + id, title, [by ?? Main], 180, explicitFlag), Orbit, Orbit.Artwork);
    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data)) };
    internal static DiscoveryTests.Http Http(bool apple = false) => new() { Override = (r, _) =>
    {
        var uri = r.RequestUri!;
        if (ArtistCatalog.ArtworkUrl(uri.AbsoluteUri)) return Task.FromResult(ArtworkQualityTests.Image(DiscoveryTests.Cover));
        if (apple && uri.Host == "api.deezer.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        if (uri.Host == "itunes.apple.com")
        {
            var a = new { artistId = 75, artistName = "North Star", primaryGenreName = "Alternative", wrapperType = "artist" };
            var album = new { artistId = 75, artistName = "North Star", collectionId = 211, collectionName = "Orbit", wrapperType = "collection", trackCount = 3, collectionExplicitness = "explicit", releaseDate = "2026-10-01", artworkUrl100 = DiscoveryTests.CoverUrl };
            var song = new { artistId = 75, artistName = "North Star", trackId = 301, trackName = "Flaws & Sins", kind = "song", wrapperType = "track", trackTimeMillis = 180000, collectionId = 211, collectionName = "Orbit", trackCount = 3, trackExplicitness = "explicit", artworkUrl100 = DiscoveryTests.CoverUrl };
            return Task.FromResult(Json(new { results = uri.Query.Contains("entity=song") ? new object[] { song } : uri.Query.Contains("entity=album") ? [album] : [a] }));
        }
        object data = uri.AbsolutePath switch
        {
            "/search/artist" => new { data = new[] { DiscoveryTests.Http.Artist(), DiscoveryTests.Http.Artist(43, "North Star") } },
            "/search/track" => new { data = new[] { Track(101, "Flaws & Sins", true), Track(102, "Flaws & Sins", false), Track(103, "Flaws & Sins (Live)", true) } },
            "/search/album" => new { data = new[] { DiscoveryTests.Http.Album(), DiscoveryTests.Http.Album(114, "Orbit (Deluxe)", "album", 6) } },
            "/artist/42" => DiscoveryTests.Http.Artist(),
            "/artist/43" => DiscoveryTests.Http.Artist(43, "North Star"),
            "/artist/42/top" => new { data = Array.Empty<object>() },
            "/artist/42/albums" => new { data = new[] { DiscoveryTests.Http.Album() }, total = 1 },
            _ => new { error = new { message = "Unsupported test URL" } },
        };
        return Task.FromResult(Json(data));
    } };
    private static object Track(int id, string title, bool explicitFlag) => new { id, title, artist = DiscoveryTests.Http.Artist(), duration = 180, explicit_lyrics = explicitFlag, album = new { id = 111, title = "Orbit", cover_medium = DiscoveryTests.CoverUrl } };

    public static async Task RunAsync()
    {
        Check.Section("unified search: intent, namesakes, edition preference and public metadata readers");
        var query = FinderQuery.Parse("Flaws & Sins by North Star");
        Check.Equal("Flaws & Sins North Star", query.Term, "natural title by artist becomes a catalog query without the connector");
        Check.Equal("Stand by Me", FinderQuery.Parse("Stand by Me").Text, "the original user search is retained");
        Check.Equal("Stand by Me", SearchRanking.Build(FinderQuery.Parse("Stand by Me"), [], [Song(108, "Stand by Me", new("deezer:800", "Ben E. King"))], [])[0].Title, "a by inside the song's actual title is not discarded by intent ranking");
        // Owner, 2026-10-18: "drake passionfruit" showed covers and karaoke, not Drake's song. Deezer's real answer, in its order and ranks.
        var drake = new CatalogArtist("deezer:246791", "Drake");
        CatalogSearchSong Ranked(int id, string title, CatalogArtist by, int rank) => Song(id, title, by) with { Song = new("deezer:" + id, title, [by], 200, true, Popularity: rank) };
        var passionfruit = new[]
        {
            Ranked(1, "Passionfruit", drake, 873309), Ranked(2, "Passionfruit (Drake Cover)", new("deezer:2", "Prudence"), 280486),
            Ranked(3, "Passionfruit (In the Style of Drake)", new("deezer:3", "UK Top 40 Backing Tracks 2017"), 18203),
            Ranked(4, "Passionfruit by Drake", new("deezer:4", "YEAST"), 15553), Ranked(5, "Passionfruit Drake", new("deezer:5", "Aiden Yoo"), 21112),
            Ranked(13, "Passionfruit (made popular by Drake) [karaoke version]", new("deezer:13", "Party Tyme Karaoke"), 68313),
        };
        foreach (var typed in new[] { "drake passionfruit", "passionfruit drake", "Drake Passionfruit" })
        {
            var hits = SearchRanking.Build(FinderQuery.Parse(typed), [], passionfruit, []);
            Check.Equal("deezer:1", hits.First(h => h.Kind == "Songs").Id, $"\"{typed}\" leads with Drake's own Passionfruit, not a cover");
            Check.Equal(drake.Id, SearchRanking.Artists(hits).First().Artist?.Id, $"\"{typed}\": Drake is the first artist shown");
        }
        // #59, live 2026-10-19: "rosalia despecha" led with a karaoke cover spelled without accents; ROSALÍA's own
        // DESPECHÁ (the most popular result) scored lowest because the typed words kept no accents and hers did.
        var rosalia = new CatalogArtist("apple:313845115", "ROSALÍA");
        var despecha = new[]
        {
            Ranked(30, "Despecha (Made Popular By Rosalia) [Karaoke Version]", new("deezer:30", "Party Tyme Karaoke"), 25980),
            Ranked(31, "Despechá (Rosalia) (Dance Version)", new("deezer:31", "Manu Sambad"), 42229),
            Ranked(32, "DESPECHÁ", rosalia, 920501), Ranked(33, "DESPECHÁ RMX", rosalia, 488965),
        };
        var accentHits = SearchRanking.Build(FinderQuery.Parse("rosalia despecha"), [], despecha, []);
        Check.Equal("deezer:32", accentHits.First(h => h.Kind == "Songs").Id, "\"rosalia despecha\" (no accents) leads with ROSALÍA's own DESPECHÁ, not a karaoke cover");
        Check.Equal(rosalia.Id, SearchRanking.Artists(accentHits).First().Artist?.Id, "\"rosalia despecha\": ROSALÍA is the first artist shown");
        var drakeSongs = new[]
        {
            Ranked(20, "Drake", new("deezer:20", "Small Band"), 9000), Ranked(21, "God's Plan", drake, 900000),
            Ranked(22, "Hotline Bling", drake, 850000), Ranked(23, "Drake (Interlude)", new("deezer:23", "Someone"), 4000),
        };
        var byName = SearchRanking.Build(FinderQuery.Parse("drake"), [drake], drakeSongs, []).Where(h => h.Kind == "Songs").Select(h => h.Id).ToList();
        Check.Equal("deezer:21,deezer:22", string.Join(",", byName.Take(2)), "typing \"drake\": Drake's songs, most popular first, before songs called \"Drake\"");
        var songs = new[] { Song(101, "Flaws & Sins"), Song(102, "Night"), Song(103, "Morning") };
        var rank = SearchRanking.Build(FinderQuery.Parse("North Star"), [Other, Main], songs, [Orbit]);
        Check.Equal(Main.Id, rank[0].Artist?.Id, "an artist query leads with the exact artist supported by song identities, even after a namesake");
        Check.True(rank.Any(h => h.Artist?.Id == Other.Id), "small namesakes remain available rather than filtered out");
        var songArtist = SearchRanking.Build(FinderQuery.Parse("Flaws & Sins"), [new("deezer:999", "Flaws & Sins")], songs, []);
        var runaway = Song(9000, "Runaway", new("deezer:1", "Kanye West")) with { Song = new("deezer:9000", "Runaway", [new("deezer:1", "Kanye West")], 548, true, Popularity: 950000) };
        var titleRank = SearchRanking.Build(FinderQuery.Parse("runaway"), [new("deezer:9999", "Runaway")],
            [Song(9001, "Runaway", new("deezer:2", "Unknown singer")) with { Song = new("deezer:9001", "Runaway", [new("deezer:2", "Unknown singer")], 180, true, Popularity: 1000) }, runaway], []);
        Check.Equal(runaway.Song.Id, titleRank.First().Track?.Song.Id, "a popular exact recording beats an obscure exact artist and low-popularity recording");
        Check.Equal(runaway.Song.Artists[0].Id, SearchRanking.Artists(titleRank).First().Artist?.Id,
            "Runaway's Artists shelf agrees with the song top result");
        // An exact-title album can give a literal namesake a stronger contextual rank without making it the top result.
        var namesake = new CatalogArtist("deezer:9999", "Runaway");
        var contextual = SearchRanking.Build(FinderQuery.Parse("runaway"), [namesake], [runaway],
            [new("deezer:777", "Runaway", namesake, "2020", "single", 1)], fansOf: a => a.Id == "deezer:1" ? 4_557_014 : 1000); // owner #188: real followings
        Check.Equal(runaway.Song.Artists[0].Id, SearchRanking.Artists(contextual).First().Artist?.Id,
            "a matching namesake album cannot replace the leading song's artist");
        Check.Equal(Main.Id, SearchRanking.Artists(songArtist).First().Artist?.Id, "Artists filter for a song title leads with the credited performer rather than a literal-name namesake");
        Check.True(SearchRanking.Artists(songArtist).First().Subtitle.Contains("Artist of “Flaws & Sins”"), "artist result explains the song relationship using catalog metadata");
        Check.True(songArtist.Any(h => h.Artist?.Id == "deezer:999"), "literal name match is retained below relevant song artists");
        var albumArtist = SearchRanking.Build(FinderQuery.Parse("Orbit"), [new("deezer:998", "Orbit")], [], [Orbit]);
        Check.Equal(Main.Id, SearchRanking.Artists(albumArtist).First().Artist?.Id, "Artists filter for an album title leads with its credited artist");
        Check.Equal("Songs", SearchRanking.Build(query, [Main], songs, [Orbit])[0].Kind, "a specific title and artist leads with the song");
        Check.Equal("Songs", SearchRanking.Build(FinderQuery.Parse("Flaws & Sins North Star"), [Main], songs, [Orbit])[0].Kind, "title plus artist works without the word by");
        Check.Equal("Albums", SearchRanking.Build(FinderQuery.Parse("Orbit"), [new("deezer:99", "Orbit")], [Song(104, "Orbit")], [Orbit with { Title = "Orbit (Deluxe)" }])[0].Kind, "an album query does not elevate an unrelated tiny namesake over the relevant album");
        var lesser = new CatalogArtist("deezer:900", "Small New Artist");
        Check.Equal(lesser.Id, SearchRanking.Build(FinderQuery.Parse(lesser.Name), [lesser], [], [])[0].Artist?.Id, "lesser artists need no popularity threshold or local library to appear first");
        Check.True(SearchRanking.Artists(SearchRanking.Build(FinderQuery.Parse(lesser.Name), [lesser], [Song(109, lesser.Name)], [])).Any(h => h.Artist?.Id == lesser.Id), "small artist remains selectable when a different performer has a song with that name");
        Check.Equal("deezer:111", ArtistCatalog.RelevanceEditions([Orbit, Orbit with { Id = "deezer:999", Title = "A newer unrelated record", Date = "2027" }])[0].Id, "search album consolidation preserves provider relevance instead of sorting by release date");
        Check.Equal(Main.Id, SearchRanking.Build(FinderQuery.Parse("North Star"), [Other, Main], [], [], new(Main, [Other, Main], ["Long Enough Album"], true))[0].Artist?.Id, "verified local album evidence outranks a provider's wrong namesake");
        TopResultIsWhatPeopleMean();
        var variants = new[] { Song(105, "Flaws & Sins", explicitFlag: false), songs[0], Song(106, "Flaws & Sins (Live)"), songs[0] with { Song = songs[0].Song with { Id = "deezer:107", Seconds = 240 } } };
        var selected = SearchRanking.DeduplicateSongs(variants).ToArray();
        Check.Equal(3, selected.Length, "equivalent clean and explicit copies collapse; live and different-length recordings stay distinct");
        Check.True(selected.Single(s => s.Song.Title == "Flaws & Sins" && s.Song.Seconds == 180).Song.Explicit == true, "the explicit copy is preferred");
        var http = Http(); var folder = Check.NewFolder("unified-network"); using var catalog = DiscoveryTests.Catalog(folder, http);
        var tracks = await catalog.SearchSongsAsync("Flaws & Sins فارسی", default);
        Check.Equal(3, tracks.Value.Count, "Deezer search preserves source song metadata before presentation deduplication");
        Check.Equal("deezer:111", tracks.Value[0].Album?.Id, "search song retains its exact parent album for navigation");
        Check.Equal("https://www.deezer.com/track/101", tracks.Value[0].Song.Request.Url, "download request keeps the selected recording's provider URL");
        Check.True(http.Calls[0].Query.Contains("%26") && Uri.UnescapeDataString(http.Calls[0].Query).Contains("فارسی"), "catalog search safely encodes ampersands and non-Latin names");
        var albums = await catalog.SearchAlbumsAsync("Orbit", default);
        Check.Equal(2, albums.Value.Count, "album search retains distinct standard/deluxe editions");
        var calls = http.Calls.Count; http.Override = (_, _) => throw new HttpRequestException("offline");
        Check.True((await catalog.SearchSongsAsync("Flaws & Sins فارسی", default)).Cached && (await catalog.SearchAlbumsAsync("Orbit", default)).Cached && http.Calls.Count == calls, "repeat searches use metadata cache entirely offline");
        await Check.ThrowsAsync<ArgumentException>(() => catalog.SearchSongsAsync("bad\nquery", default), "song search rejects controls before network access");
        await Check.ThrowsAsync<ArgumentException>(() => catalog.SearchAlbumsAsync(new string('x', 201), default), "album search rejects oversized input before network access");
        using var apple = DiscoveryTests.Catalog(Check.NewFolder("unified-apple"), Http(apple: true));
        var appleSongs = await apple.SearchSongsAsync("Flaws and Sins", default); var appleAlbums = await apple.SearchAlbumsAsync("Orbit", default);
        Check.Equal("apple:301", appleSongs.Value[0].Song.Id, "blocked Deezer falls back to Apple's song reader");
        Check.Equal("apple:211", appleAlbums.Value[0].Id, "blocked Deezer falls back to Apple's album reader");
        Check.True(appleSongs.Value[0].Song.Request.Explicit == true && appleSongs.Value[0].Song.Request.Url is null, "Apple metadata preserves explicitness without inventing a playable provider URL");
        var cancelHttp = new DiscoveryTests.Http { Override = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(); } };
        using var cancelled = DiscoveryTests.Catalog(Check.NewFolder("unified-cancel"), cancelHttp); using var stop = new CancellationTokenSource(30);
        await Check.ThrowsAsync<OperationCanceledException>(() => cancelled.SearchSongsAsync("Cancel me", stop.Token), "switching query cancels in-flight metadata without fallback traffic");
        Check.Equal(1, cancelHttp.Calls.Count, "cancelled metadata sends no second-provider request");
    }
    /// <summary>Owner #188, with Deezer's real counts (2026-10-28): the words decide what can be meant, how many people
    /// listen decides which. 172 of 175 live searches led as meant; these are the ones that went wrong before.</summary>
    private static void TopResultIsWhatPeopleMean()
    {
        Check.Section("owner #188: the top result is what people mean");
        var fans = new Dictionary<string, long>();
        CatalogArtist A(int id, string name, long? following = null) { var a = new CatalogArtist("deezer:" + id, name); if (following is { } f) fans[a.Id] = f; return a; }
        CatalogSearchSong S(int id, string title, CatalogArtist by, int rank) => Song(id, title, by) with { Song = new("deezer:" + id, title, [by], 200, true, Popularity: rank) };
        FinderHit Top(string typed, CatalogArtist[] artists, CatalogSearchSong[] songs) => SearchRanking.Build(FinderQuery.Parse(typed), artists, songs, [], fansOf: a => fans.TryGetValue(a.Id, out var f) ? f : null)[0];
        var words = SearchRanking.TypedWords("plot twis");
        Check.Equal(SearchRanking.NameFit.Typed, SearchRanking.NameMatch(words, "Plot Twist"), "an unfinished last word still names it");
        Check.Equal(SearchRanking.NameFit.Exact, SearchRanking.NameMatch(SearchRanking.TypedWords("beatles"), "The Beatles"), "a leading The may be left out of an artist's name");
        Check.Equal(SearchRanking.NameFit.Words, SearchRanking.NameMatch(SearchRanking.TypedWords("nights"), "The Nights", artist: false), "but not out of a title");

        var drake = A(246791, "Drake");
        var plotTwist = new[] { S(1, "Plot Twist", drake, 716839), S(2, "plot twist", A(2, "TWS"), 386597) };
        var namesakes = new[] { A(1703135, "Plot Twist", 848), A(14077421, "Plot Twist", 99) };
        Check.Equal("deezer:1", Top("plot twis", namesakes, plotTwist).Track?.Song.Id, "\"plot twis\": Drake's Plot Twist, even before Drake's following is known");
        fans[drake.Id] = 24_117_689;
        Check.Equal("deezer:1", Top("plot twis", namesakes, plotTwist).Track?.Song.Id, "\"plot twis\": Drake's Plot Twist with his following known");
        Check.Equal(drake.Id, SearchRanking.Artists(SearchRanking.Build(FinderQuery.Parse("plot twis"), namesakes, plotTwist, [], fansOf: a => fans.TryGetValue(a.Id, out var f) ? f : null)).First().Artist?.Id, "\"plot twis\": Drake leads the artists too");

        var kanyeWest = A(230, "Kanye West", 4_557_014);
        Check.Equal(kanyeWest.Id, Top("kanye", [A(5175553, "Kanye", 121), kanyeWest], [S(10, "KANYE", A(269444452, "FEEL"), 367856)]).Artist?.Id, "\"kanye\": Kanye West, not a song called KANYE or a namesake with 121 fans");
        var travisScott = A(4495513, "Travis Scott", 8_000_000);
        Check.Equal(travisScott.Id, Top("travis", [A(1181, "Travis", 289866), travisScott], [S(11, "Sing", A(1181, "Travis"), 806232)]).Artist?.Id, "\"travis\": Travis Scott, 28 times the band's following");
        Check.Equal("deezer:12", Top("beatles", [A(99999, "Beat Les", 40), A(12, "The Beatles", 3_000_000)], []).Artist?.Id, "\"beatles\": The Beatles, not Beat Les");
        Check.Equal("deezer:13", Top("kiss", [A(13, "KISS", 1_900_000)], [S(14, "Kiss It Better", A(564, "Rihanna", 15_000_000), 800000)]).Artist?.Id, "\"kiss\": the band, not Kiss It Better");
        Check.Equal("deezer:15", Top("heart", [A(15, "Heart", 125311)], [S(16, "Heartless", A(4050205, "The Weeknd", 14_762_391), 771398), S(17, "Heart", A(986, "Pet Shop Boys", 1_057_551), 454983)]).Artist?.Id, "\"heart\": the band, not Heartless");
        Check.Equal("deezer:18", Top("olivia", [A(18, "Olivia Rodrigo", 1_844_923)], [S(19, "Olivia", A(1424821, "One Direction", 6_592_526), 533763)]).Artist?.Id, "\"olivia\": Olivia Rodrigo, not One Direction's song");
        Check.Equal("deezer:22", Top("shape of", [], [S(20, "Shape Of", A(20, "RePLAY", 5000), 300000), S(21, "Shape Of My Heart", A(368, "Sting", 1_500_000), 700000), S(22, "Shape of You", A(384236, "Ed Sheeran", 14_000_000), 950000)]).Track?.Song.Id, "\"shape of\": Shape of You");
        var kanyeRunaway = S(23, "Runaway", kanyeWest, 870128);
        Check.Equal("deezer:23", Top("runaway", [], [kanyeRunaway, S(24, "Runaway", A(1424, "Linkin Park", 15_000_000), 650000)]).Track?.Song.Id, "\"runaway\": same title, the more played recording keeps its place");
        Check.Equal("deezer:25", Top("drake", [A(25, "Drake", 24_117_689)], [S(26, "God's Plan", drake, 937522), S(27, "Drake", A(27, "Small Band", 300), 9000)]).Artist?.Id, "\"drake\": the artist, not one of his songs");
    }

    public static void RunUi(string? pictures)
    {
        Check.Section("unified search UI: filters, owned songs, chosen downloads and native navigation");
        var folder = Check.NewFolder("unified-ui"); var http = Http(); using var catalog = DiscoveryTests.Catalog(folder, http);
        var library = new SpotifyPlaylistTests.Library(); library.Add(Path.Combine(folder, "root-one/sub"), "Flaws & Sins", "North Star");
        int fetched = 0; bool login = false; CatalogArtist? opened = null; CatalogRelease? openedAlbum = null;
        BotTrack Track(MusicRequest r) => new(r.Title, r.Artist, r.Duration, r.Title + r.Identity.GetHashCode(), async (output, _, ct) => { fetched++; await output.WriteAsync(new byte[] { 1 }, ct); return "mp3"; }) { Request = r };
        using var downloads = new Downloads(_ => { }); downloads.SetPaused(true);
        var actions = new DiscoveryActions(library, downloads, Track, () => Path.Combine(folder, "saved"), () => login);
        using var view = new DiscoveryView(() => catalog, a => opened = a, null, actions, (album, _) => { openedAlbum = album; return Task.CompletedTask; });
        var window = new Window { Content = view, Width = 820, Height = 740 }; FinderTheme.Apply(window); window.Show();
        view.Search("North Star");
        Check.Until(() => Text(view).Any(t => t.StartsWith("2 artists")), "one query fills artists, songs and albums without Telegram login");
        Check.True(Text(view).Contains("Top result") && Text(view).Contains("Recent releases"), "artist intent shows the leading artist and its recent releases together");
        Check.True(downloads.History.Count == 0 && fetched == 0, "browsing and matching never queue or download music");
        Button Find(string label) => view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, label) && b.IsEnabled);
        Find("Open in Noctis →").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check.Equal(Main.Id, opened?.Id, "leading artist opens its source identity through the native-page action");
        view.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b)?.Contains("Orbit album in Noctis") == true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check.Equal(Orbit.Id, openedAlbum?.Id, "album result delegates to native album navigation without queueing anything");
        view.SelectFilter("Songs"); Check.True(!Text(view).Contains("Recent releases") && !view.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Open in Noctis →")), "Songs filter narrows the same results instead of another search page");
        Check.True(view.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "In your library") && !b.IsEnabled), "nested local song is marked owned and protected from duplicate download");
        Find("Queue download").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.True(Text(view).Any(t => t.Contains("Log in to Telegram")) && downloads.History.Count == 0, "logged-out queue action gives a precise message and stages nothing");
        login = true; Find("Queue download").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => downloads.Active.Count == 1, "one chosen missing recording enters the existing download queue");
        Check.True(downloads.Paused && fetched == 0 && downloads.Active.Single().Track.Request?.Title == "Flaws & Sins (Live)", "queued selection keeps the exact live recording and the paused batch state");
        Find("Download now").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => fetched == 1 && downloads.Active.Count == 0, "Download now promotes only the chosen recording");
        view.SelectFilter("Albums"); var callCount = http.Calls.Count; view.SelectFilter("Artists"); view.SelectFilter("All");
        Check.True(callCount == http.Calls.Count, "filtering reuses results rather than requerying external services");
        window.Width = 480; Dispatcher.UIThread.RunJobs();
        Check.True(view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).All(b => b.Bounds.Width <= 480), "actions wrap within the minimum-width window");
        if (!string.IsNullOrEmpty(pictures))
        {
            Directory.CreateDirectory(pictures); window.Width = 840; Dispatcher.UIThread.RunJobs(); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var output = File.Create(Path.Combine(pictures, "50 unified search.png")); Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        view.Search("Flaws & Sins");
        Check.Until(() => Text(view).Any(t => t.StartsWith("2 artists")), "song-only query completes before contextual filter test"); view.SelectFilter("Artists");
        Check.True(view.GetVisualDescendants().OfType<Border>().Any(b => ToolTip.GetTip(b)?.ToString()?.StartsWith("Artist of “Flaws & Sins”") == true), "song relationship is available on hover without explanatory labels crowding the artist tiles");
        Check.False(Text(view).Any(t => t.Contains("Top search match") || t.StartsWith("Artist of “")), "visual hierarchy carries relevance rather than redundant match labels");
        opened = null; view.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b)?.Contains("artist in Noctis") == true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Equal(Main.Id, opened?.Id, "contextual artist tile navigates using the song's exact artist ID");
        window.Close();
        CancellationUi();
        PartialUi();
    }
    private static void CancellationUi()
    {
        int cancelled = 0;
        var http = new DiscoveryTests.Http { Override = async (r, ct) =>
        {
            if (r.RequestUri!.Query.Contains("old")) { try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled++; throw; } }
            return Json(new { data = Array.Empty<object>() });
        } };
        using var catalog = DiscoveryTests.Catalog(Check.NewFolder("unified-ui-cancel"), http); using var view = new DiscoveryView(() => catalog, _ => { });
        var window = new Window { Content = view, Width = 700, Height = 600 }; window.Show(); view.Search("old");
        Check.Until(() => http.Calls.Count > 0, "slow old query starts"); view.Search("new");
        Check.Until(() => cancelled > 0 && Text(view).Any(t => t.StartsWith("No catalog matches")), "new query cancels old work and settles without old results overwriting it");
        int botRequests = 0; view.BotSearchRequested += () => botRequests++;
        view.SelectFilter("Download bots"); Check.Equal(1, botRequests, "download bot search is explicit and separate from metadata browsing");
        view.ShowBots(new TextBlock { Text = "Synthetic bot results" }); view.SelectFilter("All");
        Check.Until(() => !Text(view).Contains("Synthetic bot results") && Text(view).Any(t => t.StartsWith("No catalog matches")), "returning to catalog removes bot results and restores catalog search");
        window.Close();
    }
    private static string[] Text(Control view) => view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
    private static void PartialUi()
    {
        var folder = Check.NewFolder("unified-partial-ui");
        ArtistIdentityTests.Seed(folder, "songs-search-v2:SLOW", new[] { Song(101, "Cached song") });
        ArtistIdentityTests.Seed(folder, "albums-search-v2:SLOW", new[] { Orbit });
        int stopped = 0;
        using var catalog = DiscoveryTests.Catalog(folder, new() { Override = async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { stopped++; throw; } return new();
        } });
        using var view = new DiscoveryView(() => catalog, _ => { }); var window = new Window { Content = view, Width = 800, Height = 650 }; window.Show(); view.Search("slow");
        Check.Until(() => Text(view).Contains("Cached song") && Text(view).Contains("Orbit"), "cached songs and albums appear while an unrelated artist lookup is still stalled");
        view.Dispose(); view.Dispose();
        Check.Until(() => stopped > 0, "closing the search cancels stalled work and repeated disposal is safe"); window.Close();
    }
}
