using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FreeMusicFinder.Tests;

/// <summary>For you: songs the listener does not have, from the artists they play. Synthetic data, no network.</summary>
internal static class ForYouDiscoverTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    public static async Task RunAsync()
    {
        Check.Section("For you: taste from listening, songs you don't have, reasons and dismissals");
        TasteFromListening();
        Sources();
        await Ranking();
        Hidden();
        await RelatedFromDeezer();
        await ListenBrainz();
    }

    private static void TasteFromListening()
    {
        var plays = new List<TastePlay>();
        for (int i = 0; i < 8; i++) plays.Add(new("Juice WRLD feat. Halsey", Now.AddDays(-i), 1.5));
        for (int i = 0; i < 6; i++) plays.Add(new("Skipped Artist", Now.AddDays(-1), -1.5));
        plays.Add(new("Old Phase", Now.AddDays(-400), 1.5));
        var library = Enumerable.Range(0, 25).Select(i => new LocalSong(Guid.NewGuid(), "Travis Scott", "Song " + i, "C:/m/" + i + ".flac", TimeSpan.FromMinutes(3))).ToList();
        var seeds = Taste.Seeds(plays, library, Now);
        Check.Equal("Juice WRLD", seeds.FirstOrDefault()?.Name, "the artist played and finished most leads, without its featured guest");
        Check.True(seeds.Any(s => s.Name == "Travis Scott" && s.Owned == 25), "owning many songs of an artist counts as taste too");
        Check.False(seeds.Any(s => s.Name == "Skipped Artist"), "an artist skipped early again and again is not a seed");
        Check.False(seeds.Any(s => s.Name == "Old Phase"), "a phase from more than a year ago has faded");
        Check.Equal("Tyler", Taste.MainArtist("Tyler; The Creator") , "multiple artists joined by ';' give the first");
        Check.Equal(0, Taste.Seeds([], [], Now).Count, "no plays and no library: no seeds, nothing invented");
    }

    private static void Sources()
    {
        var folder = Check.NewFolder("for-you-sources");
        var shuffle = Path.Combine(folder, "plugin-data", "dev.moshi.trueshuffle"); Directory.CreateDirectory(shuffle);
        File.WriteAllLines(Path.Combine(shuffle, "listens.jsonl"), [
            "{\"trackId\":\"1\",\"title\":\"A\",\"artist\":\"Done\",\"startUtc\":\"2026-10-15T10:00:00Z\",\"end\":\"Finished\",\"coverage\":1,\"listenedMs\":200000}",
            "{\"trackId\":\"2\",\"title\":\"B\",\"artist\":\"Skip\",\"startUtc\":\"2026-10-15T10:05:00Z\",\"end\":\"Skipped\",\"coverage\":0.05,\"listenedMs\":9000}",
            "{\"trackId\":\"3\",\"title\":\"C\",\"artist\":\"Cut",
            "{\"trackId\":\"4\",\"title\":\"D\",\"artist\":\"Picked\",\"startUtc\":\"2026-10-15T10:09:00Z\",\"end\":\"Replaced\",\"coverage\":0.01,\"listenedMs\":2000}",
        ]);
        File.WriteAllText(Path.Combine(folder, "play_history.json"), JsonSerializer.Serialize(new object[]
        {
            new { TrackId = Guid.NewGuid(), Title = "Old", Artist = "Before", PlayedAtUtc = "2026-10-10T10:00:00Z", Skipped = false },
            new { TrackId = Guid.NewGuid(), Title = "Dup", Artist = "Done", PlayedAtUtc = "2026-10-15T11:00:00Z", Skipped = false },
        }));
        var plays = TasteSources.Load(folder);
        Check.True(plays.Any(p => p.Artist == "Done" && p.Weight == 1.5) && plays.Any(p => p.Artist == "Skip" && p.Weight < -1), "True Shuffle's log gives finishes and early skips their weight");
        Check.False(plays.Any(p => p.Artist == "Picked"), "picking another song after two seconds says nothing about the first");
        Check.True(plays.Count(p => p.Artist == "Before") == 1 && !plays.Any(p => p.Artist == "Done" && p.Weight == 1.0), "Noctis history counts only before True Shuffle's log began (no double counting), and a cut line is skipped");
        Check.Equal(0, TasteSources.Load(null).Count, "no Noctis data folder: nothing read");
    }

    private sealed class FakeCatalog : IDiscoverCatalog
    {
        public readonly Dictionary<string, CatalogArtist?> Find = new();
        public readonly Dictionary<string, List<string>> Similar = new();
        public readonly Dictionary<string, CatalogArtist> Named = new();
        public readonly HashSet<string> Broken = new();
        public int Calls;
        public Task<CatalogArtist?> FindAsync(TasteSeed seed, CancellationToken ct) { Calls++; if (Broken.Contains(seed.Name)) throw new HttpRequestException("Synthetic offline"); return Task.FromResult(Find.GetValueOrDefault(seed.Name)); }
        public Task<CatalogArtist?> FindByNameAsync(string name, CancellationToken ct) { Calls++; return Task.FromResult(Named.GetValueOrDefault(name)); }
        public Task<List<SimilarArtist>> SimilarAsync(TasteSeed seed, CatalogArtist? artist, CancellationToken ct)
        {
            Calls++; if (Broken.Contains(seed.Name)) throw new HttpRequestException("Synthetic offline");
            return Task.FromResult((Similar.GetValueOrDefault(seed.Name) ?? []).Select((n, i) => new SimilarArtist(n, 1 - i * 0.1)).ToList());
        }
        public Task<List<CatalogSearchSong>> TopSongsAsync(CatalogArtist artist, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Enumerable.Range(1, 10).Select(i => new CatalogSearchSong(
                new CatalogSong(artist.Id + "-" + i, artist.Name + " hit " + i, [artist], 180, true), null)).ToList());
        }
    }
    private static CatalogArtist A(string id, string name) => new("deezer:" + id, name);

    private static async Task Ranking()
    {
        var catalog = new FakeCatalog();
        var juice = A("1", "Juice WRLD"); var travis = A("2", "Travis Scott");
        catalog.Find["Juice WRLD"] = juice; catalog.Find["Travis Scott"] = travis; catalog.Find["Ambiguous"] = null;
        catalog.Similar["Juice WRLD"] = ["Lil Uzi", "Trippie", "Iann Dior", "Unknown To Catalog"];
        catalog.Similar["Travis Scott"] = ["Trippie", "Don Toliver"];
        foreach (var a in new[] { A("10", "Lil Uzi"), A("11", "Trippie"), A("12", "Iann Dior"), A("13", "Don Toliver") }) catalog.Named[a.Name] = a;
        var seeds = new List<TasteSeed> { new("Juice WRLD", 3, 40), new("Travis Scott", 1, 10), new("Ambiguous", 0.5, 2) };
        var hidden = new DiscoverHidden(Path.Combine(Check.NewFolder("for-you-rank"), "hidden.json"));
        hidden.Hide(new CatalogSong("deezer:10-1", "x", [A("10", "Lil Uzi")], 1, true));
        var owned = new HashSet<string> { "deezer:1-1", "deezer:1-2", "deezer:11-1" };
        var result = await Discover.BuildAsync(seeds, catalog, (songs, _) => Task.FromResult(songs.Select(s => s.Id).Where(owned.Contains).ToHashSet()), hidden, null, CancellationToken.None);
        var ids = result.Songs.Select(s => s.Track.Song.Id).ToList();
        Check.True(result.Songs.Count >= 30 && ids.Distinct().Count() == ids.Count, $"a long list of different songs ({result.Songs.Count})");
        Check.False(ids.Any(owned.Contains), "songs you already have (or have queued) are never suggested");
        Check.False(ids.Contains("deezer:10-1"), "a song you said 'not for me' to stays away");
        var artists = result.Songs.Select(s => s.Track.Song.Artists[0].Id).ToList();
        Check.True(artists.Zip(artists.Skip(1)).All(p => p.First != p.Second), "the same artist never comes twice in a row");
        Check.True(artists.Take(15).GroupBy(a => a).All(g => g.Count() <= 3), "and at most three times while other artists are left");
        Check.True(result.Songs.First(s => s.Track.Song.Artists[0].Id == "deezer:12").Because == "Because you play Juice WRLD"
            && result.Songs.First(s => s.Track.Song.Artists[0].Id == "deezer:1").Because == "More from Juice WRLD", "each song says why it is there");
        Check.True(ids.IndexOf(ids.First(i => i.StartsWith("deezer:11-"))) < ids.IndexOf(ids.First(i => i.StartsWith("deezer:13-"))),
            "an artist related to two of your artists ranks above one related to a lesser one");
        Check.True(result.Artists.Select(a => a.Artist.Id).Contains("deezer:11") && !result.Artists.Any(a => a.Artist.Id is "deezer:1" or "deezer:2"),
            "Artists for you shows new artists, not the ones you already play");
        var none = await Discover.BuildAsync([], catalog, (_, _) => Task.FromResult(new HashSet<string>()), hidden, null, CancellationToken.None);
        Check.True(none.Songs.Count == 0 && none.Notice.Contains("Play some music first"), "no listening yet: says so instead of guessing");
        var offline = new FakeCatalog(); offline.Broken.Add("Juice WRLD");
        var down = await Discover.BuildAsync([new("Juice WRLD", 3, 1)], offline, (_, _) => Task.FromResult(new HashSet<string>()), hidden, null, CancellationToken.None);
        Check.True(down.Songs.Count == 0 && down.Notice.Contains("could not reach"), "an unreachable catalog is reported as such, not as 'nothing found'");
    }

    private static void Hidden()
    {
        var path = Path.Combine(Check.NewFolder("for-you-hidden"), "for-you-hidden.json");
        var hidden = new DiscoverHidden(path); var artist = A("77", "Someone");
        var first = new CatalogSong("deezer:77-1", "One", [artist], 1, true);
        Check.True(hidden.Hide(first), "the first 'not for me' hides only that song");
        var other = new CatalogSong("deezer:77-3", "Three", [artist], 1, true);
        Check.True(hidden.Hide(new CatalogSong("deezer:77-2", "Two", [artist], 1, true)) && !hidden.HidesSong(other), "a second dismissal leaves other songs by the artist eligible");
        var again = new DiscoverHidden(path);
        Check.True(again.HidesSong("deezer:77-1") && again.HidesSong("deezer:77-2") && !again.HidesSong(other), "song-only dismissals survive a restart");
        Check.True(again.HidesSong(first with { Id = "apple:10", Explicit = false }), "the same song on another release/provider stays dismissed");
        Check.False(again.HidesSong(first with { Id = "apple:11", Title = "One (Live)" }), "a distinct live recording is not hidden by the studio song preference");
        again.Undo(first); again = new DiscoverHidden(path);
        Check.True(!again.HidesSong(first) && again.HidesSong("deezer:77-2"), "Undo persists and leaves other song preferences intact");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Songs = new[] { "deezer:77-1" }, Artists = new[] { artist.Id }, Counts = new Dictionary<string, int> { [artist.Id] = 2 } }));
        var legacy = new DiscoverHidden(path);
        Check.True(legacy.HidesSong(first.Id) && !legacy.HidesSong(other), "legacy automatic artist exclusions are lifted while dismissed songs stay hidden");
    }

    private static async Task RelatedFromDeezer()
    {
        var folder = Check.NewFolder("for-you-related");
        var http = new DiscoveryTests.Http
        {
            Override = (request, _) =>
            {
                var body = request.RequestUri!.AbsolutePath == "/artist/42/related"
                    ? JsonSerializer.Serialize(new { data = new[] { new { id = 50, name = "Close One", picture_medium = "" }, new { id = 42, name = "North Star", picture_medium = "" } } })
                    : "{\"data\":[]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
        };
        using var catalog = DiscoveryTests.Catalog(folder, http);
        var related = (await catalog.RelatedAsync(DiscoveryTests.Star, CancellationToken.None)).Value;
        Check.True(related.Count == 1 && related[0].Id == "deezer:50" && related[0].Name == "Close One", "Deezer's related artists are read, without the artist itself");
        int calls = http.Calls.Count;
        await catalog.RelatedAsync(DiscoveryTests.Star, CancellationToken.None);
        Check.Equal(calls, http.Calls.Count, "related artists are cached: asking again sends nothing");
        Check.Equal(0, (await catalog.RelatedAsync(new CatalogArtist("apple:75", "North Star"), CancellationToken.None)).Value.Count, "an Apple artist has no related list (nothing guessed)");
    }

    private static async Task ListenBrainz()
    {
        var folder = Check.NewFolder("for-you-listenbrainz");
        int musicBrainz = 0, labs = 0; string? agent = null; bool busy = false;
        var http = new DiscoveryTests.Http
        {
            Override = (request, _) =>
            {
                agent = request.Headers.UserAgent.ToString();
                var uri = request.RequestUri!; var query = Uri.UnescapeDataString(uri.Query);
                string body;
                if (uri.Host == "musicbrainz.org")
                {
                    musicBrainz++;
                    if (busy) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    body = uri.AbsolutePath.Contains("/recording")
                        ? (query.Contains("Lucid Dreams")
                            ? "{\"recordings\":[{\"score\":100,\"title\":\"Lucid Dreams\",\"artist-credit\":[{\"artist\":{\"id\":\"4e4ebde4-0c56-4dec-844b-6c73adcdd92d\",\"name\":\"Juice WRLD\"}}]}]}"
                            : "{\"recordings\":[]}")
                        : "{\"artists\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"Future\"},{\"id\":\"22222222-2222-2222-2222-222222222222\",\"name\":\"Future\"}]}";
                }
                else
                {
                    labs++;
                    body = "[{\"artist_mbid\":\"b95ce3ff-3d05-4e87-9e01-c97b66af13d4\",\"name\":\"Eminem\",\"score\":2800},{\"artist_mbid\":\"4e4ebde4-0c56-4dec-844b-6c73adcdd92d\",\"name\":\"Juice WRLD\",\"score\":9000},{\"artist_mbid\":\"e4a51f17-a57b-47b1-b37b-f552d0f8e9e6\",\"name\":\"Travis Scott\",\"score\":1400}]";
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
        };
        using (var similar = new SimilarArtists(folder, http, musicBrainzPace: TimeSpan.Zero))
        {
            var mbid = await similar.ArtistIdAsync("Juice WRLD", ["Not a real song", "Lucid Dreams"], CancellationToken.None);
            Check.Equal("4e4ebde4-0c56-4dec-844b-6c73adcdd92d", mbid, "MusicBrainz finds the artist through one of the listener's own songs by them");
            Check.True(agent?.Contains("NoctisFreeMusicFinder") == true && agent.Contains("https://"), "MusicBrainz_UserAgentPresent: requests name the app and a contact page");
            int asked = musicBrainz;
            await similar.ArtistIdAsync("Juice WRLD", ["Lucid Dreams"], CancellationToken.None);
            Check.Equal(asked, musicBrainz, "an artist found once is not looked up again");
            Check.Equal(null, await similar.ArtistIdAsync("Future", [], CancellationToken.None), "two artists with the same name and no song to tell them apart: none is guessed");
            var list = await similar.SimilarAsync(mbid!, CancellationToken.None);
            Check.True(list.Select(a => a.Name).SequenceEqual(["Eminem", "Travis Scott"]) && list[0].Score == 1 && list[1].Score == 0.5,
                "ListenBrainz's similar artists, most similar first, without the artist itself");
            await similar.SimilarAsync(mbid!, CancellationToken.None);
            Check.Equal(1, labs, "similar artists are cached");
            busy = true;
            Check.Equal(null, await similar.ArtistIdAsync("Someone New", ["Song"], CancellationToken.None), "MusicBrainz_503_RetriesWithBackoff: a busy MusicBrainz is not an error; the artist is tried next time");
        }
        using (var again = new SimilarArtists(folder, http, musicBrainzPace: TimeSpan.Zero))
        {
            int before = musicBrainz + labs;
            var mbid = await again.ArtistIdAsync("Juice WRLD", ["Lucid Dreams"], CancellationToken.None);
            var list = await again.SimilarAsync(mbid!, CancellationToken.None);
            Check.True(mbid is not null && list.Count == 2 && musicBrainz + labs == before, "the cache survives a restart");
        }
    }

    /// <summary>The For you page: shown before any search, with reasons and "Not for me".</summary>
    public static void RunUi()
    {
        Check.Section("For you UI: the page before a search");
        var folder = Check.NewFolder("for-you-ui");
        File.WriteAllText(Path.Combine(folder, "play_history.json"), JsonSerializer.Serialize(Enumerable.Range(0, 6)
            .Select(i => new { TrackId = Guid.NewGuid(), Title = "T" + i, Artist = "North Star", PlayedAtUtc = DateTime.UtcNow.AddDays(-1).ToString("o"), Skipped = false })));
        var http = new DiscoveryTests.Http
        {
            Override = (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                object body = path switch
                {
                    "/search/artist" when request.RequestUri.Query.Contains("Close") => new { data = new[] { new { id = 50, name = "Close One" } } },
                    "/search/artist" => new { data = new[] { new { id = 42, name = "North Star" } } },
                    "/artist/42/related" => new { data = new[] { new { id = 50, name = "Close One" } } },
                    "/artist/42/top" => new { data = new[] { Song(42, "North Star", 501, "Star Song") } },
                    "/artist/50/top" => new { data = new[] { Song(50, "Close One", 601, "Close Song"), Song(50, "Close One", 602, "Close Two") } },
                    "/search/track" => new { data = new[] { Song(50, "Close One", 601, "Close Song"), Song(50, "Close One", 602, "Close Two") } },
                    _ => new { data = Array.Empty<object>() },
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });
            }
        };
        using var catalog = DiscoveryTests.Catalog(Path.Combine(folder, "catalog"), http);
        var library = new SpotifyPlaylistTests.Library();
        int fetched = 0;
        BotTrack Track(MusicRequest r) => new(r.Title, r.Artist, r.Duration, r.Title + r.Identity.GetHashCode(), async (output, _, ct) => { fetched++; await output.WriteAsync(new byte[] { 1 }, ct); return "mp3"; }) { Request = r };
        using var downloads = new Downloads(_ => { }); downloads.SetPaused(true);
        var actions = new DiscoveryActions(library, downloads, Track, () => Path.Combine(folder, "saved"), () => true);
        var source = new ForYouSource(() => catalog, actions, _ => [], library, folder, Path.Combine(folder, "hidden.json"));
        using var view = new DiscoveryView(() => catalog, _ => { }, null, actions, null, source);
        var window = new Window { Content = view, Width = 820, Height = 740 }; FinderTheme.Apply(window); window.Show();
        Check.Until(() => Text(view).Any(t => t.StartsWith("3 songs you don't have yet")), "For you loads by itself before any search");
        Check.True(view.ForYouMode && Text(view).Contains("Songs for you") && Text(view).Any(t => t.Contains("Because you play North Star")) && Text(view).Any(t => t.Contains("More from North Star")),
            "the songs show why they were picked");
        var notForMe = view.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, "Not for me")).ToList();
        Check.Equal(3, notForMe.Count, "every suggested song can be dismissed");
        notForMe[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Check.True(Text(view).Any(t => t.StartsWith("Hidden “")) && view.GetVisualDescendants().OfType<Button>().Count(b => Equals(b.Content, "Not for me")) == 2, "'Not for me' removes the song at once and offers Undo");
        Button Find(string label) => view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, label) && b.IsEnabled);
        Find("Queue download").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => downloads.Active.Count == 1, "Queue download on a For you song puts it in the download queue");
        view.SelectFilter("Download bots"); view.SelectFilter("For you"); Dispatcher.UIThread.RunJobs();
        Find("Queue download").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => downloads.Active.Count == 2, "after visiting another tab and coming back, For you songs can still be queued");
        Find("Download now").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => fetched == 1, "Download now on a For you song downloads it");
        view.Search("North Star");
        Check.True(!view.ForYouMode, "typing a search leaves For you for the search results");
        var chip = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("finder-primary"));
        ((Avalonia.Controls.IPseudoClasses)chip.Classes).Add(":pointerover"); Dispatcher.UIThread.RunJobs();
        var inside = chip.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault(p => p.Name == "PART_ContentPresenter");
        // The real Noctis theme animates the Button.Background for 60 ms; its initial brush is not its final state.
        Check.Until(() => inside?.Background is Avalonia.Media.ISolidColorBrush under && chip.Background is Avalonia.Media.ISolidColorBrush own && under.Color == own.Color,
            "the chosen filter keeps its colour under the pointer after the native brush transition");
        ((Avalonia.Controls.IPseudoClasses)chip.Classes).Add(":pressed"); Dispatcher.UIThread.RunJobs();
        Check.Until(() => inside?.Background is Avalonia.Media.ISolidColorBrush under && chip.Background is Avalonia.Media.ISolidColorBrush own && under.Color == own.Color,
            "the chosen filter also keeps its colour while pressed");
        window.Close();
        SavedUi();
    }

    private static void SavedUi()
    {
        Check.Section("For you saved page and background refresh");
        var folder = Check.NewFolder("for-you-saved-ui");
        var now = DateTimeOffset.UtcNow;
        new ForYouCache(Path.Combine(folder, "for-you-last.json")).Write(new(1, now, now.AddDays(1), LastFmDiscoverTests.Page()));
        File.WriteAllText(Path.Combine(folder, "play_history.json"), JsonSerializer.Serialize(new[] { new { Artist = "North Star", Title = "Own", PlayedAtUtc = now.UtcDateTime, Skipped = false } }));
        bool block = false;
        var http = new DiscoveryTests.Http { Override = async (r, ct) =>
        {
            if (block) await Task.Delay(Timeout.Infinite, ct);
            object body = r.RequestUri!.AbsolutePath switch
            {
                "/search/artist" => new { data = new[] { new { id = 42, name = "North Star" } } },
                "/artist/42/related" => new { data = new[] { new { id = 50, name = "Close One" } } },
                "/artist/42/top" => new { data = new[] { Song(42, "North Star", 501, "Star Song") } },
                "/search/track" => new { data = new[] { Song(50, "Close One", 601, "Close Song") } },
                _ => new { data = Array.Empty<object>() },
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        } };
        using var catalog = DiscoveryTests.Catalog(Path.Combine(folder, "catalog"), http);
        using var source = new ForYouSource(() => catalog, null, _ => [], new SpotifyPlaylistTests.Library(), folder, Path.Combine(folder, "hidden.json"));
        using var view = new DiscoveryView(() => catalog, _ => { }, forYou: source);
        var window = new Window { Content = view, Width = 800, Height = 650 }; FinderTheme.Apply(window); window.Show();
        Check.Until(() => Text(view).Contains("Saved song"), "a previous session's picks appear immediately without rebuilding");
        Check.Equal(0, http.Calls.Count, "a fresh saved page makes no provider request on open");
        var build = Task.Run(() => source.BuildAsync(true, null, CancellationToken.None));
        Check.Until(() => view.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "New picks ready")), "a background result offers New picks ready");
        Check.True(Text(view).Contains("Saved song") && !Text(view).Contains("Star Song"), "background refresh never replaces rows under the listener's pointer");
        view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "New picks ready")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.Until(() => Text(view).Contains("Star Song") && !Text(view).Contains("Saved song"), "the listener adopts prepared picks with one click");
        source.Forget(); block = true;
        view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Refresh")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check.True(Text(view).Contains("Star Song"), "foreground refresh keeps the previous list visible");
        view.SelectFilter("All"); view.SelectFilter("For you");
        Check.Until(() => Text(view).Contains("Star Song"), "cancelled refresh and tab return keep a usable page");
        window.Close();
    }

    private static object Song(long artist, string name, int id, string title) => new
    {
        id, title, duration = 180, explicit_lyrics = true, track_position = 1, disk_number = 1,
        artist = new { id = artist, name }, contributors = new[] { new { id = artist, name } },
        album = new { id = id * 10, title = title + " album", cover_medium = "", release_date = "2026-01-01", record_type = "single", nb_tracks = 1 },
    };

    private static List<string> Text(Control root) => root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();
}
