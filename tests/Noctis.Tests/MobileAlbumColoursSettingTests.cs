using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Appearance › Album Colours ("Colour Pages from Artwork"): on by default, saved to
/// settings.json (MobileArtworkPageColors) and read back, and followed at once by the open album
/// and artist pages, which drop to the theme's plain page when it is off.
/// </summary>
public class MobileAlbumColoursSettingTests : IDisposable
{
    private static readonly Color Beige = Color.FromRgb(0xC2, 0x9B, 0x65);

    private readonly List<string> _files = new();
    public void Dispose() { foreach (var f in _files) try { File.Delete(f); } catch { } }

    private string WriteCover()
    {
        using var bmp = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(new SKColor(0xC2, 0x9B, 0x65));
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(Path.GetTempPath(), $"noctis-colours-{Guid.NewGuid():N}.png");
        using (var fs = File.Create(path)) data.SaveTo(fs);
        _files.Add(path);
        return path;
    }

    private static Func<PageTint> Tint(Color color) => () => new PageTint(_ => color, work => Task.FromResult(work()));

    [Fact]
    public async Task OnByDefault_SavedOff_ReadBack_AndFollowedLive()
    {
        var song = MobileFixtures.Song("One");
        var album = MobileFixtures.MakeAlbum("Record", "Band", song);
        album.ArtworkPath = WriteCover();
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album }, tint: Tint(Beige),
            seed: async p =>
            {
                var s = await p.LoadSettingsAsync();
                Assert.True(s.MobileArtworkPageColors);   // the default
                s.MobileArtworkPageColors = false;
                await p.SaveSettingsAsync(s);
            });
        Assert.True(rig.Shell.ArtworkPageColors);        // before the settings are read
        await rig.Shell.InitializeAsync();
        Assert.False(rig.Shell.ArtworkPageColors);       // the saved choice

        rig.Shell.OpenAlbumCommand.Execute(album);
        var vm = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        await vm.Tint.Ready;
        Assert.False(vm.Tint.HasTint);                   // extracted, but a plain page
        Assert.Null(vm.Tint.Background);

        rig.Shell.ArtworkPageColors = true;              // the switch: the open page follows at once
        Assert.True(vm.Tint.HasTint);
        Assert.Equal(DominantColorExtractor.DeepPageColor(Beige), vm.Tint.TintColor);
        await rig.Shell.PendingPlayerBarSave;
        Assert.True((await rig.Persistence.LoadSettingsAsync()).MobileArtworkPageColors);

        rig.Shell.ArtworkPageColors = false;
        Assert.False(vm.Tint.HasTint);
        await rig.Shell.PendingPlayerBarSave;
        Assert.False((await rig.Persistence.LoadSettingsAsync()).MobileArtworkPageColors);
    }

    [Fact]
    public async Task TheArtistPage_FollowsItToo()
    {
        var song = MobileFixtures.Song("One");
        var album = MobileFixtures.MakeAlbum("Record", "Band", song);
        album.ArtworkPath = WriteCover();
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album }, tint: Tint(Beige));
        rig.Shell.ArtworkPageColors = false;
        rig.Shell.OpenArtistCommand.Execute("Band");
        var vm = Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
        await vm.Tint.Ready;
        Assert.False(vm.Tint.HasTint);
        rig.Shell.ArtworkPageColors = true;
        Assert.True(vm.Tint.HasTint);
        Assert.Equal(DominantColorExtractor.DeepPageColor(Beige), vm.Tint.TintColor);
    }

    [AvaloniaFact]
    public void SwitchedOff_TheAlbumPageIsTheThemes_AndTintsWhenSwitchedOn()
    {
        var song = MobileFixtures.Song("One");
        var album = MobileFixtures.MakeAlbum("Record", "Band", song);
        album.ArtworkPath = WriteCover();
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album }, tint: Tint(Beige));
        rig.Shell.ArtworkPageColors = false;
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();
        var page = MobileFixtures.Find<AlbumPage>(view);
        Assert.False(page.Classes.Contains("tinted"));
        Assert.Null(page.Background);
        Assert.Null(MobileFixtures.Named<StackPanel>(page, "PageBody").Background);

        rig.Shell.ArtworkPageColors = true;
        window.UpdateLayout();
        Assert.True(page.Classes.Contains("tinted"));
        var deep = DominantColorExtractor.DeepPageColor(Beige);
        Assert.Equal(deep, ((ISolidColorBrush)page.Background!).Color);
        Assert.Equal(Colors.White, ((ISolidColorBrush)MobileFixtures.Named<TextBlock>(page, "AlbumTitle").Foreground!).Color);
        var body = Assert.IsType<LinearGradientBrush>(MobileFixtures.Named<StackPanel>(page, "PageBody").Background);
        Assert.Equal(Beige, body.GradientStops[0].Color);
        Assert.Equal(deep, body.GradientStops[^1].Color);
        window.Close();
    }
}
