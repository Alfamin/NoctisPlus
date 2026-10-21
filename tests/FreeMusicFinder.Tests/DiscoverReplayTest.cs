using System.Text.Json;

namespace FreeMusicFinder.Tests;

/// <summary>
/// Explicit opt-in, offline: replays the listener's own history in time order and compares For you's seed artists before
/// and after this change (see <see cref="DiscoverReplay"/>). FMF_DISCOVER_REPLAY = a COPY of Noctis's data folder (only
/// listens.jsonl under plugin-data/dev.moshi.trueshuffle, play_history.json, library.json, playlists.json and, if present,
/// for-you-feedback.json are read).
/// No network, no writes, nothing printed but artist counts and percentages.
/// </summary>
internal static class DiscoverReplayTest
{
    public static void Run(string noctisData)
    {
        var library = new List<LocalSong>();
        var path = Path.Combine(noctisData, "library.json");
        using (var doc = File.Exists(path) ? JsonDocument.Parse(File.ReadAllBytes(path)) : JsonDocument.Parse("[]"))
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object) continue;
                    JsonElement? P(string name) => t.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault();
                    string S(string name) => P(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";
                    library.Add(new LocalSong(Guid.TryParse(S("id"), out var id) ? id : Guid.NewGuid(), S("artist"), S("title"), S("filePath"), TimeSpan.Zero,
                        AlbumArtist: S("albumArtist"), Favorite: P("isFavorite") is { ValueKind: JsonValueKind.True },
                        Rating: P("rating") is { ValueKind: JsonValueKind.Number } r ? r.GetInt32() : 0, Disliked: P("isDisliked") is { ValueKind: JsonValueKind.True }));
                }
        var plays = TasteSources.Load(noctisData);
        var playlists = TasteSources.Playlists(noctisData);
        Console.WriteLine($"Replaying {plays.Count} plays, {library.Count} library songs, {playlists.Count} playlists ({playlists.Count(p => p.Imported)} imported).");
        Console.WriteLine("Library marks and playlists are today's, applied at every cutoff; plays are strictly before each cutoff.");
        foreach (var score in DiscoverReplay.Run(plays, library, playlists)) Console.WriteLine("  " + score);
        // The shelf's own record (for-you-feedback.json in Free Music Finder's data folder), if it was copied too.
        var feedback = new[] { Path.Combine(noctisData, "for-you-feedback.json"), Path.Combine(noctisData, "plugin-data", "dev.moshi.freemusicfinder", "for-you-feedback.json") }
            .FirstOrDefault(File.Exists);
        if (feedback is not null)
        {
            var shelf = new DiscoverFeedback(feedback);
            Console.WriteLine("Shelf: " + shelf.Summary(shelf.Learn(plays, library, playlists)));
        }
        Console.WriteLine("recall: share of artists enjoyed in the next 14 days that were seeds. returning favourites: the same for artists");
        Console.WriteLine("loved months earlier and played again. seeds skipped next: seeds that were only skipped in the next 14 days.");
    }
}
