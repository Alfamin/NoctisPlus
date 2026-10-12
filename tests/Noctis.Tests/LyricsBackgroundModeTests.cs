using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Bugs found in the lyrics page background (Artwork / Solid / Gradient), each pinned by the
/// test that failed before its fix.
/// </summary>
public class LyricsBackgroundModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lbgmode-" + Guid.NewGuid().ToString("N"));

    public LyricsBackgroundModeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* plugin contexts may hold files briefly */ }
    }

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    /// <summary>settings.json in memory: every load is a fresh copy, as on disk.</summary>
    private sealed class SettingsStore : TestPersistenceService
    {
        private string _json = JsonSerializer.Serialize(new AppSettings());
        public AppSettings Current { get => JsonSerializer.Deserialize<AppSettings>(_json)!; set => _json = JsonSerializer.Serialize(value); }
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Current);
        public override Task SaveSettingsAsync(AppSettings settings) { Current = settings; return Task.CompletedTask; }
    }

    /// <summary>A one-colour cover on disk with its colours warmed, as a track change leaves them.</summary>
    private string Cover(string hex)
    {
        var path = Path.Combine(_dir, hex.TrimStart('#') + "-" + Guid.NewGuid().ToString("N")[..6] + ".png");
        using (var bmp = new SKBitmap(64, 64))
        {
            bmp.Erase(SKColor.Parse(hex));
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        Assert.True(DominantColorExtractor.WarmFromFile(path));
        return path;
    }

    private static PlayerViewModel NewPlayer(string? coverPath = null)
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        player.CurrentTrack = new Track { Title = "Soy Peor", Artist = "Bad Bunny", Album = "Soy Peor - Single" };
        if (coverPath != null)
        {
            player.AlbumArt = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            player.CurrentArtPath = coverPath;
        }
        return player;
    }

    private static LyricsViewModel NewVm(PlayerViewModel player, TestPersistenceService? persistence = null)
        => new(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            persistence ?? new TestPersistenceService(), new FakeLibraryService());

    private static Color ColorOf(IBrush brush) => Assert.IsType<SolidColorBrush>(brush, exactMatch: false).Color;

    /// <summary>
    /// Bug: the Artwork mode text palette was picked for the cover under a 40% scrim
    /// (ArtworkScrimAlpha = 0x66) while the page has drawn a 60% one (#99000000) since
    /// v1.1.13. A light grey cover then got the "medium" palette (dark button wash, pale
    /// accent) although what shows is a dark grey (200 x 0.4 = 80, luminance 0.31).
    /// </summary>
    [AvaloniaFact]
    public void ArtworkMode_PicksTheTextPalette_UnderThePagesScrim()
    {
        var vm = NewVm(NewPlayer(Cover("#C8C8C8")));
        Assert.True(vm.IsColorModeArtwork);

        Assert.Equal(Color.Parse("#33FFFFFF"), ColorOf(vm.LyricsBtnBg));
        Assert.Equal(Color.Parse("#B0FFFFFF"), ColorOf(vm.LyricsSecondaryFg));
    }
}
