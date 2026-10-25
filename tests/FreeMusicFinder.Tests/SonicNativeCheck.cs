using System.Diagnostics;
using System.Text.Json;

namespace FreeMusicFinder.Tests;

/// <summary>Opt-in real decoder/native ONNX parity and resource check. All inputs are benchmark artifacts, no network.</summary>
internal static class SonicNativeCheck
{
    public static async Task RunAsync(string work)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(work, "manifest.json")));
        var reference = JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(Path.Combine(work, "clap-music-int8-vectors.json")))!;
        var modelPath = Path.Combine(work, "models", "clap-music", "audio-model-int8.onnx");
        var export = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(work, "models", "clap-music", "export.json")));
        string sha = export.RootElement.GetProperty("variants").EnumerateArray().First(v => v.GetProperty("file").GetString() == "audio-model-int8.onnx").GetProperty("sha256").GetString()!;
        string exe = Environment.GetEnvironmentVariable("NOCTIS_TEST_FFMPEG") ?? throw new ArgumentException("NOCTIS_TEST_FFMPEG required");
        using var encoder = new SonicEncoder(modelPath, "music-clap-int8-v1", () => exe, sha);
        var self = Process.GetCurrentProcess();
        long before = self.WorkingSet64;
        var rows = new List<object>();
        foreach (var track in doc.RootElement.GetProperty("tracks").EnumerateArray().Where(t => t.TryGetProperty("audio", out _) && reference.ContainsKey(t.GetProperty("key").GetString()!)).Take(8))
        {
            var clock = Stopwatch.StartNew();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await encoder.EncodeAsync(File.ReadAllBytes(track.GetProperty("audio").GetString()!), stop.Token);
            Check.True(result is not null, "native sonic: real preview decodes and embeds");
            double cosine = result?.Similarity(SonicVector.Create(encoder.Model, "preview", reference[track.GetProperty("key").GetString()!])!) ?? 0;
            Check.True(cosine >= .9999, "native sonic: matches Python frontend + encoder (cosine >= .9999)");
            rows.Add(new { Milliseconds = clock.Elapsed.TotalMilliseconds, Cosine = cosine });
        }
        self.Refresh();
        var report = new { Model = encoder.Model, Threads = 2, WorkingSetGrowthMb = (self.WorkingSet64 - before) / 1024.0 / 1024, Rows = rows };
        File.WriteAllText(Path.Combine(work, "native-parity.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        Console.WriteLine($"{Check.Passed} ok, {Check.Failed} wrong");
    }
}
