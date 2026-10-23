using FreeMusicFinder;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// For you's Discover shelf (Milestone A): recording identity, songs already heard, song-only dismissal and its Undo,
/// exposure that rests songs without blaming their artist, lasting interests beside a recent phase, deliberate versus
/// imported evidence, and an offline replay. Synthetic data only; no network.
/// </summary>
public class DiscoverLearningTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "noctis-discover-" + Guid.NewGuid().ToString("N"));

    public DiscoverLearningTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    private static CatalogArtist A(string id, string name) => new("deezer:" + id, name);
    private static CatalogSong S(string id, string title, CatalogArtist artist, double seconds = 180, bool? @explicit = true) => new(id, title, [artist], seconds, @explicit);

    private sealed class Catalog : IDiscoverCatalog
    {
        public readonly Dictionary<string, CatalogArtist> Artists = new();
        public readonly Dictionary<string, List<string>> Similar = new();
        public readonly Dictionary<string, List<CatalogSong>> Songs = new();
        public Task<CatalogArtist?> FindAsync(TasteSeed seed, CancellationToken ct) => Task.FromResult(Artists.GetValueOrDefault(seed.Name));
        public Task<CatalogArtist?> FindByNameAsync(string name, CancellationToken ct) => Task.FromResult(Artists.GetValueOrDefault(name));
        public Task<List<SimilarArtist>> SimilarAsync(TasteSeed seed, CatalogArtist? artist, CancellationToken ct) =>
            Task.FromResult((Similar.GetValueOrDefault(seed.Name) ?? []).Select((n, i) => new SimilarArtist(n, 1 - i * 0.05)).ToList());
        public Task<List<CatalogSearchSong>> TopSongsAsync(CatalogArtist artist, CancellationToken ct) =>
            Task.FromResult((Songs.GetValueOrDefault(artist.Name) ?? Enumerable.Range(1, 10).Select(i => S(artist.Id + "-" + i, artist.Name + " song " + i, artist)).ToList())
                .Select(s => new CatalogSearchSong(s, null)).ToList());
        public Catalog Add(string id, string name, params string[] similar) { Artists[name] = A(id, name); if (similar.Length > 0) Similar[name] = [.. similar]; return this; }
    }

    private static Task<HashSet<string>> NothingOwned(IReadOnlyList<CatalogSong> songs, CancellationToken ct) => Task.FromResult(new HashSet<string>());

    [Fact]
    public void Identity_JoinsEditionsAndProviders_ButKeepsOtherRecordings()
    {
        var a = A("1", "Artist");
        var studio = S("deezer:1", "Song", a);
        Assert.True(DiscoverIdentity.Same(studio, S("apple:9", "Song (Remastered 2011)", a, 181)), "a remaster on another provider");
        Assert.True(DiscoverIdentity.Same(studio, S("deezer:2", "Song - 2009 Digital Remaster", a, 178)), "a dash remaster tag");
        Assert.True(DiscoverIdentity.Same(studio, S("deezer:3", "Song (Clean)", a, 180, false)), "the clean edition");
        Assert.True(DiscoverIdentity.Same(studio, S("deezer:4", "Song (feat. Guest)", a)), "the guest named in the title");
        Assert.True(DiscoverIdentity.Same(studio, S("deezer:5", "SONG", a, 0)), "another spelling, length unknown");
        foreach (var other in new[] { "Song (Live)", "Song (Remix)", "Song - Radio Edit", "Song (Acoustic)", "Song (Instrumental)", "Song (Sped Up)", "Song - Demo" })
            Assert.False(DiscoverIdentity.Same(studio, S("x", other, a)), other + " is a recording of its own");
        Assert.False(DiscoverIdentity.Same(studio, S("x", "Song", a, 260)), "an unlabelled longer mix is not merged");
        Assert.False(DiscoverIdentity.Same(studio, S("x", "Song", A("2", "Someone Else"))), "a namesake title by another artist");
        var kept = DiscoverIdentity.Distinct(new[] { S("c", "Song (Clean)", a, 180, false), S("e", "Song", a), S("l", "Song (Live)", a, 200) }, s => s);
        Assert.Equal(new[] { "e", "l" }, kept.Select(s => s.Id));
    }

    [Fact]
    public async Task Discover_LeavesOutHeardSongs_NamesakeCredits_AndRepeatsAcrossProviders()
    {
        var catalog = new Catalog().Add("1", "Seed", "Other");
        catalog.Add("2", "Other");
        var other = catalog.Artists["Other"]; var stranger = A("99", "Stranger");
        catalog.Songs["Other"] =
        [
            S("deezer:o1", "Heard Before", other), S("deezer:o2", "Fresh", other), S("apple:o2", "Fresh (Remastered)", other, 181),
            new("deezer:o3", "Compilation Filler", [stranger], 180, true), S("deezer:o4", "Fresh (Live)", other, 240),
        ];
        var familiar = TasteSources.Familiar([new("Other", Now.AddDays(-3), 1.5, "Heard Before", Heard: true)]);
        var result = await Discover.BuildAsync([new("Seed", 4, 10)], catalog, NothingOwned, new DiscoverHidden(Path.Combine(_folder, "h.json")), null, default,
            context: new(familiar, DiscoverLearning.None, Now));
        var titles = result.Songs.Select(s => s.Track.Song.Title).ToList();
        Assert.DoesNotContain("Heard Before", titles);
        Assert.DoesNotContain("Compilation Filler", titles);
        Assert.Single(titles, t => t.StartsWith("Fresh") && !t.Contains("Live"));
        Assert.Contains("Fresh (Live)", titles);
        Assert.False(string.IsNullOrEmpty(result.Batch));
        Assert.All(result.Songs, s => Assert.Equal("Seed", s.Seed));
    }

    [Fact]
    public async Task Discover_OneSeedOrOwnArtistsDoNotFillTheShelf()
    {
        var catalog = new Catalog().Add("1", "Big", "B1", "B2", "B3", "B4", "B5", "B6").Add("2", "Small", "S1", "S2").Add("3", "Third", "T1", "T2").Add("4", "Fourth", "F1");
        foreach (var (n, i) in new[] { "B1", "B2", "B3", "B4", "B5", "B6", "S1", "S2", "T1", "T2", "F1" }.Select((n, i) => (n, i))) catalog.Add("1" + i, n);
        var result = await Discover.BuildAsync([new("Big", 40, 50), new("Small", 1, 1), new("Third", 1, 1), new("Fourth", 1, 1)], catalog, NothingOwned,
            new DiscoverHidden(Path.Combine(_folder, "h.json")), null, default);
        var shelf = result.Songs.Take(Discover.Shelf).ToList();
        Assert.Equal(Discover.Shelf, shelf.Count);
        // A seed's own songs and its neighbours' share one limit.
        Assert.All(shelf.GroupBy(s => s.Seed), g => Assert.True(g.Count() <= Discover.PerSeed, g.Key + " fills " + g.Count()));
        Assert.True(shelf.Count(s => s.Own) <= Discover.OwnArtists);
        Assert.Contains(shelf, s => s.Seed == "Small" && !s.Own);
    }

    [Fact]
    public async Task Exposure_RestsPassedOverSongs_AndDismissalsStaySongLevel()
    {
        var clock = new DateTimeOffset(Now);
        var feedback = new DiscoverFeedback(Path.Combine(_folder, "feedback.json"), () => clock);
        var artist = A("7", "Seen");
        DiscoverShown Card(int i, int position) => new(DiscoverIdentity.Key(S("x", "Seen song " + i, artist)), "deezer:7-" + i, DiscoverIdentity.Artist("Seen"), "Seed", false, position);
        for (int page = 0; page < 3; page++) Assert.True(feedback.Shown("p" + page, [Card(1, 0), Card(2, 1), Card(3, 15)]));
        Assert.False(feedback.Shown("p0", [Card(1, 0)]), "the same page shown again is not a new exposure");
        feedback.Shown("p3", [Card(4, 0)]);
        Assert.True(feedback.Record("p0", Card(2, 1).Key, Card(2, 1).Artist, "Seen song 2", DiscoverAction.Open));
        Assert.False(feedback.Record("p0", Card(2, 1).Key, Card(2, 1).Artist, "Seen song 2", DiscoverAction.Open), "a repeated click counts once");
        var learning = feedback.Learn();
        Assert.Equal(0, learning.Rest(Card(1, 0).Key, clock));
        Assert.Equal(1, learning.Rest(Card(2, 1).Key, clock));
        Assert.Equal(1, learning.Rest(Card(3, 15).Key, clock));
        Assert.Equal(0.8, learning.Rest(Card(4, 0).Key, clock));
        Assert.Equal(1, learning.Rest(Card(1, 0).Key, clock.AddDays(31)));
        Assert.Equal(1, learning.ArtistFactor(Card(1, 0).Artist));

        feedback.Record("p3", Card(4, 0).Key, Card(4, 0).Artist, "Seen song 4", DiscoverAction.Dismiss);
        Assert.Equal(1, feedback.Learn().ArtistFactor(Card(4, 0).Artist));
        feedback.Record("p3", Card(5, 0).Key, Card(5, 0).Artist, "Seen song 5", DiscoverAction.Dismiss);
        feedback.Record("p3", Card(6, 0).Key, Card(6, 0).Artist, "Seen song 6", DiscoverAction.Dismiss);
        var lowered = feedback.Learn().ArtistFactor(Card(4, 0).Artist);
        Assert.InRange(lowered, 0.4, 0.99);
        feedback.Retract("p3", Card(6, 0).Key, DiscoverAction.Dismiss);
        Assert.True(feedback.Learn().ArtistFactor(Card(4, 0).Artist) > lowered, "Undo takes the learning back");

        await feedback.Flushed;
        var reloaded = new DiscoverFeedback(Path.Combine(_folder, "feedback.json"), () => clock);
        Assert.Equal(4, reloaded.Batches.Count);
        clock = clock.AddDays(200);
        Assert.Empty(new DiscoverFeedback(Path.Combine(_folder, "feedback.json"), () => clock).Batches);
        reloaded.Reset(); await reloaded.Flushed;
        Assert.False(File.Exists(Path.Combine(_folder, "feedback.json")));
        Assert.Empty(reloaded.Learn().Ignored);
    }

    [Fact]
    public async Task Discover_AppliesRestingAndDismissalsToTheNextPageOnly()
    {
        var catalog = new Catalog().Add("1", "Seed", "Near");
        catalog.Add("2", "Near");
        var near = catalog.Artists["Near"];
        var hidden = new DiscoverHidden(Path.Combine(_folder, "h.json"));
        var feedback = new DiscoverFeedback(Path.Combine(_folder, "f.json"), () => Now);
        var first = await Discover.BuildAsync([new("Seed", 4, 10)], catalog, NothingOwned, hidden, null, default, context: new(new HashSet<string>(), feedback.Learn(), Now));
        var top = first.Songs.First(s => !s.Own).Track.Song;
        for (int i = 0; i < 3; i++) feedback.Shown("p" + i, [new(DiscoverIdentity.Key(top), top.Id, DiscoverIdentity.Artist(top.Artists[0].Name), "Seed", false, 0)]);
        var dismissed = first.Songs.Where(s => !s.Own).Skip(1).First().Track.Song;
        hidden.Hide(dismissed);
        feedback.Record(first.Batch, DiscoverIdentity.Key(dismissed), DiscoverIdentity.Artist(dismissed.Artists[0].Name), dismissed.Title, DiscoverAction.Dismiss);
        var next = await Discover.BuildAsync([new("Seed", 4, 10)], catalog, NothingOwned, hidden, null, default, context: new(new HashSet<string>(), feedback.Learn(), Now));
        var ids = next.Songs.Select(s => s.Track.Song.Id).ToList();
        Assert.DoesNotContain(top.Id, ids);
        Assert.DoesNotContain(dismissed.Id, ids);
        Assert.Contains(next.Songs, s => s.Track.Song.Artists[0].Id == near.Id);
        Assert.NotEqual(first.Batch, next.Batch);
        // One dismissal: the artist's other songs keep their scores.
        var before = first.Songs.Where(s => s.Track.Song.Artists[0].Id == near.Id && s.Track.Song.Id != top.Id && s.Track.Song.Id != dismissed.Id).ToDictionary(s => s.Track.Song.Id, s => s.Score);
        Assert.All(next.Songs.Where(s => before.ContainsKey(s.Track.Song.Id)), s => Assert.Equal(before[s.Track.Song.Id], s.Score));
    }

    [Fact]
    public void Hidden_IsOneRecording_AcrossEditions_AndClears()
    {
        var path = Path.Combine(_folder, "hidden.json");
        var hidden = new DiscoverHidden(path); var a = A("3", "Artist");
        Assert.True(hidden.Hide(S("deezer:1", "Song", a)));
        var again = new DiscoverHidden(path);
        Assert.True(again.HidesSong(S("apple:7", "Song (Remastered 2015)", a, 182)));
        Assert.False(again.HidesSong(S("apple:8", "Song (Live)", a, 182)));
        Assert.False(again.HidesSong(S("deezer:2", "Other Song", a)));
        again.Clear();
        Assert.False(new DiscoverHidden(path).HidesSong(S("deezer:1", "Song", a)));
    }

    [Fact]
    public void Taste_KeepsALastingInterest_BesideARecentPhase()
    {
        var plays = new List<TastePlay>();
        // Two years of one rap artist, many songs, stopping three months ago.
        for (int d = 90; d < 700; d += 9) plays.Add(new("Old Love", Now.AddDays(-d), 1.5, "Track " + d % 12));
        // A three-week rock phase across sixteen artists.
        for (int band = 0; band < 16; band++)
            for (int d = 0; d < 21; d += 3) plays.Add(new("Rock Band " + band, Now.AddDays(-d), 1.5, "Riff " + d));
        var seeds = Taste.Seeds(plays, [], Now);
        Assert.Contains(seeds, s => s.Name == "Old Love");
        Assert.True(seeds.Count(s => s.Name.StartsWith("Rock Band")) >= 10, "the recent phase still leads");
        Assert.DoesNotContain("oldlove", DiscoverReplay.Baseline(plays, [], Now, 15));
    }

    [Fact]
    public void Taste_ReplaysHaveDiminishingReturns_AndOneDislikeIsNotABan()
    {
        var plays = new List<TastePlay>();
        for (int i = 0; i < 50; i++) plays.Add(new("Looper", Now.AddHours(-i), 1.5, "The One Song"));
        for (int song = 0; song < 10; song++) for (int i = 0; i < 2; i++) plays.Add(new("Broad", Now.AddHours(-i), 1.5, "Song " + song));
        for (int i = 0; i < 6; i++) plays.Add(new("Liked Mostly", Now.AddDays(-i), 1.5, "Good " + i));
        var library = new List<LocalSong> { new(Guid.NewGuid(), "Liked Mostly", "Bad One", "C:/m/bad.flac", TimeSpan.FromMinutes(3), Disliked: true) };
        var seeds = Taste.Seeds(plays, library, Now);
        var weight = seeds.ToDictionary(s => s.Name, s => s.Weight);
        Assert.True(weight["Broad"] > weight["Looper"], "ten liked songs say more than one song looped");
        Assert.Contains("Liked Mostly", weight.Keys);
    }

    [Fact]
    public void Taste_ImportedPlaylistsCountLessThanDeliberateChoices()
    {
        var imported = Enumerable.Range(0, 800).Select(i => new LocalSong(Guid.NewGuid(), "Imported Star", "Song " + i, "C:/i/" + i, TimeSpan.FromMinutes(3))).ToList();
        var chosen = Enumerable.Range(0, 6).Select(i => new LocalSong(Guid.NewGuid(), "Chosen One", "Pick " + i, "C:/c/" + i, TimeSpan.FromMinutes(3), Favorite: i < 3)).ToList();
        var lists = new List<TastePlaylist>
        {
            new(true, imported.Select(s => s.Id).ToList()),
            new(false, chosen.Select(s => s.Id).ToList(), chosen.ToDictionary(s => s.Id, _ => Now.AddDays(-10))),
        };
        var seeds = Taste.Seeds([], [.. imported, .. chosen], Now, playlists: lists).ToDictionary(s => s.Name, s => s.Weight);
        Assert.True(seeds["Chosen One"] > seeds["Imported Star"], $"deliberate {seeds["Chosen One"]} vs imported {seeds["Imported Star"]}");
    }

    [Fact]
    public void Sources_ReadPlaylistsAndFamiliarity_WithoutCountingSlips()
    {
        var shuffle = Path.Combine(_folder, "plugin-data", "dev.moshi.trueshuffle"); Directory.CreateDirectory(shuffle);
        File.WriteAllLines(Path.Combine(shuffle, "listens.jsonl"), [
            "{\"trackId\":\"1\",\"title\":\"Slip\",\"artist\":\"A\",\"startUtc\":\"2026-10-15T10:00:00Z\",\"end\":\"Replaced\",\"coverage\":0.01,\"listenedMs\":1500}",
            "{\"trackId\":\"2\",\"title\":\"Tried\",\"artist\":\"A\",\"startUtc\":\"2026-10-15T10:01:00Z\",\"end\":\"Skipped\",\"coverage\":0.05,\"listenedMs\":9000}",
            "{\"trackId\":\"3\",\"title\":\"Paused Long\",\"artist\":\"A\",\"startUtc\":\"2026-10-15T10:02:00Z\",\"end\":\"Stopped\",\"coverage\":0.1,\"listenedMs\":20000}",
        ]);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_folder, "playlists.json"), System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new { Name = "Mine", Description = "", TrackIds = new[] { id }, TrackAddedAt = new Dictionary<string, DateTime> { [id.ToString()] = Now } },
            new { Name = "Spotify", Description = "Imported from Spotify\nhttps://open.spotify.com/playlist/x", TrackIds = new[] { id } },
            new { Name = "Smart", IsSmartPlaylist = true, TrackIds = new[] { id } },
        }));
        var familiar = TasteSources.Familiar(TasteSources.Load(_folder));
        Assert.DoesNotContain(DiscoverIdentity.Key("Slip", "A"), familiar);
        Assert.Contains(DiscoverIdentity.Key("Tried", "A"), familiar);
        Assert.Contains(DiscoverIdentity.Key("Paused Long", "A"), familiar);
        var lists = TasteSources.Playlists(_folder);
        Assert.Equal(2, lists.Count);
        Assert.False(lists[0].Imported); Assert.True(lists[1].Imported);
        Assert.Equal(Now, lists[0].Added![id]);
    }

    [Fact]
    public void ATaste_ComesBackAfterARest_ButASongHeardProperlyNever()
    {
        // Owner #141: ten seconds of a new album song, skipped, may interest them months later.
        var shuffle = Path.Combine(_folder, "plugin-data", "dev.moshi.trueshuffle"); Directory.CreateDirectory(shuffle);
        File.WriteAllLines(Path.Combine(shuffle, "listens.jsonl"), [
            "{\"trackId\":\"1\",\"title\":\"Tasted\",\"artist\":\"A\",\"startUtc\":\"2026-10-15T10:00:00Z\",\"end\":\"Skipped\",\"coverage\":0.05,\"listenedMs\":10000}",
            "{\"trackId\":\"2\",\"title\":\"Half Minute\",\"artist\":\"A\",\"startUtc\":\"2026-10-15T10:01:00Z\",\"end\":\"Skipped\",\"coverage\":0.15,\"listenedMs\":31000}",
            "{\"trackId\":\"3\",\"title\":\"Short Song\",\"artist\":\"A\",\"startUtc\":\"2026-10-15T10:02:00Z\",\"end\":\"Skipped\",\"coverage\":0.25,\"listenedMs\":25000}",
            "{\"trackId\":\"4\",\"title\":\"Done\",\"artist\":\"A\",\"startUtc\":\"2026-10-15T10:05:00Z\",\"end\":\"Finished\",\"coverage\":1,\"listenedMs\":180000}",
        ]);
        var plays = TasteSources.Load(_folder);
        var soon = TasteSources.Familiar(plays, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains(DiscoverIdentity.Key("Tasted", "A"), soon); // just skipped: not offered again straight away
        var later = TasteSources.Familiar(plays, new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc) + TasteSources.SampleRest + TimeSpan.FromDays(2));
        Assert.DoesNotContain(DiscoverIdentity.Key("Tasted", "A"), later);
        Assert.Contains(DiscoverIdentity.Key("Half Minute", "A"), later); // 30 audible seconds
        Assert.Contains(DiscoverIdentity.Key("Short Song", "A"), later); // a fifth of the song
        Assert.Contains(DiscoverIdentity.Key("Done", "A"), later);
    }

    [Fact]
    public void Replay_ScoresBothMethodsChronologically()
    {
        var plays = new List<TastePlay>();
        var start = Now.AddDays(-400);
        for (int d = 0; d < 400; d++)
        {
            var at = start.AddDays(d);
            if (d % 4 == 0) plays.Add(new("Lifer", at, 1.5, "L" + d % 9));
            if (d < 120 || d > 330) { if (d % 4 == 1) plays.Add(new("Lifer", at.AddHours(1), 1.5, "L" + d % 7)); }
            if (d is > 200 and < 300) for (int b = 0; b < 16; b++) plays.Add(new("Phase " + b, at.AddMinutes(b), 1.5, "P" + d % 5));
        }
        var scores = DiscoverReplay.Run(plays, []);
        Assert.Equal(new[] { "before", "now" }, scores.Select(s => s.Name));
        Assert.All(scores, s => Assert.True(s.Cutoffs > 10));
        Assert.True(scores[1].ReturnRecall >= scores[0].ReturnRecall);
    }
}
