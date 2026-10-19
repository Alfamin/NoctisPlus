using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
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
/// The phone album page's colour, Apple Music's: the cover's bottom colour deepens into the page
/// as a thicker layer of its ink would (<see cref="DominantColorExtractor.DeepPageColor"/>), white text on it, the
/// ramp between the two (HeroChrome), the status bar's haze in the cover's TOP colour, and the
/// tint switching off and on (the Album Colours setting: MobileAlbumColoursSettingTests).
/// </summary>
public class DeepPageColorTests : IDisposable
{
    private readonly List<string> _files = new();
    public void Dispose() { foreach (var f in _files) try { File.Delete(f); } catch { } }

    /// <summary>The Silk Sonic cover's bottom band as measured (its mode, 2026-10-14), and from
    /// the iOS reference screenshot (Display P3, converted to sRGB): Apple's cover where it meets
    /// the page and the floor of Apple's page under it.</summary>
    private static readonly Color SilkSonicCover = Color.FromRgb(0xC2, 0x9B, 0x65),
        ApplesCover = Color.FromRgb(0xC2, 0x95, 0x5E), ApplesFloor = Color.FromRgb(0x4C, 0x20, 0x00);

    private static (double L, double C, double Hue) Lch(Color c)
    {
        var (l, a, b) = DominantColorExtractor.ToOkLab(c.R, c.G, c.B);
        return (l, Math.Sqrt(a * a + b * b), (Math.Atan2(b, a) * 180 / Math.PI + 360) % 360);
    }

    /// <summary>OKLab distance: ~0.01 is about one just-noticeable difference.</summary>
    private static double Distance(Color x, Color y)
    {
        var (l1, a1, b1) = DominantColorExtractor.ToOkLab(x.R, x.G, x.B);
        var (l2, a2, b2) = DominantColorExtractor.ToOkLab(y.R, y.G, y.B);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    // ---- The deep page colour -------------------------------------------------------------------

    /// <summary>A beige deepens the way a thicker layer of its ink does: the red holds, the blue
    /// falls away, so the brown is warmer than the beige (Apple's is too: OKLab hue 51° under a
    /// 71° beige), not an olive at the beige's own hue.</summary>
    [Fact]
    public void LightCover_DeepensToTheFloorLightness_TurningWarmer()
    {
        var deep = DominantColorExtractor.DeepPageColor(SilkSonicCover);
        var (l, c, hue) = Lch(deep);
        var (_, _, coverHue) = Lch(SilkSonicCover);
        Assert.Equal(DominantColorExtractor.DeepPageLightness, l, 2);
        Assert.InRange(coverHue - hue, 10, 25);
        Assert.InRange(c, 0.03, 0.09);
    }

    /// <summary>Against Apple's own page: from Apple's beige, under one just-noticeable difference
    /// of Apple's floor (keeping the beige's hue was 0.032 away); from ours, within ~1.</summary>
    [Fact]
    public void SilkSonic_LandsOnApplesPage()
    {
        var fromApples = DominantColorExtractor.DeepPageColor(ApplesCover);
        Assert.True(Distance(fromApples, ApplesFloor) < 0.01, $"{fromApples} vs {ApplesFloor}: {Distance(fromApples, ApplesFloor):F3}");
        var deep = DominantColorExtractor.DeepPageColor(SilkSonicCover);
        Assert.True(Distance(deep, ApplesFloor) < 0.015, $"{deep} vs {ApplesFloor}: {Distance(deep, ApplesFloor):F3}");
    }

    /// <summary>Apple's ramp between the two, sampled down the reference screenshot (sRGB): each
    /// row is the cover's colour deepened to that row's lightness.</summary>
    [Theory]
    [InlineData(0xA7, 0x76, 0x44)]
    [InlineData(0x7D, 0x4C, 0x20)]
    [InlineData(0x5C, 0x2E, 0x0A)]
    public void ApplesRampRows_AreTheCoverDeepened(byte r, byte g, byte b)
    {
        var row = Color.FromRgb(r, g, b);
        var ours = DominantColorExtractor.DeepenTo(ApplesCover, Lch(row).L);
        Assert.True(Distance(ours, row) < 0.01, $"{ours} vs {row}: {Distance(ours, row):F3}");
    }

    [Fact]
    public void DeepenTo_NotLighter_IsTheColourItself_GreyStaysGrey()
    {
        Assert.Equal(SilkSonicCover, DominantColorExtractor.DeepenTo(SilkSonicCover, 0.9));
        var grey = DominantColorExtractor.DeepenTo(Color.FromRgb(0x9A, 0x9A, 0x9A), 0.4);
        Assert.InRange(Math.Abs(grey.R - grey.B), 0, 1);
        Assert.Equal(0.4, Lch(grey).L, 2);
    }

    [Fact]
    public void DarkCover_KeepsItsColour()
    {
        var navy = Color.FromRgb(0x1C, 0x24, 0x4A);
        Assert.True(Distance(DominantColorExtractor.DeepPageColor(navy), navy) < 0.005);
        Assert.Equal(Colors.Black, DominantColorExtractor.DeepPageColor(Colors.Black));
    }

    [Fact]
    public void WhiteCover_GivesANeutralDarkGrey()
    {
        var deep = DominantColorExtractor.DeepPageColor(Colors.White);
        Assert.InRange(Math.Abs(deep.R - deep.G), 0, 2);
        Assert.InRange(Math.Abs(deep.G - deep.B), 0, 2);
        Assert.Equal(DominantColorExtractor.DeepPageLightness, Lch(deep).L, 2);
    }

    /// <summary>A neon cover's page is a deep version of the neon: in gamut by pulling the chroma
    /// in, not by clipping channels (which turns the hue).</summary>
    [Fact]
    public void NeonCover_StaysChromatic_InGamut()
    {
        var neon = Color.FromRgb(0xF6, 0x5A, 0xFB);
        var deep = DominantColorExtractor.DeepPageColor(neon);
        var (l, c, hue) = Lch(deep);
        Assert.Equal(DominantColorExtractor.DeepPageLightness, l, 2);
        Assert.True(c > 0.08, $"chroma {c}");
        Assert.InRange(Math.Abs(hue - Lch(neon).Hue), 0, 3);
    }

    [Fact]
    public void WhiteText_ReadsOnEveryDeepPage()
    {
        var rnd = new Random(11);
        for (var i = 0; i < 500; i++)
        {
            var cover = Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
            var deep = DominantColorExtractor.DeepPageColor(cover);
            Assert.True(PageTint.ContrastRatio(Colors.White, deep) >= 9, $"white on {deep} (from {cover})");
            Assert.False(PageTint.PrefersDarkText(deep));
        }
    }

    // ---- The top band ---------------------------------------------------------------------------

    private static byte[] Fill(int n, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var rgb = new byte[n * n * 3];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            var (r, g, b) = pixel(x, y);
            var i = (y * n + x) * 3;
            rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b;
        }
        return rgb;
    }

    /// <summary>Sky over sand: the top band is the sky, the bottom the sand; the haze under the
    /// status bar takes the top's colour, where before it took the bottom's.</summary>
    [Fact]
    public void TopBand_IsTheSky_BottomBand_TheSand()
    {
        const int n = 64;
        var sky = Color.FromRgb(0x8C, 0xB8, 0xE8);
        var sand = Color.FromRgb(0xC8, 0xA4, 0x78);
        var rgb = Fill(n, (_, y) => y < n * 3 / 4 ? (sky.R, sky.G, sky.B) : (sand.R, sand.G, sand.B));
        Assert.True(Distance(DominantColorExtractor.PickHeroTopColor(rgb, n, n)!.Value, sky) < 0.01);
        Assert.True(Distance(DominantColorExtractor.PickHeroBottomColor(rgb, n, n)!.Value, sand) < 0.01);
    }

    private string WriteSkyOverSand()
    {
        using var bmp = new SKBitmap(200, 200);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(0x8C, 0xB8, 0xE8));
            using var paint = new SKPaint { Color = new SKColor(0xC8, 0xA4, 0x78) };
            canvas.DrawRect(new SKRect(0, 150, 200, 200), paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(Path.GetTempPath(), $"noctis-deep-{Guid.NewGuid():N}.png");
        using (var fs = File.Create(path)) data.SaveTo(fs);
        _files.Add(path);
        return path;
    }

    [Fact]
    public void FromFile_CarriesTheTopColourToo()
    {
        var hero = DominantColorExtractor.ExtractHeroColorsFromFile(WriteSkyOverSand())!;
        Assert.True(Distance(hero.Top, Color.FromRgb(0x8C, 0xB8, 0xE8)) < 0.01, $"top {hero.Top}");
        Assert.True(Distance(hero.Bottom, Color.FromRgb(0xC8, 0xA4, 0x78)) < 0.01, $"bottom {hero.Bottom}");
    }

    // ---- PageTint ---------------------------------------------------------------------------------

    [Fact]
    public async Task HeroTint_KeepsTheCoverColour_AndDeepensThePage()
    {
        var tint = new PageTint(_ => SilkSonicCover, work => Task.FromResult(work())) { HeroFade = true };
        tint.Load(WriteSkyOverSand());
        await tint.Ready;
        Assert.Equal(SilkSonicCover, tint.CoverColor);
        Assert.Equal(SilkSonicCover, tint.TopColor);
        Assert.Equal(DominantColorExtractor.DeepPageColor(SilkSonicCover), tint.TintColor);
        Assert.Equal(tint.TintColor, ((ISolidColorBrush)tint.Background!).Color);
        Assert.False(tint.IsLight);
        Assert.True(tint.HasTint);
    }

    /// <summary>Other tints (the mini player's progress fill) are the colour itself.</summary>
    [Fact]
    public async Task PlainTint_IsTheColourItself()
    {
        var tint = new PageTint(_ => SilkSonicCover, work => Task.FromResult(work()));
        tint.Load(WriteSkyOverSand());
        await tint.Ready;
        Assert.Equal(SilkSonicCover, tint.TintColor);
        Assert.Equal(SilkSonicCover, tint.CoverColor);
        // The desktop's rule still decides the plain tint's text (the beige's luminance is 0.36).
        Assert.Equal(DominantColorExtractor.GetRelativeLuminance(SilkSonicCover) > PageTint.LightTintThreshold, tint.IsLight);
    }

    [Fact]
    public async Task SwitchedOff_ThePageIsPlain_AndBackOn_WithoutAnotherDecode()
    {
        var decodes = 0;
        var tint = new PageTint(_ => { decodes++; return SilkSonicCover; }, work => Task.FromResult(work())) { HeroFade = true };
        tint.Load(WriteSkyOverSand());
        await tint.Ready;
        Assert.True(tint.HasTint);

        tint.Enabled = false;
        Assert.False(tint.HasTint);
        Assert.Null(tint.Background);
        Assert.Null(tint.TintColor);
        Assert.Null(tint.CoverColor);

        tint.Enabled = true;
        Assert.True(tint.HasTint);
        Assert.Equal(DominantColorExtractor.DeepPageColor(SilkSonicCover), tint.TintColor);
        Assert.Equal(1, decodes);
    }

    // ---- The ramp ---------------------------------------------------------------------------------

    [Fact]
    public void Ramp_RunsFromTheCoverToTheFloor_EasingOut()
    {
        var (cover, floor) = (SilkSonicCover, DominantColorExtractor.DeepPageColor(SilkSonicCover));
        Assert.Equal(cover, HeroChrome.RampColour(cover, floor, 0));
        Assert.Equal(floor, HeroChrome.RampColour(cover, floor, 1));
        Assert.Equal(floor, HeroChrome.RampColour(cover, floor, 3));
        // Ease-out: more than half the way at the half-way point (Apple's steps shrink toward the floor).
        var half = Lch(HeroChrome.RampColour(cover, floor, 0.5)).L;
        Assert.True(half < (Lch(cover).L + Lch(floor).L) / 2, $"L at t=0.5: {half}");
    }

    /// <summary>The fade over the cover's foot and the body's ramp are one gradient: the fade
    /// starts clear in the cover's colour and ends opaque in the ramp's colour at the cover's
    /// edge; the body's ramp is pinned in page pixels from the fade's top to PageRampRun below
    /// the cover, cover colour to floor.</summary>
    [Fact]
    public void HeroFade_AndBodyRamp_MeetAtTheCoversEdge()
    {
        var (cover, floor) = (SilkSonicCover, DominantColorExtractor.DeepPageColor(SilkSonicCover));
        const double heroHeight = 412, fade = 185;
        var share = fade / (fade + HeroChrome.PageRampRun);

        var fadeBrush = HeroChrome.HeroFadeBrush(cover, floor, share);
        var first = fadeBrush.GradientStops[0].Color;
        var last = fadeBrush.GradientStops[^1].Color;
        Assert.Equal(0, first.A);
        Assert.Equal((cover.R, cover.G, cover.B), (first.R, first.G, first.B));
        Assert.Equal(255, last.A);
        var atEdge = HeroChrome.RampColour(cover, floor, share);
        Assert.Equal((atEdge.R, atEdge.G, atEdge.B), (last.R, last.G, last.B));
        Assert.True(Lch(atEdge).L < Lch(cover).L && Lch(atEdge).L > Lch(floor).L);

        var body = HeroChrome.PageRampBrush(cover, floor, heroHeight - fade, heroHeight + HeroChrome.PageRampRun);
        Assert.Equal(RelativeUnit.Absolute, body.StartPoint.Unit);
        Assert.Equal(heroHeight - fade, body.StartPoint.Point.Y);
        Assert.Equal(heroHeight + HeroChrome.PageRampRun, body.EndPoint.Point.Y);
        Assert.Equal(cover, body.GradientStops[0].Color);
        Assert.Equal(floor, body.GradientStops[^1].Color);
        Assert.Equal(GradientSpreadMethod.Pad, body.SpreadMethod);
    }

    /// <summary>The page's body carries the ramp once the tint lands: the cover's colour at its
    /// first stop, the deep page colour at its last; the page itself is the floor.</summary>
    [AvaloniaFact]
    public void AlbumPage_BodyCarriesTheRamp_PageIsTheFloor()
    {
        var song = MobileFixtures.Song("One");
        var album = MobileFixtures.MakeAlbum("Record", "Band", song);
        album.ArtworkPath = WriteSkyOverSand();
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album },
            tint: () => new PageTint(_ => SilkSonicCover, work => Task.FromResult(work())));
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();
        var page = MobileFixtures.Find<AlbumPage>(view);
        var deep = DominantColorExtractor.DeepPageColor(SilkSonicCover);
        Assert.Equal(deep, ((ISolidColorBrush)page.Background!).Color);
        var body = Assert.IsType<LinearGradientBrush>(MobileFixtures.Named<StackPanel>(page, "PageBody").Background);
        Assert.Equal(SilkSonicCover, body.GradientStops[0].Color);
        Assert.Equal(deep, body.GradientStops[^1].Color);
        var hero = MobileFixtures.Named<Panel>(page, "Hero");
        var fade = MobileFixtures.Named<Border>(page, "HeroFade");
        Assert.Equal(hero.Height - fade.Height, body.StartPoint.Point.Y, 1);
        Assert.Equal(hero.Height + HeroChrome.PageRampRun, body.EndPoint.Point.Y, 1);
        window.Close();
    }
}
