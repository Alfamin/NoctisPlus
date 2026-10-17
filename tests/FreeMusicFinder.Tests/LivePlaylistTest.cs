using System.Globalization;
using System.Text.Json;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>Optional metadata-only check against an explicitly supplied public playlist.
/// Uses a read-only library snapshot and a fake native playlist store in the test folder.</summary>
internal static class LivePlaylistTest
{
    /// <summary>The local files of a copy of Noctis' library list (library.json), which is only read.</summary>
    internal static SpotifyPlaylistTests.Library Snapshot(string libraryFile)
    {
        var library = new SpotifyPlaylistTests.Library();
        using var json = JsonDocument.Parse(File.ReadAllText(libraryFile));
        foreach (var track in json.RootElement.EnumerateArray())
        {
            var path = track.GetProperty("filePath").GetString()!;
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) continue;
            if (track.TryGetProperty("sourceType", out var source) && source.GetString() is not ("Local" or null)) continue;
            library.Songs.Add(new(Guid.Parse(track.GetProperty("id").GetString()!), track.GetProperty("artist").GetString()!,
                track.GetProperty("title").GetString()!, path, TimeSpan.Parse(track.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture),
                track.TryGetProperty("isExplicit", out var flag) && flag.GetBoolean(),
                AlbumArtist: track.TryGetProperty("albumArtist", out var albumArtist) ? albumArtist.GetString() ?? "" : ""));
        }
        return library;
    }

    public static async Task RunAsync(string? url, string? libraryFile, string? report)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (string.IsNullOrWhiteSpace(libraryFile)) throw new ArgumentException("A read-only library snapshot is required for the live test.");
        var folder = Check.NewFolder("live-playlist");
        var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(libraryFile));
        var library = Snapshot(libraryFile);
        var source1 = new TransferTests.Source("DeezLoad"); var source2 = new TransferTests.Source("Music Hunters");
        var sources = new MusicSources(source1, source2, _ => { }); using var downloads = new Downloads(_ => { });
        using var imports = new SpotifyPlaylists(Path.Combine(folder, "imports.json"), library, downloads, sources.Track);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await imports.ImportAsync(url, timeout.Token);
        var job = imports.Jobs.Single();
        if (!job.Created) throw new InvalidOperationException("Live playlist check: " + job.Status);
        var missing = job.Manifest!.Entries.Where(e => !job.Matches.ContainsKey(e.Position)).ToArray();
        var queued = await imports.QueueMissingAsync(Path.Combine(folder, "isolated-staging"));
        var result = new
        {
            name = job.Manifest.Name, total = job.Total, localMatches = job.Found, missingEntries = missing.Length,
            unavailableMetadata = missing.Count(e => e.Request is null), uniqueMissingQueued = queued,
            validLocalFiles = library.Songs.Count, realAudioRequests = source1.Calls + source2.Calls,
            originalOrderRetained = library.Lists[job.LibraryId].SequenceEqual(job.Matches.OrderBy(m => m.Key).Select(m => m.Value)),
            missing = missing.Select(e => new { e.Position, e.Request?.Artist, e.Request?.Title, e.Issue }),
        };
        if (report is not null) AtomicJson.Write(report, result);
        Console.WriteLine($"LIVE PLAYLIST RESULT: {job.Manifest.Name} · {job.Total} total · {job.Found} local matches · {missing.Length} missing entries · {queued} unique staged requests · {source1.Calls + source2.Calls} audio requests.");
        Check.True(downloads.Active.All(d => d.Held), "live test stages metadata without audio");
        Check.True(before.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(libraryFile))), "live test keeps user's library snapshot byte-identical");
        Check.True(result.originalOrderRetained, "live playlist matches preserve Spotify order");
    }

    /// <summary>
    /// Optional, and without any network: matches saved imports (a copy of the plugin's
    /// spotify-playlists.json) against a library snapshot again and prints what changed. Both files
    /// are only read; the work is done on a copy in the test folder and a stand-in playlist store.
    /// </summary>
    public static async Task ReplayAsync(string importsFile, string? libraryFile)
    {
        if (string.IsNullOrWhiteSpace(libraryFile)) throw new ArgumentException("FMF_LIBRARY_SNAPSHOT (a library snapshot) is required with FMF_REPLAY_IMPORTS.");
        Check.Section("replay: saved playlist imports against the library snapshot, offline");
        var hashes = new[] { importsFile, libraryFile }.Select(f => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))).ToArray();
        var folder = Check.NewFolder("replay-playlists"); var copy = Path.Combine(folder, "imports.json");
        File.Copy(importsFile, copy);
        var library = Snapshot(libraryFile);
        var known = library.Songs.Select(s => s.Id).ToHashSet();
        var saved = JsonSerializer.Deserialize<List<SpotifyPlaylist>>(File.ReadAllText(copy))!;
        foreach (var job in saved.Where(j => j.Created)) library.Lists[job.LibraryId] = job.Applied.ToList();
        var source1 = new TransferTests.Source("DeezLoad"); var source2 = new TransferTests.Source("Music Hunters");
        var sources = new MusicSources(source1, source2, _ => { }); using var downloads = new Downloads(_ => { });
        using var imports = new SpotifyPlaylists(copy, library, downloads, sources.Track);
        await imports.CheckLibraryAsync();
        var songs = library.Songs.ToDictionary(s => s.Id);
        foreach (var (job, old) in imports.Jobs.Zip(saved))
        {
            var kept = old.Matches.Where(m => known.Contains(m.Value)).ToArray();
            var lost = kept.Count(m => job.Matches.GetValueOrDefault(m.Key) != m.Value);
            var gained = job.Matches.Where(m => !old.Matches.ContainsKey(m.Key)).OrderBy(m => m.Key).ToArray();
            Console.WriteLine($"  {job.Manifest?.Name}: {job.Total} entries · matched before {old.Matches.Count} ({kept.Length} of them in this snapshot) · now {job.Found} · new {gained.Length} · changed or lost {lost} · {job.Status}");
            foreach (var match in gained)
            {
                var request = job.Manifest!.Entries[match.Key - 1].Request!; var song = songs[match.Value];
                Console.WriteLine($"      + {match.Key}. {request.Artist} | {request.Title}  =  {song.Artist} | {song.Title}");
            }
            foreach (var entry in job.Manifest!.Entries.Where(e => e.Request is not null && !job.Matches.ContainsKey(e.Position) && !job.MissingReasons.GetValueOrDefault(e.Position, "").StartsWith("No matching")))
                Console.WriteLine($"      ? {entry.Position}. {entry.Request!.Artist} | {entry.Request.Title}: {job.MissingReasons[entry.Position]}");
            Check.Equal(0, lost, $"{job.Manifest.Name}: every song matched before is still matched to the same file");
        }
        Check.Equal(0, source1.Calls + source2.Calls, "nothing was asked of a bot");
        Check.True(hashes.Zip(new[] { importsFile, libraryFile }).All(p => p.First.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p.Second)))), "the saved imports and the library snapshot were only read");
    }
}
