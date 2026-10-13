using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-12: the phone draws EXACTLY the desktop's icon for every function both apps have.
/// Each phone key below is rasterized next to the desktop glyph it stands for, the way the
/// desktop draws it on its 24 grid (filled, or stroked like LineIcon/MenuV2 for the MenuLine*
/// line icons), and the two must cover the same pixels. A phone glyph redrawn by hand, or a
/// desktop icon changed without its phone twin, fails here.
/// </summary>
public class MobileDesktopIconParityTests
{
    /// <summary>Phone dictionary, phone key, desktop key, desktop stroke width (0 = filled).</summary>
    public static readonly TheoryData<string, string, string, double> Pairs = new()
    {
        // Player bar
        { "PhoneIcons", "PhPlayFill", "PlayIcon", 0 },
        { "PhoneIcons", "PhPauseFill", "PauseIcon", 0 },
        { "PhoneIcons", "PhForwardFill", "FastForwardIcon", 0 },
        { "PhoneIcons", "PhRewindFill", "RewindIcon", 0 },
        { "PhoneIcons", "PhShuffle", "ShuffleIcon", 0 },
        { "PhoneIcons", "PhRepeat", "RepeatAllIcon", 0 },
        { "PhoneIcons", "PhRepeatOne", "RepeatOneIcon", 0 },
        { "PhoneIcons", "PhVolumeLow", "SpeakerLowIcon", 0 },
        { "PhoneIcons", "PhVolumeHigh", "SpeakerHighIcon", 0 },
        { "PhoneIcons", "PhLyrics", "MenuLineLyrics", 2.25 },
        { "PhoneIcons", "PhQueue", "MenuLineQueue", 2.25 },
        { "LibraryIcons", "QueueRepeatIcon", "RepeatAllIcon", 0 },
        { "LibraryIcons", "QueueRepeatOneIcon", "RepeatOneIcon", 0 },
        { "LibraryIcons", "QueueShuffleIcon", "ShuffleIcon", 0 },
        { "LibraryIcons", "QueueListIcon", "MenuLineQueue", 2.25 },
        { "LibraryIcons", "LibOnRepeatIcon", "RepeatAllIcon", 0 },
        // Favourite heart (HeartIcon's pair) and the artist star
        { "PhoneIcons", "PhHeart", "HeartOutlineIcon", 0 },
        { "PhoneIcons", "PhHeartFill", "FavoritesFilledIcon", 0 },
        { "TabIcons", "TabFavoritesIcon", "HeartOutlineIcon", 0 },
        { "TabIcons", "TabFavoritesFilledIcon", "FavoritesFilledIcon", 0 },
        { "PhoneIcons", "PhStar", "MenuLineStar", 2.25 },
        { "PhoneIcons", "PhStarFill", "StarIcon", 0 },
        // Common buttons
        { "PhoneIcons", "PhMore", "MoreDotsIcon", 0 },
        { "LibraryIcons", "LibMoreIcon", "MoreDotsIcon", 0 },
        { "LibraryIcons", "QueueMoreIcon", "MoreDotsIcon", 0 },
        { "PhoneIcons", "PhSearch", "SearchIcon", 0 },
        { "TabIcons", "TabSearchIcon", "SearchIcon", 0 },
        { "TabIcons", "TabSearchFilledIcon", "SearchIcon", 0 },
        { "PhoneIcons", "PhBack", "ChevronLeftIcon", 0 },
        { "PhoneIcons", "PhChevronRight", "ChevronRightIcon", 0 },
        { "LibraryIcons", "LibChevronIcon", "ChevronRightIcon", 0 },
        { "PhoneIcons", "PhCheck", "CheckmarkIcon", 0 },
        { "PhoneIcons", "PhClose", "MenuLineClose", 2.25 },
        { "LibraryIcons", "QueueCloseIcon", "MenuLineClose", 2.25 },
        { "PhoneIcons", "PhPerson", "RailPersonIcon", 0 },
        { "PhoneIcons", "PhSort", "SortIcon", 0 },
        { "PhoneIcons", "PhFilter", "FilterIcon", 0 },
        { "PhoneIcons", "PhPlus", "AddIcon", 0 },
        // Song menu (the phone's long-press sheet)
        { "PhoneIcons", "PhPlayNext", "MenuLinePlayNext", 2.25 },
        { "PhoneIcons", "PhAddToQueue", "MenuLineQueue", 2.25 },
        { "PhoneIcons", "PhAddToPlaylist", "MenuLinePlaylistAdd", 2.25 },
        { "PhoneIcons", "PhTrash", "MenuLineTrash", 2.25 },
        { "PhoneIcons", "PhPin", "MenuLinePin", 2.25 },
        { "PhoneIcons", "PhAlbum", "MenuLineAlbum", 2.25 },
        { "PhoneIcons", "PhArtist", "MenuLineArtist", 2.25 },
        { "LibraryIcons", "LibPinIcon", "PinFilledIcon", 0 },
        { "LibraryIcons", "QueueReorderIcon", "LineGrip", 3.6 },
    };

    private const int Scale = 8; // 24 grid -> 192 px

    [Theory]
    [MemberData(nameof(Pairs))]
    public void PhoneGlyph_DrawsTheDesktopGlyph(string dictionary, string phoneKey, string desktopKey, double strokeWidth)
    {
        var phone = Geometries(Path.Combine("src", "Noctis.Mobile", "Views", dictionary + ".axaml"))[phoneKey];
        var desktop = DesktopGeometries()[desktopKey];

        using var phoneMask = Rasterize(phone, 0);
        using var desktopMask = Rasterize(desktop, strokeWidth);

        int differ = 0, covered = 0;
        for (var y = 0; y < phoneMask.Height; y++)
        for (var x = 0; x < phoneMask.Width; x++)
        {
            var a = phoneMask.GetPixel(x, y).Alpha > 127;
            var b = desktopMask.GetPixel(x, y).Alpha > 127;
            if (a || b) covered++;
            if (a != b) differ++;
        }
        Assert.True(covered > 0, $"{phoneKey} draws nothing");
        // Edge pixels may flip between a stroke and its expanded outline; a different glyph
        // differs by far more than 2% of the shape.
        Assert.True(differ <= covered * 0.02, $"{phoneKey} vs desktop {desktopKey}: {differ} of {covered} pixels differ");
    }

    private static SKBitmap Rasterize(string data, double strokeWidth)
    {
        var nonZero = data.StartsWith("F1", StringComparison.Ordinal);
        if (data.StartsWith("F0", StringComparison.Ordinal) || nonZero) data = data[2..];
        using var path = SKPath.ParseSvgPathData(data.Trim()) ?? throw new InvalidOperationException("unparsable: " + data);
        path.FillType = nonZero ? SKPathFillType.Winding : SKPathFillType.EvenOdd;
        var bitmap = new SKBitmap(24 * Scale, 24 * Scale, SKColorType.Alpha8, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(Scale);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        if (strokeWidth > 0)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)strokeWidth;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.StrokeJoin = SKStrokeJoin.Round;
        }
        canvas.DrawPath(path, paint);
        return bitmap;
    }

    private static Dictionary<string, string> DesktopGeometries()
    {
        var all = Geometries(Path.Combine("src", "Noctis.UI", "Assets", "Icons.axaml"));
        foreach (var (k, v) in Geometries(Path.Combine("src", "Noctis.UI", "Assets", "IconsMenuExtra.axaml")))
            all[k] = v;
        return all;
    }

    private static Dictionary<string, string> Geometries(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        return Regex.Matches(text, "<StreamGeometry x:Key=\"(\\w+)\"\\s*>(.*?)</StreamGeometry>", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => string.Join(' ', m.Groups[2].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
