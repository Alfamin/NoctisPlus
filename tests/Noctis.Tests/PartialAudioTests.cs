using FreeMusicFinder;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #100: a download stopped part way keeps what it had; the next download of the same file goes on from there
/// and ends with exactly the same bytes.</summary>
[Collection("PartialAudio")] // the store's folder is static
public class PartialAudioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-partial-" + Guid.NewGuid().ToString("N"));
    private readonly string? _was = PartialAudio.Folder;
    public PartialAudioTests() { Directory.CreateDirectory(_dir); PartialAudio.Folder = Path.Combine(_dir, "kept"); }
    public void Dispose() { PartialAudio.Folder = _was; try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static byte[] Song(int size) { var bytes = new byte[size]; new Random(7).NextBytes(bytes); return bytes; }

    [Fact]
    public async Task AStoppedTransfer_IsKept_AndTheNextOneGoesOnFromThere_WithTheSameBytes()
    {
        const int chunk = AudioFileTransfer.ChunkSize;
        var song = Song(chunk * 10 + 999);
        var partial = Path.Combine(_dir, "a.part");
        var slot = new PartialAudio.Slot { DocumentId = 42, Size = song.Length };
        using var stop = new CancellationTokenSource();
        var asked = new List<long>();
        Task<byte[]> Read(long offset, int count, CancellationToken ct)
        {
            lock (asked) asked.Add(offset);
            if (offset >= chunk * 8) { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(song.AsSpan((int)offset, (int)Math.Min(count, song.Length - offset)).ToArray());
        }
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AudioFileTransfer.CopyAsync(output, song.Length, Read, null, stop.Token, kept: slot));
        Assert.InRange(slot.Prefix, chunk * 4, chunk * 8);
        PartialAudio.Keep(partial, slot);

        var resumed = Path.Combine(_dir, "b.part");
        asked.Clear();
        await using (var output = new FileStream(resumed, FileMode.Create, FileAccess.ReadWrite))
        {
            var start = await PartialAudio.RestoreAsync(42, song.Length, output, CancellationToken.None);
            Assert.Equal(slot.Prefix, start);
            Task<byte[]> All(long offset, int count, CancellationToken ct)
            { lock (asked) asked.Add(offset); return Task.FromResult(song.AsSpan((int)offset, (int)Math.Min(count, song.Length - offset)).ToArray()); }
            await AudioFileTransfer.CopyAsync(output, song.Length, All, null, CancellationToken.None, start: start);
        }
        Assert.Equal(song, await File.ReadAllBytesAsync(resumed));
        Assert.DoesNotContain(asked, o => o < slot.Prefix); // the kept part was not asked for again
        Assert.Empty(Directory.EnumerateFiles(PartialAudio.Folder!)); // used up
    }

    [Fact]
    public async Task NothingKept_MeansAWholeDownload()
    {
        await using var output = new MemoryStream();
        Assert.Equal(0, await PartialAudio.RestoreAsync(7, 1000, output, CancellationToken.None));
        PartialAudio.Keep(Path.Combine(_dir, "missing.part"), new PartialAudio.Slot { DocumentId = 7, Size = 1000, Prefix = 0 });
        Assert.False(Directory.Exists(PartialAudio.Folder) && Directory.EnumerateFiles(PartialAudio.Folder!).Any());
    }
}
