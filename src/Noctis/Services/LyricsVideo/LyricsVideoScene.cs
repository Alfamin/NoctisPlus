using System.Globalization;
using SkiaSharp;

namespace Noctis.Services.LyricsVideo;

/// <summary>A lyric line laid out for the Reel: its rows, its words' places and its pre-drawn resting looks.</summary>
internal sealed class LaidLine
{
    public required int Index { get; init; }
    public required LyricsVideoLine Line { get; init; }
    public required List<(string Text, float Y)> Rows { get; init; }
    public required List<LaidWord> Words { get; init; }
    public float Top { get; set; }
    public float Height { get; init; }
    public float Width { get; init; }
    /// <summary>The line fully lit, sharp and at each depth blur (<see cref="LyricsVideoScene.BlurLevels"/>), drawn once.</summary>
    public SKImage?[] Looks { get; } = new SKImage?[LyricsVideoScene.BlurLevels.Length];
    public float LookPad { get; set; }
}

/// <summary>One timed word's place in its line.</summary>
internal sealed class LaidWord
{
    public required string Text { get; init; }
    public required float X { get; init; }
    public required float Baseline { get; init; }
    public required float GlyphWidth { get; init; }
    public required LyricsVideoWord Timing { get; init; }
    /// <summary>Letters (grapheme clusters) with their x and width inside the word, for held-note words.</summary>
    public List<(string Text, float X, float Width)>? Letters { get; init; }
}

/// <summary>
/// Owner #243: everything one Reel draws that does not change from frame to frame — the cover (sharp and pre-blurred
/// the way the lyrics page blurs it), the page's own fonts, the lyric lines laid out with their resting looks, and the
/// line motion. Built once, then read by every frame renderer at the same time (SkiaSharp images and typefaces are
/// safe to draw from several threads).
/// </summary>
internal sealed class LyricsVideoScene : IDisposable
{
    /// <summary>The page's depth blur steps at its 46 px type (<see cref="LyricsVideoMotion.BlurFor"/>).</summary>
    public static readonly double[] BlurLevels = [0, 4, 6, 8, 10];

    /// <summary>The page's lyric size; blur and glow are fractions of it.</summary>
    public const float PageLyricSize = 46;

    public LyricsVideoSpec Spec { get; }
    public LyricsVideoGeometry Geometry { get; }
    public LyricsVideoMotion Motion { get; }
    public IReadOnlyList<LaidLine> Lines { get; }
    public SKTypeface Bold { get; }
    public SKTypeface SemiBold { get; }
    public SKImage? Cover { get; }
    /// <summary>The cover as the lyrics page's flowing background uses it: 128 px, blurred once (PreBlurredArtworkConverter).</summary>
    public SKImage Backdrop { get; }
    /// <summary>The same cover washed out further, for the Reel around the page (deeper than the page itself).</summary>
    public SKImage Wash { get; }
    /// <summary>Grain as two saturating byte tables (added, then subtracted), alpha bytes zero; a power of two long.</summary>
    public byte[] GrainAdd { get; }
    public byte[] GrainSub { get; }
    /// <summary>The page panel's and the cover's soft shadows, blurred once (blurring them every frame was most of a frame).</summary>
    public SKImage PanelShadow { get; }
    public float PanelShadowPad { get; }
    public SKImage CoverShadow { get; }
    public float CoverShadowPad { get; }
    public SKFontMetrics LyricMetrics { get; }

    public LyricsVideoScene(LyricsVideoSpec spec)
    {
        Spec = spec;
        Geometry = LyricsVideoGeometry.For(spec.Layout);
        Bold = LyricsVideoFonts.Load("Inter-Bold.ttf", SKFontStyleWeight.Bold);
        SemiBold = LyricsVideoFonts.Load("Inter-SemiBold.ttf", SKFontStyleWeight.SemiBold);
        Cover = LoadCover(spec.CoverPath, 1200);
        Backdrop = MakeBackdrop(Cover, 2.4f);
        Wash = MakeBackdrop(Cover, 6f);
        (GrainAdd, GrainSub) = MakeGrain();
        PanelShadowPad = 70;
        PanelShadow = Shadow(Geometry.Page.Width, Geometry.Page.Height, Geometry.PageRadius, PanelShadowPad, 34, 0.62f);
        CoverShadowPad = Geometry.Cover.Width * 0.2f;
        CoverShadow = Shadow(Geometry.Cover.Width, Geometry.Cover.Height, Geometry.CoverRadius, CoverShadowPad, Geometry.Cover.Width * 0.06f, 0.5f);

        using var paint = LyricPaint();
        LyricMetrics = paint.FontMetrics;
        var lines = new List<LaidLine>(spec.Lines.Count);
        var gap = Geometry.LyricSize * (20f / PageLyricSize);
        float top = 0;
        for (var i = 0; i < spec.Lines.Count; i++)
        {
            var laid = Lay(i, spec.Lines[i], paint);
            laid.Top = top;
            top += laid.Height + gap;
            lines.Add(laid);
        }
        Lines = lines;
        Motion = new LyricsVideoMotion(spec.Lines, lines.Select(l => l.Top).ToArray(),
            16f * Geometry.LyricSize / PageLyricSize, spec.ClipStart);

        // Only lines that can be on screen during the clip get their looks drawn.
        var from = Math.Max(0, spec.FirstLine - 8);
        var to = Math.Min(lines.Count - 1, spec.LastLine + 10);
        for (var i = from; i <= to; i++) DrawLooks(lines[i], paint);
    }

    public SKPaint LyricPaint() => new()
    {
        IsAntialias = true,
        Typeface = Bold,
        TextSize = Geometry.LyricSize,
        Color = SKColors.White,
        SubpixelText = true,
        LcdRenderText = false,
    };

    private float RowHeight => Geometry.LyricSize * 1.21f;

    private LaidLine Lay(int index, LyricsVideoLine line, SKPaint paint)
    {
        var rows = new List<(string, float)>();
        var words = new List<LaidWord>();
        var width = Geometry.LyricsWidth;
        var ascent = -LyricMetrics.Ascent;

        // Tokens: the timed words as sung, or the line's words when it has none.
        var tokens = line.Words is { } timed
            ? timed.Select(w => (Text: w.Text, Timing: (LyricsVideoWord?)w)).ToList()
            : SplitWords(line.Text).Select(t => (Text: t, Timing: (LyricsVideoWord?)null)).ToList();

        var row = new System.Text.StringBuilder();
        float x = 0, maxW = 0;
        var rowIndex = 0;
        foreach (var (text, timing) in tokens)
        {
            var glyph = text.TrimEnd();
            var glyphW = ShareCardRenderer.MeasureTextFallback(paint, glyph);
            var advance = ShareCardRenderer.MeasureTextFallback(paint, text);
            if (x > 0 && x + glyphW > width)
            {
                rows.Add((row.ToString().TrimEnd(), rowIndex * RowHeight));
                maxW = Math.Max(maxW, x);
                row.Clear();
                x = 0;
                rowIndex++;
            }
            var baseline = rowIndex * RowHeight + ascent + (RowHeight - Geometry.LyricSize * 1.21f) / 2;
            if (timing is not null && glyph.Length > 0)
            {
                words.Add(new LaidWord
                {
                    Text = glyph,
                    X = x,
                    Baseline = baseline,
                    GlyphWidth = glyphW,
                    Timing = timing,
                    Letters = timing.IsEmphasis ? Letters(glyph, paint) : null,
                });
            }
            row.Append(text);
            if (!text.EndsWith(' ') && timing is null) row.Append(' ');
            x += timing is null ? advance + ShareCardRenderer.MeasureTextFallback(paint, " ") : advance;
        }
        if (row.Length > 0) rows.Add((row.ToString().TrimEnd(), rowIndex * RowHeight));
        maxW = Math.Max(maxW, x);
        return new LaidLine
        {
            Index = index,
            Line = line,
            Rows = rows,
            Words = words,
            Height = Math.Max(1, rows.Count) * RowHeight,
            Width = Math.Min(width, maxW),
        };
    }

    private static List<(string, float, float)> Letters(string word, SKPaint paint)
    {
        var letters = new List<(string, float, float)>();
        var starts = StringInfo.ParseCombiningCharacters(word);
        var total = ShareCardRenderer.MeasureTextFallback(paint, word);
        for (var i = 0; i < starts.Length; i++)
        {
            var from = starts[i];
            var to = i + 1 < starts.Length ? starts[i + 1] : word.Length;
            var x0 = ShareCardRenderer.MeasureTextFallback(paint, word[..from]);
            var x1 = to < word.Length ? ShareCardRenderer.MeasureTextFallback(paint, word[..to]) : total;
            letters.Add((word[from..to], x0, x1 - x0));
        }
        return letters;
    }

    private static IEnumerable<string> SplitWords(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The line's resting looks: fully lit, sharp and at each depth blur, padded so the blur never clips.</summary>
    private void DrawLooks(LaidLine line, SKPaint paint)
    {
        var k = Geometry.LyricSize / PageLyricSize;
        var pad = (float)Math.Ceiling(BlurLevels[^1] * k * 3 + 8);
        line.LookPad = pad;
        var w = (int)Math.Ceiling(line.Width + pad * 2);
        var h = (int)Math.Ceiling(line.Height + pad * 2);
        var ascent = -LyricMetrics.Ascent;
        for (var level = 0; level < BlurLevels.Length; level++)
        {
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            using var p = paint.Clone();
            // Sigma = the page's blur radius at this size: Apple-like soft neighbours, clearly out of focus.
            var sigma = (float)(BlurLevels[level] * k) * 0.8f;
            if (sigma > 0) p.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma);
            foreach (var (text, y) in line.Rows)
                ShareCardRenderer.DrawTextFallback(canvas, text, pad, pad + y + ascent, p);
            line.Looks[level] = surface.Snapshot();
        }
    }

    internal static SKImage? LoadCover(string? path, int maxSize)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is null) return null;
            var info = codec.Info;
            var scale = Math.Min(1f, (float)maxSize / Math.Max(info.Width, info.Height));
            var size = codec.GetScaledDimensions(scale);
            using var decoded = SKBitmap.Decode(codec, new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (decoded is null) return null;
            if (Math.Max(decoded.Width, decoded.Height) > maxSize)
            {
                var k = (float)maxSize / Math.Max(decoded.Width, decoded.Height);
                using var resized = decoded.Resize(new SKImageInfo((int)(decoded.Width * k), (int)(decoded.Height * k)), new SKSamplingOptions(SKCubicResampler.Mitchell));
                return SKImage.FromBitmap(resized);
            }
            return SKImage.FromBitmap(decoded);
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsVideo:CoverUnreadable", ex.Message);
            return null;
        }
    }

    /// <summary>The page's pre-blurred cover: 128 px on the long side, gaussian sigma 2.4 (PreBlurredArtworkConverter).</summary>
    private static SKImage MakeBackdrop(SKImage? cover, float sigma)
    {
        const int size = 128;
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x1a, 0x14, 0x24));
        if (cover is not null)
        {
            using var small = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
            small.Canvas.DrawImage(cover, new SKRect(0, 0, size, size), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var smallImage = small.Snapshot();
            // A touch more colour than the page (saturation 1.35): Reels are watched on bright phone screens, and the
            // dark scrim otherwise greys the cover out.
            using var blur = new SKPaint
            {
                ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp),
                ColorFilter = SKColorFilter.CreateColorMatrix(Saturation(1.35f)),
            };
            canvas.DrawImage(smallImage, 0, 0, blur);
        }
        return surface.Snapshot();
    }

    private static float[] Saturation(float s)
    {
        const float r = 0.2126f, g = 0.7152f, b = 0.0722f;
        return
        [
            r + (1 - r) * s, g - g * s, b - b * s, 0, 0,
            r - r * s, g + (1 - g) * s, b - b * s, 0, 0,
            r - r * s, g - g * s, b + (1 - b) * s, 0, 0,
            0, 0, 0, 1, 0,
        ];
    }

    /// <summary>Fine film grain, the same every export (a fixed seed): about ±2 levels (enough to dither the dark gradients, little enough to keep the file small), grey (equal in B, G, R), none on alpha.</summary>
    private static (byte[] Add, byte[] Sub) MakeGrain()
    {
        const int length = 1 << 20;
        var add = new byte[length];
        var sub = new byte[length];
        var random = new Random(243);
        for (var i = 0; i < length; i += 4)
        {
            var v = (int)Math.Round((random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 2.4);
            var a = (byte)Math.Max(0, v);
            var s = (byte)Math.Max(0, -v);
            add[i] = add[i + 1] = add[i + 2] = a;
            sub[i] = sub[i + 1] = sub[i + 2] = s;
        }
        return (add, sub);
    }

    /// <summary>A rounded rect's soft shadow, drawn once at half size (it is all blur), padded by <paramref name="pad"/>.</summary>
    private static SKImage Shadow(float w, float h, float radius, float pad, float blur, float darkness)
    {
        const float k = 0.5f;
        var iw = (int)Math.Ceiling((w + pad * 2) * k);
        var ih = (int)Math.Ceiling((h + pad * 2) * k);
        using var surface = SKSurface.Create(new SKImageInfo(iw, ih, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black.WithAlpha((byte)Math.Round(255 * darkness)),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur * k),
        };
        surface.Canvas.DrawRoundRect(SKRect.Create(pad * k, pad * k, w * k, h * k), radius * k, radius * k, paint);
        return surface.Snapshot();
    }

    public void Dispose()
    {
        foreach (var line in Lines)
            foreach (var look in line.Looks) look?.Dispose();
        Cover?.Dispose();
        Backdrop.Dispose();
        Wash.Dispose();
        PanelShadow.Dispose();
        CoverShadow.Dispose();
        Bold.Dispose();
        SemiBold.Dispose();
    }
}

/// <summary>Where things sit in the 1080×1920 Reel for one layout (owner #243). Rects are in Reel pixels unless noted.</summary>
internal sealed record LyricsVideoGeometry
{
    /// <summary>The page panel (side by side) or the whole frame.</summary>
    public required SKRect Page { get; init; }
    public float PageRadius { get; init; }
    /// <summary>Cover rect inside <see cref="Page"/>.</summary>
    public required SKRect Cover { get; init; }
    public float CoverRadius { get; init; }
    /// <summary>Title baseline origin inside <see cref="Page"/>; centered when <see cref="TitleCentered"/>.</summary>
    public required SKPoint Title { get; init; }
    public bool TitleCentered { get; init; }
    public float TitleSize { get; init; }
    public float ArtistSize { get; init; }
    /// <summary>The lyrics viewport inside <see cref="Page"/>.</summary>
    public required SKRect Lyrics { get; init; }
    public float LyricsWidth => Lyrics.Width;
    public float LyricSize { get; init; }
    /// <summary>Where the active line's top sits, as a fraction of the lyrics viewport (the page's 0.32).</summary>
    public float AnchorRatio { get; init; } = 0.32f;
    /// <summary>Soft fade at the viewport's top and bottom edges.</summary>
    public float EdgeFade { get; init; }

    public static LyricsVideoGeometry For(LyricsVideoLayout layout) => layout switch
    {
        // The desktop page as a wide panel, centred in the Reel's safe area (above Instagram's caption, below its
        // header): cover and title in the left column, lyrics in the right, the page's own proportions.
        _ => new LyricsVideoGeometry
        {
            Page = SKRect.Create(32, 600, 1016, 640),
            PageRadius = 36,
            Cover = SKRect.Create(80, 130, 280, 280),
            CoverRadius = 10,
            Title = new SKPoint(220, 130 + 280 + 52),
            TitleCentered = true,
            TitleSize = 28,
            ArtistSize = 22,
            Lyrics = SKRect.Create(420, 0, 560, 640),
            LyricSize = 40,
            AnchorRatio = 0.32f,
            EdgeFade = 70,
        },
    };
}

/// <summary>The lyrics page's own typefaces (Inter, bundled with Avalonia), so the Reel's type is the page's.</summary>
internal static class LyricsVideoFonts
{
    public static SKTypeface Load(string file, SKFontStyleWeight weight)
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://Avalonia.Fonts.Inter/Assets/{file}"));
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            if (SKTypeface.FromStream(copy) is { } face) return face;
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsVideo:FontMissing", $"{file}: {ex.Message}");
        }
        var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        foreach (var family in new[] { "Inter", "Segoe UI", "Helvetica Neue", "Noto Sans" })
            if (SKFontManager.Default.MatchFamily(family, style) is { } face) return face;
        return SKTypeface.CreateDefault();
    }
}
