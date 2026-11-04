using SkiaSharp;

namespace Noctis.Services.LyricsVideo;

/// <summary>Cover colours extracted once in OKLab; the slow field is a pure function of track time.</summary>
internal static class LyricsVideoAurora
{
    internal readonly record struct Lab(double L, double A, double B)
    {
        public double Distance(Lab other) => (L - other.L) * (L - other.L)
            + (A - other.A) * (A - other.A) + (B - other.B) * (B - other.B);
        public static Lab Mix(Lab a, Lab b, double t) => new(a.L + (b.L - a.L) * t,
            a.A + (b.A - a.A) * t, a.B + (b.B - a.B) * t);
        public SKColor Color()
        {
            var c = DominantColorExtractor.FromOkLabInGamut(L, A, B);
            return new SKColor(c.R, c.G, c.B);
        }
    }

    public static IReadOnlyList<Lab> Palette(SKImage? cover)
    {
        Lab[] fallback = [new(0.28, 0.035, -0.06), new(0.48, 0.08, -0.1), new(0.44, 0.1, 0.015), new(0.38, -0.025, -0.07)];
        if (cover is null) return fallback;
        using var surface = SKSurface.Create(new SKImageInfo(32, 32));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(cover, new SKRect(0, 0, 32, 32));
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        var points = new List<Lab>(1024);
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Alpha < 32) continue;
                var (l, a, b) = DominantColorExtractor.ToOkLab(c.Red, c.Green, c.Blue);
                points.Add(new(l, a, b));
            }
        if (points.Count == 0) return fallback;

        // Farthest-point seeds keep the palette deterministic, including flat and monochrome artwork.
        var centres = new Lab[4];
        centres[0] = points[0];
        for (var k = 1; k < centres.Length; k++)
            centres[k] = points.MaxBy(p => centres.Take(k).Min(p.Distance));
        for (var pass = 0; pass < 8; pass++)
        {
            var sums = new Lab[4];
            var counts = new int[4];
            foreach (var point in points)
            {
                var nearest = 0;
                for (var k = 1; k < 4; k++)
                    if (point.Distance(centres[k]) < point.Distance(centres[nearest])) nearest = k;
                var sum = sums[nearest];
                sums[nearest] = new(sum.L + point.L, sum.A + point.A, sum.B + point.B);
                counts[nearest]++;
            }
            for (var k = 0; k < 4; k++)
                if (counts[k] > 0) centres[k] = new(sums[k].L / counts[k], sums[k].A / counts[k], sums[k].B / counts[k]);
        }
        return centres.OrderBy(c => c.L).ToArray();
    }

    public static void Draw(SKSurface target, IReadOnlyList<Lab> palette, double time, double pulse, float scrim, bool vignette)
    {
        var canvas = target.Canvas;
        var bounds = canvas.DeviceClipBounds;
        float w = bounds.Width, h = bounds.Height;
        using var paint = new SKPaint { IsAntialias = true, IsDither = true };
        using (var baseShader = SKShader.CreateLinearGradient(new(0, 0), new(w, h),
            [palette[0].Color(), palette[2].Color()], SKShaderTileMode.Clamp))
        {
            paint.Shader = baseShader;
            canvas.DrawRect(0, 0, w, h, paint);
        }
        for (var i = 0; i < 4; i++)
        {
            var phase = time * 0.045 + i * Math.PI / 2;
            var x = w * (0.5 + 0.3 * Math.Sin(phase));
            var y = h * (0.5 + 0.35 * Math.Cos(phase * 0.73 + i));
            var radius = (float)(Math.Max(w, h) * 0.72 * (1 + 0.025 * Math.Clamp(pulse, 0, 1)));
            var mixed = Lab.Mix(palette[i], palette[(i + 1) % 4], 0.18 + 0.12 * Math.Sin(time * 0.025 + i));
            var color = mixed.Color();
            // A white label or border on the cover should not wash out its coloured field.
            var chroma = Math.Sqrt(mixed.A * mixed.A + mixed.B * mixed.B);
            var opacity = (byte)Math.Round(110 + 100 * Math.Clamp(chroma / 0.16, 0, 1));
            using var shader = SKShader.CreateRadialGradient(new((float)x, (float)y), radius,
                [color.WithAlpha(opacity), color.WithAlpha((byte)(opacity / 2)), color.WithAlpha(0)], [0f, 0.55f, 1f], SKShaderTileMode.Clamp);
            paint.Shader = shader;
            canvas.DrawRect(0, 0, w, h, paint);
        }
        paint.Shader = null;
        paint.Color = SKColors.Black.WithAlpha((byte)Math.Round(255 * scrim));
        canvas.DrawRect(0, 0, w, h, paint);
        if (vignette)
        {
            using var shader = SKShader.CreateRadialGradient(new(w / 2, h / 2), h * 0.72f,
                [SKColors.Transparent, SKColors.Black.WithAlpha(110)], [0.5f, 1f], SKShaderTileMode.Clamp);
            paint.Color = SKColors.White;
            paint.Shader = shader;
            canvas.DrawRect(0, 0, w, h, paint);
        }
    }
}
