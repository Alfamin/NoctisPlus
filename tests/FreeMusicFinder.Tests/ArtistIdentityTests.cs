using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class ArtistIdentityTests
{
    internal static void Seed<T>(string folder, string key, T value) => File.WriteAllText(Path.Combine(folder,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json"), JsonSerializer.Serialize(new { Version = 1, SavedAt = DateTimeOffset.UtcNow, Value = value }));

    public static async Task RunAsync()
    {
        Check.Section("artist identity: namesakes, wrong legacy pins and recording evidence");
        var actual = new CatalogArtist("apple:271256", "Drake", "Hip-Hop/Rap");
        var namesake = new CatalogArtist("apple:1147300457", "Drake", "Dance");
        CatalogRelease Release(string id, string title, CatalogArtist by) => new(id, title, by, "2026-01-01", "album", 14, true);
        var folder = Check.NewFolder("artist-identity");
        Seed(folder, "search:DRAKE", new[] { namesake, actual });
        Seed(folder, "artist:" + actual.Id, new CatalogProfile(actual, [], [Release("apple:1440762388", "Thank Me Later", actual), Release("apple:1418213110", "Scorpion", actual), Release("apple:991", "Late Night Sessions", namesake)]));
        Seed(folder, "artist:" + namesake.Id, new CatalogProfile(namesake, [], [Release("apple:992", "Late Night Sessions", namesake)]));
        var http = new DiscoveryTests.Http { Offline = true }; using var catalog = DiscoveryTests.Catalog(folder, http, () => DateTimeOffset.UtcNow.AddDays(2));
        var selected = await catalog.IdentifyAsync("Drake", ["Thank Me Later (Int'l Version)", "Scorpion"], namesake, default);
        Check.Equal(actual.Id, selected.Artist?.Id, "actual Drake wins by owned albums even when namesake is first and incorrectly saved");
        Check.True(selected.LibraryMatch && selected.Albums.Count == 2, "artist choice records real library-album evidence");
        Check.Equal(0, http.Calls.Count, "cached identity correction works offline without requests even after the search TTL expires");
        Check.True(!(await catalog.ArtistAsync(actual, false, default)).Value.Releases.Any(r => r.Title == "Late Night Sessions"), "same-name foreign-ID release in a legacy cache is excluded");

        var coldFolder = Check.NewFolder("artist-identity-cold");
        var coldHttp = new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
        { results = new[] { new { wrapperType = "collection", collectionId = 500, collectionName = "Thank Me Later", artistId = 271256, artistName = "Drake", trackCount = 14, collectionExplicitness = "explicit" } } })) }) };
        using var cold = DiscoveryTests.Catalog(coldFolder, coldHttp);
        var proof = await cold.IdentifyAsync("Drake", ["Thank Me Later"], namesake, default, [namesake, actual]);
        Check.Equal(actual.Id, proof.Artist?.Id, "first-time disambiguation links known album to stable artist ID");
        Check.True(proof.LibraryMatch && coldHttp.Calls.Count == 1, "targeted identity lookup uses one album search, without loading every namesake catalog");
        await cold.IdentifyAsync("Drake", ["Thank Me Later"], namesake, default, [namesake, actual]);
        Check.Equal(1, coldHttp.Calls.Count, "identity evidence search is reused from cache");

        var ambiguousHttp = new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
        { results = new[] { new { wrapperType = "collection", collectionId = 500, collectionName = "Shared Album", artistId = 271256, artistName = "Drake" }, new { wrapperType = "collection", collectionId = 501, collectionName = "Shared Album", artistId = 1147300457, artistName = "Drake" } } })) }) };
        using var ambiguous = DiscoveryTests.Catalog(Check.NewFolder("artist-identity-ambiguous"), ambiguousHttp);
        Check.True((await ambiguous.IdentifyAsync("Drake", ["Shared Album"], namesake, default, [namesake, actual])).Artist is null, "equal album evidence never guesses the first or saved namesake");
        var before = ambiguousHttp.Calls.Count;
        Check.True((await ambiguous.IdentifyAsync("Drake", ["Unknown Album", "Unreleased", "Greatest Hits"], namesake, default, [namesake, actual])).Artist is null, "generic/unreleased album tags do not falsely prove an identity");
        Check.Equal(before, ambiguousHttp.Calls.Count, "generic tags trigger no speculative catalog searches");
        using var offline = DiscoveryTests.Catalog(Check.NewFolder("artist-identity-offline"), new() { Offline = true });
        var unavailable = await offline.IdentifyAsync("Drake", ["Thank Me Later"], namesake, default, [namesake, actual]);
        Check.True(unavailable.Artist is null && unavailable.Notice.Contains("Could not verify"), "failed verification does not silently reuse an incorrect saved name pin");
        var underground = new CatalogArtist("apple:99", "Tiny artist", "Hip-Hop/Rap");
        Check.Equal(underground.Id, (await offline.IdentifyAsync("Tiny artist", [], null, default, [underground])).Artist?.Id, "unique underground artists need no popularity or follower threshold");
        Seed(folder, "artist:apple:55", new CatalogProfile(namesake, [], []));
        Check.True(await catalog.SavedArtistAsync(new("apple:55", "Drake"), default) is null, "cache file with a different artist identity is not trusted");
        await Check.ThrowsAsync<System.IO.InvalidDataException>(() => catalog.ArtistAsync(new("apple:55", "Drake"), false, default), "wrong cached artist ID is rejected before display");
        Seed(folder, "album:apple:123", new CatalogAlbum(Release("apple:123", "Thank Me Later", namesake), []));
        await Check.ThrowsAsync<InvalidDataException>(() => catalog.AlbumAsync(Release("apple:123", "Thank Me Later", actual), false, default), "cached release with a foreign artist ID is rejected before opening songs");
        await MergedProviderAsync();
    }

    private static async Task MergedProviderAsync()
    {
        var merged = new CatalogArtist("deezer:11235313", "AveNoir");
        var actual = new CatalogArtist("apple:1607710313", "Avenoir", "R&B/Soul");
        var other = new CatalogArtist("apple:6802208621", "Avenoir", "Ambient");
        CatalogSearchSong Song(CatalogArtist artist, string id, string title, string album) => new(
            new(id, title, [artist], 180, true), new("apple:900", album, artist, "2026-01-01", "album", 10));
        var folder = Check.NewFolder("merged-provider-identity");
        var evidence = new List<CatalogSearchSong>
        {
            Song(actual, "apple:1", "Shameless", "NOIRE"), Song(actual, "apple:2", "Favour", "Mirage"),
            Song(other, "apple:3", "Tonight", "Unrelated release")
        };
        Seed(folder, "identity-recordings:apple:AVENOIR", evidence);
        var http = new DiscoveryTests.Http { Offline = true };
        using var catalog = DiscoveryTests.Catalog(folder, http);
        Check.Equal(actual.Id, (await catalog.SeparateNamesakesAsync(merged, ["NOIRE"], ["Shameless", "Favour"], default)).Id,
            "independent recording evidence separates musicians merged under ONE Deezer id");
        Check.Equal(0, http.Calls.Count, "cached cross-provider identity verification works offline without delaying Search");
        Check.Equal(merged.Id, (await catalog.SeparateNamesakesAsync(merged, ["Unreleased"], ["Unknown song", "Unknown two"], default)).Id,
            "no matching evidence never substitutes a famous or first namesake");
        evidence.Add(Song(other, "apple:4", "Shameless", "NOIRE"));
        evidence.Add(Song(other, "apple:5", "Favour", "Mirage"));
        var tiedFolder = Check.NewFolder("merged-provider-tied"); Seed(tiedFolder, "identity-recordings:apple:AVENOIR", evidence);
        using var tied = DiscoveryTests.Catalog(tiedFolder, new() { Offline = true });
        Check.Equal(merged.Id, (await tied.SeparateNamesakesAsync(merged, ["NOIRE"], ["Shameless", "Favour"], default)).Id,
            "equal independent recording evidence does not guess a namesake");
        Check.Equal(actual.Id, (await catalog.SeparateNamesakesAsync(actual, [], [], default)).Id,
            "a separated Apple identity never crosses back into a merged provider");
        using var offline = DiscoveryTests.Catalog(Check.NewFolder("merged-provider-offline"), new() { Offline = true });
        Check.Equal(merged.Id, (await offline.SeparateNamesakesAsync(merged, ["NOIRE"], ["Shameless", "Favour"], default)).Id,
            "verification outage retains the selected identity and local library");
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => catalog.SeparateNamesakesAsync(merged, [], [], stopped.Token),
            "cancelled artist navigation does not continue verification");

        var topHttp = new DiscoveryTests.Http(); using var top = DiscoveryTests.Catalog(Check.NewFolder("artist-top-songs"), topHttp);
        var songs = await top.ArtistSongsAsync(DiscoveryTests.Star, false, default);
        Check.Equal(3, songs.Value.Count, "artist top songs include online recordings absent from the library");
        Check.True(songs.Value.All(t => t.Song.Artists.Any(a => a.Id == DiscoveryTests.Star.Id)), "online top songs retain stable artist credits");
        await top.ArtistSongsAsync(DiscoveryTests.Star, false, default);
        Check.Equal(1, topHttp.Calls.Count, "See all reuses the artist-song catalog cache");
        var foreignHttp = new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { id = 9, title = "Tonight", artist = new { id = 999, name = "North Star" } } } })) }) };
        using var foreign = DiscoveryTests.Catalog(Check.NewFolder("artist-foreign-top"), foreignHttp);
        Check.Equal(0, (await foreign.ArtistSongsAsync(DiscoveryTests.Star, false, default)).Value.Count,
            "same-name top song credited to another provider id never enters the selected catalog");
    }
}
