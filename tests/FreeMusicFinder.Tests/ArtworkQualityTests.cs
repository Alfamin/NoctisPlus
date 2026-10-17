using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class ArtworkQualityTests
{
    internal const string Preview = "https://is1-ssl.mzstatic.com/image/thumb/Music/test/100x100bb.jpg";
    internal static byte[] Png(int size)
    {
        using var output = new MemoryStream(); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string name, byte[] bytes)
        {
            var type = System.Text.Encoding.ASCII.GetBytes(name); var length = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length); output.Write(length); output.Write(type); output.Write(bytes);
            uint crc = 0xffffffff; foreach (var b in type.Concat(bytes)) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
            BinaryPrimitives.WriteUInt32BigEndian(length, crc ^ 0xffffffff); output.Write(length);
        }
        var header = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)size); header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[size * 4 + 1]; for (int x = 0; x < size; x++) { row[1 + x * 4] = (byte)(x * 255 / size); row[2 + x * 4] = 100; row[3 + x * 4] = 160; row[4 + x * 4] = 255; }
            for (int y = 0; y < size; y++) zip.Write(row);
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return output.ToArray();
    }
    internal static HttpResponseMessage Image(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("image/png"); return new(HttpStatusCode.OK) { Content = content };
    }
    public static async Task RunAsync()
    {
        Check.Section("progressive artwork: real dimensions, cached previews, failures and cancellation");
        var large = CatalogArtwork.LargeUrl(Preview)!;
        Check.True(large.EndsWith("/1200x1200bb.jpg"), "Apple cover upgrades to 1200 pixels while preserving image identity");
        Check.Equal("https://cdn-images.dzcdn.net/images/cover/hash/1000x1000-000000-80-0-0.jpg", CatalogArtwork.LargeUrl("https://cdn-images.dzcdn.net/images/cover/hash/250x250-000000-80-0-0.jpg"), "Deezer cover upgrades to 1000 pixels with its original image hash");
        Check.Equal(Preview.Replace("100x100", "2400x2400"), CatalogArtwork.LargeUrl(Preview.Replace("100x100", "2400x2400")), "already larger source is never downgraded");
        Check.True(CatalogArtwork.LargeUrl("https://evil.invalid/100x100bb.jpg") is null, "high-resolution URL conversion preserves the CDN allowlist");
        var low = Png(100); var high = Png(1200); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new DiscoveryTests.Http { Override = async (r, ct) => { if (r.RequestUri!.AbsoluteUri == large) { started.TrySetResult(); await finish.Task.WaitAsync(ct); return Image(high); } return Image(low); } };
        using var catalog = DiscoveryTests.Catalog(Check.NewFolder("progressive-art"), http);
        var published = new List<string>(); var progress = catalog.ProgressiveArtworkAsync(Preview, p => { lock (published) published.Add(p); return Task.CompletedTask; }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(!progress.IsCompleted && published.Count == 1 && CatalogArtwork.Dimensions(File.ReadAllBytes(published[0])).Width == 100, "100-pixel preview is published while high-resolution request is still pending");
        var previewPath = published[0]; finish.TrySetResult(); var sharp = await progress;
        Check.True(sharp is not null && published.Count == 2 && CatalogArtwork.Dimensions(File.ReadAllBytes(sharp)).Width == 1200, "ready high-resolution file replaces preview with genuine 1200-pixel image");
        Check.True(File.Exists(previewPath) && previewPath != sharp, "preview is retained at a separate cache path for offline fallback");
        var calls = http.Calls.Count; published.Clear(); await catalog.ProgressiveArtworkAsync(Preview, p => { published.Add(p); return Task.CompletedTask; }, default);
        Check.True(http.Calls.Count == calls && published.Count == 1 && published[0] == sharp, "repeat open immediately publishes sharp cached art, with no thumbnail flash or network request");

        var failedHttp = new DiscoveryTests.Http { Override = (r, _) => Task.FromResult(r.RequestUri!.AbsoluteUri == large ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Image(low)) };
        using var failed = DiscoveryTests.Catalog(Check.NewFolder("progressive-art-failed"), failedHttp);
        var fallback = new List<string>(); var result = await failed.ProgressiveArtworkAsync(Preview, p => { fallback.Add(p); return Task.CompletedTask; }, default);
        Check.True(result is not null && fallback.Count == 1 && CatalogArtwork.Dimensions(File.ReadAllBytes(result)).Width == 100, "failed upgrade keeps usable thumbnail without replacing it with a blank");
        var partialHttp = new DiscoveryTests.Http { Override = (r, _) => Task.FromResult(r.RequestUri!.AbsoluteUri == large ? Image(high) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) };
        using var partial = DiscoveryTests.Catalog(Check.NewFolder("progressive-preview-failed"), partialHttp);
        Check.True(await partial.ProgressiveArtworkAsync(Preview, _ => Task.CompletedTask, default) is not null, "high-resolution cover still works if thumbnail endpoint fails");
        var concurrentHttp = new DiscoveryTests.Http { Override = (r, _) => Task.FromResult(Image(r.RequestUri!.AbsoluteUri == large ? high : low)) };
        using var concurrent = DiscoveryTests.Catalog(Check.NewFolder("progressive-shared"), concurrentHttp);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => concurrent.ProgressiveArtworkAsync(Preview, _ => Task.CompletedTask, default)));
        Check.Equal(2, concurrentHttp.Calls.Count, "concurrent tile/header requests share each cached image rather than downloading it repeatedly");
        using var stop = new CancellationTokenSource(); var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelHttp = new DiscoveryTests.Http { Override = async (r, ct) => { if (r.RequestUri!.AbsoluteUri == large) { begun.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return Image(low); } };
        using var cancelled = DiscoveryTests.Catalog(Check.NewFolder("progressive-cancelled"), cancelHttp);
        var seen = new List<string>(); var cancelledWork = cancelled.ProgressiveArtworkAsync(Preview, p => { seen.Add(p); return Task.CompletedTask; }, stop.Token);
        await begun.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => cancelledWork, "navigating away cancels a pending large-art download");
        Check.True(seen.Count == 1 && File.Exists(seen[0]), "cancelled upgrade preserves the already cached preview");
        var boundedHttp = new DiscoveryTests.Http { Override = (_, _) => Task.FromResult(Image(new byte[4 * 1024 * 1024 + 1])) };
        using var bounded = DiscoveryTests.Catalog(Check.NewFolder("progressive-bounds"), boundedHttp);
        Check.True(await bounded.ArtworkAsync(large, default, highQuality: true) is null, "high-resolution artwork has a 4 MiB response cap");
        boundedHttp.Override = (_, _) => { var huge = Png(1); BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16), 50000); return Task.FromResult(Image(huge)); };
        Check.True(await bounded.ArtworkAsync(large, default, highQuality: true) is null, "image dimensions are bounded before oversized decoded allocations");
    }
}
