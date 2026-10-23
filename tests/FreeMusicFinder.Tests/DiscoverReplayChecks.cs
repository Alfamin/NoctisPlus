using System.Security.Cryptography;
using System.Text.Json;

namespace FreeMusicFinder.Tests;

/// <summary>The owner's local replay (Milestone C) on a synthetic data folder whose catalog was saved by a real build:
/// it compares the engines from the saved catalog, sends nothing, writes nothing and prints no titles or names.</summary>
internal static class DiscoverReplayChecks
{
    internal static void Run()
    {
        Check.Section("Discover replay: offline and read-only on a copy of the data folder");
        var data = Check.NewFolder("discover-replay");
        var finder = Path.Combine(data, "plugin-data", "dev.moshi.freemusicfinder");
        var shuffle = Path.Combine(data, "plugin-data", "dev.moshi.trueshuffle");
        Directory.CreateDirectory(finder); Directory.CreateDirectory(shuffle);
        var now = DateTimeOffset.UtcNow;
        File.WriteAllLines(Path.Combine(shuffle, "listens.jsonl"), Enumerable.Range(1, 6).Select(d => JsonSerializer.Serialize(new
        {
            title = "Owned", artist = "North Star", startUtc = now.AddDays(-d).UtcDateTime.ToString("O"), end = "Finished", coverage = 1.0, listenedMs = 180_000,
        })));
        File.WriteAllText(Path.Combine(data, "library.json"), JsonSerializer.Serialize(new[] { new { id = Guid.NewGuid(), artist = "North Star", title = "Owned", album = "Orbit", filePath = "C:/m/1.flac" } }));
        var library = new SpotifyPlaylistTests.Library();
        library.Add(data, "Owned", "North Star");
        var http = new DiscoveryTests.Http();
        using (var catalog = DiscoveryTests.Catalog(Path.Combine(finder, "artist-catalog"), http))
        using (var source = new ForYouSource(() => catalog, null, _ => ["Orbit"], library, data, Path.Combine(finder, "for-you-hidden.json")))
        {
            var build = source.BuildAsync(true, null, CancellationToken.None);
            Check.Until(() => build.IsCompleted, "a real build finishes");
            Check.True(build.Result.Songs.Count > 0, "a real build saves a page and its catalog answers");
        }
        string Hashes() => string.Join("\n", Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => f + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));
        var before = Hashes();
        int calls = http.Calls.Count;
        var output = new StringWriter();
        var replay = DiscoverReplay.ReportAsync(data, output);
        Check.Until(() => replay.IsCompleted, "the replay finishes");
        replay.GetAwaiter().GetResult();
        var report = output.ToString();
        if (Environment.GetEnvironmentVariable("FMF_SHOW_REPLAY") is { Length: > 0 }) Console.WriteLine(report);
        Check.Equal(calls, http.Calls.Count, "the replay sends no request, not even to the test's own handler");
        Check.Equal(before, Hashes(), "the replay changes no file in the copy");
        Check.True(report.Contains("The engines on your saved catalog") && report.Contains("before ") && report.Contains("artists ") && report.Contains("now "), "the three engines run from the saved catalog");
        Check.True(System.Text.RegularExpressions.Regex.IsMatch(report, @"now\s+cards\s+[1-9]"), "the current engine finds songs in the saved catalog");
        Check.False(report.Contains("North Star") || report.Contains("Missing") || report.Contains("Night") || report.Contains("Orbit"), "the report prints counts only, no artist or song names");
    }
}
