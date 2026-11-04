using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Noctis.Controls;
using Noctis.Converters;
using Noctis.Helpers;
using SkiaSharp;

namespace Noctis.Services.LyricsVideo;

/// <summary>
/// Owner #243: draws one frame of the lyrics Reel at a track time. A pure function of that time (and the beat pulse
/// handed in), so frames can be drawn on several threads at once — one renderer per thread, all sharing one
/// <see cref="LyricsVideoScene"/>. The page is the lyrics page as it moves live: the same flowing cover, line glide,
/// cascade, depth blur, word sweep with its soft edge, word float and held-note swell and glow, from the same motion
/// helpers the page uses. Moving lyrics get motion blur (several moments averaged, only over the lyrics, where the
/// motion is), the intro and outro over the whole frame; every frame gets a fine grain, added while the pixels are
/// copied out, that also keeps the dark gradients from banding once Instagram re-encodes them.
/// </summary>
internal sealed class LyricsVideoRenderer : IDisposable
{
    public const int W = LyricsVideoSpec.Width;
    public const int H = LyricsVideoSpec.Height;

    /// <summary>Moments averaged into a frame whose lyrics glide (a 180° shutter at 30 fps).</summary>
    public const int MotionSamples = 4;
    /// <summary>Moments averaged into an intro or outro frame, where the whole page moves.</summary>
    public const int PageMotionSamples = 3;
    private const double Shutter = 0.5 / LyricsVideoSpec.Fps;

    private readonly LyricsVideoScene _scene;
    private readonly SKSurface _frame;
    private readonly SKSurface _page;
    private readonly SKSurface _lyrics;
    private readonly SKSurface _outerFlow;
    private readonly SKSurface _pageFlow;
    private readonly SKSurface _reflection;
    private readonly SKPaint _lyric;
    private readonly SKPaint _title;
    private readonly SKPaint _artist;
    private readonly Dictionary<SKTypeface, SKFont> _lyricFonts = new();
    private readonly SKSamplingOptions _smooth = new(SKFilterMode.Linear, SKMipmapMode.None);
    private readonly SKSamplingOptions _sharp = new(SKCubicResampler.Mitchell);
    private uint[]? _frameSum;
    private uint[]? _lyricsSum;

    public LyricsVideoRenderer(LyricsVideoScene scene)
    {
        _scene = scene;
        var g = scene.Geometry;
        _frame = Surface(W, H);
        _page = Surface((int)g.Page.Width, (int)g.Page.Height);
        _lyrics = Surface((int)g.Lyrics.Width + 2 * LyricsBleed, (int)g.Lyrics.Height);
        // The flowing backgrounds are pure blur: drawn at a quarter size and stretched, they look the same for a sixteenth of the work.
        _outerFlow = Surface(g.IsPanel ? W / 4 : 1, g.IsPanel ? H / 4 : 1);
        _pageFlow = Surface((int)g.Page.Width / 4, (int)g.Page.Height / 4);
        _reflection = Surface((int)g.Page.Width / 16, (int)g.Page.Height / 16);
        _lyric = scene.LyricPaint();
        _title = new SKPaint { IsAntialias = true, SubpixelText = true, Typeface = scene.Bold, TextSize = g.TitleSize, Color = SKColors.White };
        _artist = new SKPaint { IsAntialias = true, SubpixelText = true, Typeface = scene.SemiBold, TextSize = g.ArtistSize, Color = SKColors.White };
    }

    /// <summary>Room either side of the lyrics column for the active line's swell and glow.</summary>
    private const int LyricsBleed = 40;

    private static SKSurface Surface(int w, int h) =>
        SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));

    /// <summary>Bytes of one frame as handed to the encoder: BT.709 limited-range YUV 4:2:0, planar.</summary>
    public const int FrameBytes = W * H * 3 / 2;

    /// <summary>
    /// Frame <paramref name="index"/> into <paramref name="dest"/> (<see cref="FrameBytes"/>, YUV 4:2:0 — converted here, on
    /// the rendering cores, so the encoder only encodes). <paramref name="pulse"/> gives the beat pulse (0..1) at a track time.
    /// </summary>
    public void RenderFrame(int index, Func<double, double> pulse, Span<byte> dest)
    {
        RenderPixels(index, pulse);
        Bt709.ToYuv420(_frame.PeekPixels().GetPixelSpan(), W, H, dest);
    }

    /// <summary>Frame <paramref name="index"/> as BGRA pixels in this renderer's frame (tests, previews).</summary>
    public ReadOnlySpan<byte> RenderPixels(int index, Func<double, double> pulse)
    {
        var t = _scene.Spec.ClipStart + (index + 0.5) / LyricsVideoSpec.Fps;
        if (PageMoves(t))
        {
            // Intro / outro: the whole page moves, so the whole frame is averaged.
            _frameSum ??= new uint[W * H * 4];
            Array.Clear(_frameSum);
            for (var s = 0; s < PageMotionSamples; s++)
            {
                var ts = t + ((s + 0.5) / PageMotionSamples - 0.5) * Shutter;
                Draw(ts, pulse(ts), lyricsSamples: 1);
                Add(_frameSum, _frame.PeekPixels().GetPixelSpan());
            }
            Average(_frameSum, _frame.PeekPixels().GetPixelSpan(), PageMotionSamples);
        }
        else
        {
            Draw(t, pulse(t), _scene.Motion.IsMoving(t) ? MotionSamples : 1);
        }
        var pixels = _frame.PeekPixels().GetPixelSpan();
        GrainOut(pixels, pixels, index);
        return pixels;
    }

    /// <summary>One frame for the live preview: no motion blur, grain included.</summary>
    public SKImage Snapshot(double trackTime, double pulse, int frameIndex)
    {
        Draw(trackTime, pulse, lyricsSamples: 1);
        var pixels = _frame.PeekPixels().GetPixelSpan();
        GrainOut(pixels, pixels, frameIndex);
        return _frame.Snapshot();
    }

    /// <summary>The transparent lyrics before compositing, for measuring motion without background or grain.</summary>
    internal SKImage SnapshotLyrics(double trackTime)
    {
        DrawLyricsLayer(trackTime, 1, 0);
        return _lyrics.Snapshot();
    }

    private bool PageMoves(double t)
    {
        var spec = _scene.Spec;
        return (spec.Intro && t - spec.ClipStart < LyricsVideoSpec.IntroSeconds) || (spec.Outro && t > spec.LyricsEnd);
    }

    private static void Add(uint[] sum, ReadOnlySpan<byte> pixels)
    {
        for (var i = 0; i < pixels.Length; i++) sum[i] += pixels[i];
    }

    private static void Average(uint[] sum, Span<byte> pixels, int samples)
    {
        var half = (uint)samples / 2;
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)((sum[i] + half) / (uint)samples);
    }

    /// <summary>Copies the frame out with the scene's grain added (the alpha channel untouched): two saturating byte passes,
    /// 32 bytes at a time where the CPU has AVX2.</summary>
    private void GrainOut(ReadOnlySpan<byte> src, Span<byte> dest, int frameIndex)
    {
        var add = _scene.GrainAdd;
        var sub = _scene.GrainSub;
        var mask = add.Length - 1;
        var offset = (int)((uint)(frameIndex * 2654435761u) % (uint)add.Length) & ~31;
        var i = 0;
        if (Avx2.IsSupported)
        {
            for (; i + 32 <= src.Length; i += 32)
            {
                var j = (offset + i) & mask;
                var v = Vector256.Create(src.Slice(i, 32));
                v = Avx2.AddSaturate(v, Vector256.Create(add.AsSpan(j, 32)));
                v = Avx2.SubtractSaturate(v, Vector256.Create(sub.AsSpan(j, 32)));
                v.CopyTo(dest.Slice(i, 32));
            }
        }
        for (; i < src.Length; i++)
        {
            var j = (offset + i) & mask;
            dest[i] = (byte)Math.Clamp(src[i] + add[j] - sub[j], 0, 255);
        }
    }

    // ── One moment ────────────────────────────────────────────────────────────────────────────

    private void Draw(double t, double pulse, int lyricsSamples)
    {
        var spec = _scene.Spec;
        var g = _scene.Geometry;
        var canvas = _frame.Canvas;
        var v = t - spec.ClipStart;          // video time
        var u = t - spec.LyricsEnd;          // time into the outro

        // The Reel's own background: the flowing cover, deeper and darker than the page's, vignette baked in.
        if (g.IsPanel)
        {
            DrawBackground(_outerFlow, _scene.Wash, t, pulse, scrim: 0.6f, vignette: true);
            using var snap = _outerFlow.Snapshot();
            canvas.DrawImage(snap, new SKRect(0, 0, W, H), _smooth);
        }
        else canvas.Clear(SKColors.Black);

        // The page arrives (intro) and leaves (outro).
        double pageIn = 1, pageScale = 1, pageDy = 0, titleIn = 1, lyricsIn = 1, lyricsDy = 0;
        if (spec.Intro)
        {
            var p = LineMotion.Ease((v - 0.15) / 1.15);
            pageIn = LyricsVideoMotion.CubicInOut((v - 0.15) / 0.7);
            pageScale = 0.9 + 0.1 * p;
            pageDy = 80 * (1 - p);
            titleIn = LyricsVideoMotion.CubicInOut((v - 0.75) / 0.6);
            lyricsIn = LyricsVideoMotion.CubicInOut((v - 1.05) / 0.7);
            lyricsDy = 26 * (1 - LineMotion.Ease((v - 1.05) / 0.9));
        }
        var headerDy = 0.0;
        if (!g.IsPanel && spec.Intro)
        {
            pageScale = 1;
            pageDy = 0;
            headerDy = 32 * (1 - LineMotion.Ease((v - 0.15) / 1.0));
            titleIn = LyricsVideoMotion.CubicInOut((v - 0.35) / 0.55);
            lyricsIn = LyricsVideoMotion.CubicInOut((v - 0.7) / 0.8);
            lyricsDy = 28 * (1 - LineMotion.Ease((v - 0.7) / 1.0));
        }
        double coverMove = 0;
        if (spec.Outro && u > 0)
        {
            lyricsIn *= 1 - LyricsVideoMotion.CubicInOut(u / 0.55);
            coverMove = LineMotion.Ease((u - 0.25) / 1.2);
        }

        DrawPage(t, pulse, titleIn, lyricsIn, lyricsDy, coverMove, lyricsSamples, headerDy);
        if (g.IsPanel) DrawPanel(canvas, g, pageIn, pageScale, pageDy);
        else
        {
            using var page = _page.Snapshot();
            canvas.DrawImage(page, 0, 0);
        }

        // Fade from and to black: the Reel opens out of black and closes into it, so its loop reads as one.
        double black = spec.Intro
            ? 1 - LyricsVideoMotion.CubicInOut(v / 0.9)
            : 1 - LyricsVideoMotion.CubicInOut(v / 0.25);
        black = Math.Max(black, spec.Outro
            ? LyricsVideoMotion.CubicInOut((u - 1.75) / 0.85)
            : LyricsVideoMotion.CubicInOut((t - (spec.ClipEnd - 0.45)) / 0.45));
        if (black > 0.001)
        {
            using var paint = new SKPaint { Color = SKColors.Black.WithAlpha(Alpha(black)) };
            canvas.DrawRect(0, 0, W, H, paint);
        }
    }

    /// <summary>The lyrics page's flowing background into <paramref name="target"/>: the pre-blurred cover filling it (scaled
    /// 1.18 so its edges never show), two copies drifting and turning over it (<see cref="FlowingArtworkMotion"/>), the beat
    /// breathing it, then the page's dark scrim. Owner #243: no white beat glow here — over a whole Reel it read as the
    /// screen flashing brighter on every hit.</summary>
    private static void DrawFlow(SKSurface target, SKImage art, double t, double pulse, float scrim, bool vignette = false)
    {
        var canvas = target.Canvas;
        var bounds = canvas.DeviceClipBounds;
        float w = bounds.Width, h = bounds.Height;
        var frame = FlowingArtworkMotion.Evaluate(t, w, h, pulse);
        var fill = Math.Max(w, h);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        canvas.Clear(SKColors.Black);
        canvas.Save();
        canvas.Translate(w / 2, h / 2);
        canvas.Scale((float)frame.BackdropScale);
        DrawCentered(canvas, art, fill * 1.18f, 1f, sampling);
        foreach (var pose in new[] { frame.Layer1, frame.Layer2 })
        {
            canvas.Save();
            canvas.Translate((float)pose.X, (float)pose.Y);
            canvas.RotateDegrees((float)pose.AngleDeg);
            canvas.Scale((float)pose.Scale);
            DrawCentered(canvas, art, fill, (float)pose.Opacity, sampling);
            canvas.Restore();
        }
        canvas.Restore();

        using var dark = new SKPaint { Color = SKColors.Black.WithAlpha(Alpha(scrim)) };
        canvas.DrawRect(0, 0, w, h, dark);
        if (vignette)
        {
            using var shade = new SKPaint();
            shade.Shader = SKShader.CreateRadialGradient(new SKPoint(w / 2, h / 2), h * 0.72f,
                [SKColors.Transparent, SKColors.Black.WithAlpha(110)], [0.5f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, w, h, shade);
        }
    }

    private void DrawBackground(SKSurface target, SKImage art, double t, double pulse, float scrim, bool vignette = false)
    {
        if (_scene.Spec.Background == LyricsVideoBackground.Aurora)
            LyricsVideoAurora.Draw(target, _scene.AuroraPalette, t, pulse, scrim, vignette);
        else DrawFlow(target, art, t, pulse, scrim, vignette);
    }

    private static void DrawCentered(SKCanvas canvas, SKImage art, float size, float opacity, SKSamplingOptions sampling)
    {
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
        canvas.DrawImage(art, new SKRect(-size / 2, -size / 2, size / 2, size / 2), sampling, paint);
    }

    // ── The page ──────────────────────────────────────────────────────────────────────────────

    private void DrawPage(double t, double pulse, double titleIn, double lyricsIn, double lyricsDy, double coverMove, int lyricsSamples, double headerDy)
    {
        var g = _scene.Geometry;
        var canvas = _page.Canvas;
        DrawBackground(_pageFlow, _scene.Backdrop, t, pulse, scrim: 0.5f);
        using (var snap = _pageFlow.Snapshot())
            canvas.DrawImage(snap, new SKRect(0, 0, g.Page.Width, g.Page.Height), _smooth);

        // Outro: the cover glides to the middle of the page and grows a little, the title following under it.
        var cover = g.Cover;
        cover.Offset(0, (float)headerDy);
        if (coverMove > 0)
        {
            var size = g.IsPanel ? cover.Width * (1 + 0.18f * (float)coverMove) : Lerp(cover.Width, 560, coverMove);
            var cx = Lerp(cover.MidX, g.Page.Width / 2, coverMove);
            var cy = Lerp(cover.MidY, g.IsPanel ? g.Page.Height / 2 - 40 : 840, coverMove);
            cover = SKRect.Create(cx - size / 2, cy - size / 2, size, size);
        }
        DrawCover(canvas, cover);
        if (g.IsPanel) DrawTitle(canvas, cover.MidX, cover.Bottom + (g.Title.Y - g.Cover.Bottom), titleIn);
        else
        {
            var outro = _scene.Spec.Outro ? Math.Max(0, t - _scene.Spec.LyricsEnd) : 0;
            var headerAlpha = 1 - LyricsVideoMotion.CubicInOut(outro / 0.35);
            DrawTitle(canvas, g.Title.X, g.Title.Y + (float)headerDy, titleIn * headerAlpha,
                g.TitleCentered ? 1 : 0, g.TitleWidth);
            // The compact title lives beside the cover; do not drag it through the growing artwork.
            // Fade it out in place, then bring the outro title in under the centred cover.
            if (outro > 0)
                DrawTitle(canvas, 540, 1180, titleIn * LyricsVideoMotion.CubicInOut((outro - 0.95) / 0.6), 1, 840);
        }

        if (lyricsIn <= 0.001) return;
        var vp = g.Lyrics;
        var layerDest = SKRect.Create(vp.Left - LyricsBleed, vp.Top, vp.Width + 2 * LyricsBleed, vp.Height);
        if (lyricsSamples <= 1)
        {
            DrawLyricsLayer(t, (float)lyricsIn, (float)lyricsDy);
        }
        else
        {
            // The glide's motion blur, over the lyrics only.
            var pixels = _lyrics.PeekPixels().GetPixelSpan();
            _lyricsSum ??= new uint[pixels.Length];
            Array.Clear(_lyricsSum);
            for (var s = 0; s < lyricsSamples; s++)
            {
                var ts = t + ((s + 0.5) / lyricsSamples - 0.5) * Shutter;
                DrawLyricsLayer(ts, (float)lyricsIn, (float)lyricsDy);
                Add(_lyricsSum, _lyrics.PeekPixels().GetPixelSpan());
            }
            Average(_lyricsSum, _lyrics.PeekPixels().GetPixelSpan(), lyricsSamples);
        }
        using var layer = _lyrics.Snapshot();
        canvas.DrawImage(layer, layerDest.Left, layerDest.Top);
    }

    private void DrawCover(SKCanvas canvas, SKRect rect)
    {
        var g = _scene.Geometry;
        var k = rect.Width / g.Cover.Width;
        var radius = g.CoverRadius * k;
        // The pre-drawn soft shadow, scaled with the cover.
        var shadow = _scene.CoverShadow;
        var pad = _scene.CoverShadowPad * k;
        canvas.DrawImage(shadow, SKRect.Create(rect.Left - pad, rect.Top - pad + rect.Width * 0.035f, rect.Width + 2 * pad, rect.Height + 2 * pad), _smooth);

        canvas.Save();
        using var clip = new SKRoundRect(rect, radius);
        canvas.ClipRoundRect(clip, antialias: true);
        if (_scene.Cover is { } art)
        {
            var s = Math.Max(rect.Width / art.Width, rect.Height / art.Height);
            var dw = art.Width * s;
            var dh = art.Height * s;
            canvas.DrawImage(art, SKRect.Create(rect.MidX - dw / 2, rect.MidY - dh / 2, dw, dh), _sharp);
        }
        else
        {
            using var placeholder = new SKPaint { Color = new SKColor(255, 255, 255, 30) };
            canvas.DrawRect(rect, placeholder);
        }
        canvas.Restore();
    }

    private void DrawTitle(SKCanvas canvas, float centerX, float baseline, double opacity, double alignment = 1, float? maxWidth = null)
    {
        if (opacity <= 0.001) return;
        var g = _scene.Geometry;
        var spec = _scene.Spec;
        var maxW = maxWidth ?? g.Cover.Width + 100;
        var badge = spec.IsExplicit ? g.TitleSize * 0.62f : 0;
        var title = Ellipsize(spec.Title, maxW - (badge > 0 ? badge + 10 : 0), _title);
        var artist = Ellipsize(spec.Artist, maxW, _artist);
        var titleW = ShareCardRenderer.MeasureTextFallback(_title, title);
        var x = centerX - (float)(alignment * (titleW + (badge > 0 ? badge + 10 : 0)) / 2);

        _title.Color = SKColors.White.WithAlpha(Alpha(opacity));
        ShareCardRenderer.DrawTextFallback(canvas, title, x, baseline, _title);
        if (badge > 0)
        {
            var bx = x + titleW + 10;
            var by = baseline - badge - g.TitleSize * 0.06f;
            using var box = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(Alpha(0.6 * opacity)) };
            canvas.DrawRoundRect(SKRect.Create(bx, by, badge, badge), 4, 4, box);
            using var e = new SKPaint { IsAntialias = true, Typeface = _scene.Bold, TextSize = badge * 0.72f, Color = SKColors.Black.WithAlpha(Alpha(0.85 * opacity)), TextAlign = SKTextAlign.Center };
            canvas.DrawText("E", bx + badge / 2, by + badge * 0.76f, e);
        }

        _artist.Color = SKColors.White.WithAlpha(Alpha(0.6 * opacity));
        var artistW = ShareCardRenderer.MeasureTextFallback(_artist, artist);
        ShareCardRenderer.DrawTextFallback(canvas, artist, centerX - (float)(alignment * artistW / 2), baseline + g.ArtistSize * 1.45f, _artist);
    }

    private static string Ellipsize(string text, float maxW, SKPaint paint)
    {
        text = (text ?? "").Trim();
        if (ShareCardRenderer.MeasureTextFallback(paint, text) <= maxW) return text;
        var end = text.Length;
        while (end > 1 && ShareCardRenderer.MeasureTextFallback(paint, text[..end].TrimEnd() + "…") > maxW) end--;
        return text[..end].TrimEnd() + "…";
    }

    /// <summary>The lyrics list into <see cref="_lyrics"/> (its own small layer, so motion blur only repeats this part).</summary>
    private void DrawLyricsLayer(double t, float alpha, float dy)
    {
        var g = _scene.Geometry;
        var motion = _scene.Motion;
        var canvas = _lyrics.Canvas;
        canvas.Clear(SKColors.Transparent);
        var vpH = g.Lyrics.Height;
        float left = LyricsBleed;
        var anchor = vpH * g.AnchorRatio;
        var scroll = (float)motion.ScrollAt(t);
        var active = motion.ActiveAt(t);
        var k = g.LyricSize / LyricsVideoScene.PageLyricSize;

        foreach (var line in _scene.Lines)
        {
            var y = anchor + line.Top - scroll + (float)motion.CascadeLag(line.Index, t) + dy;
            if (y + line.Height < -40 || y > vpH + 40) continue;
            var opacity = motion.LineOpacity(line.Index, t) * alpha;
            if (opacity <= 0.003) continue;
            var scale = (float)motion.LineScale(line.Index, t);

            canvas.Save();
            // The page grows the active line from its left edge, about its middle.
            canvas.Scale(scale, scale, left, y + line.Height / 2);
            if (line.Index == active && line.Line.Words is not null)
                DrawSungLine(canvas, line, left, y, t, (float)opacity, (float)motion.LineBase(line.Index, t));
            else
                DrawRestingLine(canvas, line, left, y, (float)opacity, motion.LineBlur(line.Index, t) * k);
            canvas.Restore();
        }

        // Soft edges where the list leaves the page.
        if (g.EdgeFade > 0)
        {
            var w = canvas.DeviceClipBounds.Width;
            using var fade = new SKPaint { BlendMode = SKBlendMode.DstIn };
            fade.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, g.EdgeFade),
                [SKColors.Transparent, SKColors.Black], SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, 0, w, g.EdgeFade), fade);
            fade.Shader = SKShader.CreateLinearGradient(new SKPoint(0, vpH - g.EdgeFade), new SKPoint(0, vpH),
                [SKColors.Black, SKColors.Transparent], SKShaderTileMode.Clamp);
            canvas.DrawRect(SKRect.Create(0, vpH - g.EdgeFade, w, g.EdgeFade), fade);
        }
    }

    /// <summary>A line at rest: its pre-drawn look at the current depth blur (two neighbouring blur steps cross-faded).</summary>
    private void DrawRestingLine(SKCanvas canvas, LaidLine line, float x, float y, float opacity, double blur)
    {
        var looks = line.Looks;
        if (looks[0] is null)
        {
            // Outside the pre-drawn range (never on screen in practice): plain text.
            _lyric.Color = SKColors.White.WithAlpha(Alpha(opacity));
            foreach (var (text, rowY) in line.Rows)
                ShareCardRenderer.DrawTextFallback(canvas, text, x, y + rowY - _scene.LyricMetrics.Ascent, _lyric);
            _lyric.Color = SKColors.White;
            return;
        }
        var k = _scene.Geometry.LyricSize / LyricsVideoScene.PageLyricSize;
        var levels = LyricsVideoScene.BlurLevels;
        var hi = 1;
        while (hi < levels.Length - 1 && levels[hi] * k < blur) hi++;
        var lo = hi - 1;
        var span = (levels[hi] - levels[lo]) * k;
        var f = span <= 0 ? 0 : Math.Clamp((blur - levels[lo] * k) / span, 0, 1);
        DrawLook(canvas, looks[lo]!, line.LookPad, x, y, opacity * (float)(1 - f));
        if (f > 0.002) DrawLook(canvas, looks[hi]!, line.LookPad, x, y, opacity * (float)f);
    }

    private void DrawLook(SKCanvas canvas, SKImage look, float pad, float x, float y, float opacity)
    {
        if (opacity <= 0.002) return;
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) };
        canvas.DrawImage(look, x - pad, y - pad, _smooth, paint);
    }

    /// <summary>
    /// The line being sung, word by word as the page draws it (KaraokeWordText): each word lit behind the sweep and
    /// unsung ahead of it, the soft edge a fixed 0.3 em either side of the front; every word floating up as it is sung
    /// (<see cref="WordFloat"/>); held notes swelling letter by letter with their glow (<see cref="EmphasisMotion"/>,
    /// <see cref="EmphasisGlow"/>).
    /// </summary>
    private void DrawSungLine(SKCanvas canvas, LaidLine line, float x, float y, double t, float opacity, float baseLevel)
    {
        var tt = t + LyricsVideoMotion.WordLookahead;
        var size = _scene.Geometry.LyricSize;
        var lit = SKColors.White.WithAlpha(Alpha(opacity));
        var dim = SKColors.White.WithAlpha(Alpha(opacity * baseLevel));
        foreach (var word in line.Words)
        {
            var w = word.Timing;
            var progress = KaraokeSweep.BandProgress(w.Start, w.End, tt);
            var feather = KaraokeBand.Feather(word.GlyphWidth, size);
            var center = KaraokeBand.Center(progress, feather, w.LeadIn, w.TrailOut);
            var lo = (float)((center - feather) * word.GlyphWidth);
            var hi = (float)((center + feather) * word.GlyphWidth);
            var sinceMs = (tt - w.Start) * 1000;
            var rise = WordFloat.Rise(sinceMs, (w.End - w.Start) * 1000);
            var wx = x + word.X;
            var wy = y + word.Baseline - (float)(WordFloat.LiftEm * size * rise);

            if (word.Letters is { Count: > 0 } letters && w.IsEmphasis)
            {
                var p = EmphasisMotion.For(w.HeldMs, w.IsLastInLine);
                var emphasisMs = EmphasisMotion.IsInFlight(sinceMs, p, w.EmphasisCharCount) ? sinceMs : double.NaN;
                var glow = double.IsNaN(emphasisMs) ? 0 : EmphasisGlow.Opacity(emphasisMs, w.HeldMs, w.IsLastInLine, w.EmphasisCharCount);
                if (glow > 0.003) DrawGlow(canvas, letters, wx, wy, hi, emphasisMs, p, glow * opacity);
                for (var i = 0; i < letters.Count; i++)
                {
                    var m = LetterMatrix(letters, i, emphasisMs, p, size);
                    canvas.Save();
                    canvas.Translate(wx, wy);
                    canvas.Concat(in m);
                    // The edge stays in word space while the letter moves inside it (AMLL masks the word).
                    m.TryInvert(out var inverse);
                    DrawSwept(canvas, letters[i].Text, letters[i].X, lo, hi, lit, dim, inverse);
                    canvas.Restore();
                }
            }
            else
            {
                canvas.Save();
                canvas.Translate(wx, wy);
                DrawSwept(canvas, word.Text, 0, lo, hi, lit, dim, SKMatrix.Identity);
                canvas.Restore();
            }
        }
    }

    private void DrawSwept(SKCanvas canvas, string text, float x, float lo, float hi, SKColor lit, SKColor dim, SKMatrix local)
    {
        if (hi - lo < 0.5f)
        {
            _lyric.Color = hi <= x ? dim : lit;
            DrawLyricText(canvas, text, x, 0, _lyric);
        }
        else
        {
            _lyric.Color = SKColors.White;
            using var shader = SKShader.CreateLinearGradient(new SKPoint(lo, 0), new SKPoint(hi, 0), [lit, dim], null,
                SKShaderTileMode.Clamp, local);
            _lyric.Shader = shader;
            DrawLyricText(canvas, text, x, 0, _lyric);
            _lyric.Shader = null;
        }
        _lyric.Color = SKColors.White;
    }

    private void DrawLyricText(SKCanvas canvas, string text, float x, float y, SKPaint paint)
    {
        foreach (var (run, face) in ShareCardRenderer.SplitFallbackRuns(text, paint.Typeface!))
        {
            if (!_lyricFonts.TryGetValue(face, out var font))
            {
                font = paint.ToFont();
                font.Typeface = face;
                // The native page moves rasterized letters continuously. Snapping a freshly drawn
                // Reel glyph's baseline to an integer pixel turned its gentle rise into 1px stairs.
                font.BaselineSnap = false;
                font.Subpixel = true;
                _lyricFonts.Add(face, font);
            }
            canvas.DrawText(run, x, y, font, paint);
            x += font.MeasureText(run);
        }
    }

    /// <summary>A held-note letter's pose about its centre, in word space (KaraokeWordText.LetterTransform).</summary>
    private SKMatrix LetterMatrix(List<(string Text, float X, float Width)> letters, int i, double emphasisMs, EmphasisParams p, float size)
    {
        if (double.IsNaN(emphasisMs)) return SKMatrix.Identity;
        var pose = EmphasisMotion.CharPose(emphasisMs, i, letters.Count, p);
        var cx = letters[i].X + letters[i].Width / 2;
        var cy = _scene.LyricMetrics.Ascent / 2; // the letter's middle, above the baseline
        var s = (float)pose.Scale;
        var dx = (float)(pose.OffsetXEm * size);
        var dy = (float)(pose.OffsetYEm * size);
        return SKMatrix.CreateTranslation(-cx, -cy)
            .PostConcat(SKMatrix.CreateScale(s, s))
            .PostConcat(SKMatrix.CreateTranslation(cx + dx, cy + dy));
    }

    /// <summary>The held-note halo: the sung letters, blurred 0.45 em, in white under the word.</summary>
    private void DrawGlow(SKCanvas canvas, List<(string Text, float X, float Width)> letters,
        float wx, float wy, float litTo, double emphasisMs, EmphasisParams p, double opacity)
    {
        var size = _scene.Geometry.LyricSize;
        using var paint = _lyric.Clone();
        paint.Color = SKColors.White.WithAlpha(Alpha(opacity));
        paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)EmphasisGlowBlurConverter.Radius(size) * 0.5f);
        canvas.Save();
        canvas.Translate(wx, wy);
        canvas.ClipRect(new SKRect(-size, -size * 2, Math.Max(0, litTo), size));
        for (var i = 0; i < letters.Count; i++)
        {
            var m = LetterMatrix(letters, i, emphasisMs, p, size);
            canvas.Save();
            canvas.Concat(in m);
            DrawLyricText(canvas, letters[i].Text, letters[i].X, 0, paint);
            canvas.Restore();
        }
        canvas.Restore();
    }

    // ── The panel in the Reel ─────────────────────────────────────────────────────────────────

    /// <summary>The page as a floating panel: its pre-drawn soft shadow, a frosted glow of itself below it, a hairline edge.</summary>
    private void DrawPanel(SKCanvas canvas, LyricsVideoGeometry g, double opacity, double scale, double dy)
    {
        if (opacity <= 0.002) return;
        var rect = g.Page;
        using var page = _page.Snapshot();

        canvas.Save();
        canvas.Translate(0, (float)dy);
        canvas.Scale((float)scale, (float)scale, rect.MidX, rect.MidY);

        var shadow = _scene.PanelShadow;
        var pad = _scene.PanelShadowPad;
        using (var paint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) })
            canvas.DrawImage(shadow, SKRect.Create(rect.Left - pad, rect.Top - pad + 30, rect.Width + 2 * pad, rect.Height + 2 * pad), _smooth, paint);

        // Below the panel, a frosted reflection: the page shrunk to a sixteenth (so it is pure colour, no readable text),
        // mirrored with the panel's own rounded corners, fading out. Its strength is the user's (0 = none).
        var reflection = _scene.Spec.Reflection;
        var rc = _reflection.Canvas;
        rc.Clear(SKColors.Transparent);
        rc.DrawImage(page, SKRect.Create(0, 0, rect.Width / 16, rect.Height / 16), _smooth);
        if (reflection > 0.001)
        using (var small = _reflection.Snapshot())
        {
            var reflectH = rect.Height * 0.32f;
            var top = rect.Bottom + 22;
            canvas.SaveLayer(SKRect.Create(rect.Left, top, rect.Width, reflectH), null);
            canvas.Save();
            canvas.Translate(0, top * 2);
            canvas.Scale(1, -1);
            var mirrored = SKRect.Create(rect.Left, top - rect.Height, rect.Width, rect.Height);
            using (var round = new SKRoundRect(mirrored, g.PageRadius))
                canvas.ClipRoundRect(round, antialias: true);
            using (var mirror = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(reflection * opacity)) })
                canvas.DrawImage(small, SKRect.Create(0, 0, rect.Width / 16, rect.Height / 16), mirrored, _smooth, mirror);
            canvas.Restore();
            using (var fade = new SKPaint { BlendMode = SKBlendMode.DstIn })
            {
                fade.Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + reflectH),
                    [SKColors.Black, SKColors.Transparent], SKShaderTileMode.Clamp);
                canvas.DrawRect(SKRect.Create(rect.Left, top, rect.Width, reflectH), fade);
            }
            canvas.Restore();
        }

        canvas.Save();
        using (var clip = new SKRoundRect(rect, g.PageRadius))
            canvas.ClipRoundRect(clip, antialias: true);
        using (var paint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(opacity)) })
            canvas.DrawImage(page, rect.Left, rect.Top, paint);
        canvas.Restore();
        // Glass edge: a hairline that catches the light along the top and fades down the sides.
        using (var edge = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = 1.5f })
        {
            edge.Shader = SKShader.CreateLinearGradient(new SKPoint(0, rect.Top), new SKPoint(0, rect.Bottom),
                [SKColors.White.WithAlpha(Alpha(0.32 * opacity)), SKColors.White.WithAlpha(Alpha(0.07 * opacity)), SKColors.White.WithAlpha(Alpha(0.1 * opacity))],
                [0f, 0.45f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(SKRect.Inflate(rect, -0.75f, -0.75f), g.PageRadius, g.PageRadius, edge);
        }
        canvas.Restore();
    }

    private static byte Alpha(double a) => (byte)Math.Round(255 * Math.Clamp(a, 0, 1));

    private static float Lerp(float a, float b, double f) => a + (b - a) * (float)f;

    public void Dispose()
    {
        _frame.Dispose();
        _page.Dispose();
        _lyrics.Dispose();
        _outerFlow.Dispose();
        _pageFlow.Dispose();
        _reflection.Dispose();
        _lyric.Dispose();
        _title.Dispose();
        _artist.Dispose();
        foreach (var font in _lyricFonts.Values) font.Dispose();
    }
}

/// <summary>BGRA → BT.709 limited-range YUV 4:2:0 (planar I420), the format the Reel is encoded in. Runs on every
/// rendering core, so it walks the pixels by reference (no per-pixel bounds checks): a few milliseconds a frame.</summary>
internal static class Bt709
{
    public static void ToYuv420(ReadOnlySpan<byte> bgra, int w, int h, Span<byte> dest)
    {
        if (bgra.Length < w * h * 4 || dest.Length < w * h * 3 / 2) throw new ArgumentException("frame size");
        ref var src = ref MemoryMarshal.GetReference(bgra);
        ref var yPlane = ref MemoryMarshal.GetReference(dest);
        ref var uPlane = ref Unsafe.Add(ref yPlane, w * h);
        ref var vPlane = ref Unsafe.Add(ref uPlane, w * h / 4);
        var cw = w / 2;
        for (var y = 0; y < h; y += 2)
        {
            ref var r0 = ref Unsafe.Add(ref src, y * w * 4);
            ref var r1 = ref Unsafe.Add(ref r0, w * 4);
            ref var y0 = ref Unsafe.Add(ref yPlane, y * w);
            ref var y1 = ref Unsafe.Add(ref y0, w);
            ref var u = ref Unsafe.Add(ref uPlane, y / 2 * cw);
            ref var v = ref Unsafe.Add(ref vPlane, y / 2 * cw);
            for (var x = 0; x < w; x += 2)
            {
                int b0 = Unsafe.Add(ref r0, x * 4), g0 = Unsafe.Add(ref r0, x * 4 + 1), q0 = Unsafe.Add(ref r0, x * 4 + 2);
                int b1 = Unsafe.Add(ref r0, x * 4 + 4), g1 = Unsafe.Add(ref r0, x * 4 + 5), q1 = Unsafe.Add(ref r0, x * 4 + 6);
                int b2 = Unsafe.Add(ref r1, x * 4), g2 = Unsafe.Add(ref r1, x * 4 + 1), q2 = Unsafe.Add(ref r1, x * 4 + 2);
                int b3 = Unsafe.Add(ref r1, x * 4 + 4), g3 = Unsafe.Add(ref r1, x * 4 + 5), q3 = Unsafe.Add(ref r1, x * 4 + 6);
                Unsafe.Add(ref y0, x) = Y(q0, g0, b0);
                Unsafe.Add(ref y0, x + 1) = Y(q1, g1, b1);
                Unsafe.Add(ref y1, x) = Y(q2, g2, b2);
                Unsafe.Add(ref y1, x + 1) = Y(q3, g3, b3);
                int sr = q0 + q1 + q2 + q3, sg = g0 + g1 + g2 + g3, sb = b0 + b1 + b2 + b3;
                // 2×2 average (the sums are 4×), coefficients ×256 (×1024 with the sum).
                Unsafe.Add(ref u, x / 2) = (byte)Math.Clamp(((-26 * sr - 87 * sg + 112 * sb + 512) >> 10) + 128, 16, 240);
                Unsafe.Add(ref v, x / 2) = (byte)Math.Clamp(((112 * sr - 102 * sg - 10 * sb + 512) >> 10) + 128, 16, 240);
            }
        }
    }

    private static byte Y(int r, int g, int b) => (byte)(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);
}
