using System.Net.Http;
using System.Text.Json;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner #209 (opt-in, real network): which of Unison and Kugou has word timing for the owner's songs, saved for the
/// scratch comparison against lrc.red and WordLyrics. FIT200_SURVEY = an output folder; reads D:\music (lengths only).
/// </summary>
public class OnlineSourceSurveyTests
{
    [Fact]
    public async Task LiveUnisonAndKugouSurvey()
    {
        var output = Environment.GetEnvironmentVariable("FIT200_SURVEY");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        var index = JsonDocument.Parse(File.ReadAllText(@"C:\Users\Alfamine\lyrics-compare\lrcred-cache\index.json")).RootElement;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var unison = new UnisonLyricsService(http);
        var kugou = new KugouLyricsService(http);
        using var log = new StreamWriter(Path.Combine(output, "survey.jsonl"), append: false);
        foreach (var song in index.EnumerateObject())
        {
            var parts = song.Name.Split(" - ", 3);
            if (parts.Length < 3) continue;
            var (artist, title) = (parts[1], parts[2]);
            var audio = new[] { ".flac", ".mp3", ".m4a" }.Select(e => Path.Combine(@"D:\music", song.Name + e)).FirstOrDefault(File.Exists);
            if (audio == null) continue;
            double seconds;
            using (var tags = TagLib.File.Create(audio)) seconds = tags.Properties.Duration.TotalSeconds;
            async Task<string> Ask(string source, Func<Task<Noctis.Models.LrcLibResult?>> search)
            {
                try
                {
                    var result = await search();
                    if (result == null) return "none";
                    if (OnlineWordTiming.WordTimed(result, seconds) is { } ttml)
                    {
                        File.WriteAllText(Path.Combine(output, song.Name + "." + source + ".ttml"), ttml);
                        return "word";
                    }
                    return result.HasSyncedLyrics ? "line" : result.HasLyrics ? "plain" : "none";
                }
                catch (LyricsProviderException) { return "error"; }
            }
            var u = await Ask("unison", () => unison.SearchLyricsAsync(artist, title, seconds));
            var k = await Ask("kugou", () => kugou.SearchLyricsAsync(artist, title, seconds));
            log.WriteLine(JsonSerializer.Serialize(new { name = song.Name, seconds, lrcred = song.Value.GetProperty("found").GetBoolean(), unison = u, kugou = k }));
            log.Flush();
            await Task.Delay(700);   // gentle: well under Unison's 120 requests a minute
        }
    }
}
