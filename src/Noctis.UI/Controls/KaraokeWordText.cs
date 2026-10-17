using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// One karaoke word: the dim (unsung) and bright (sung) text in a single draw, split by a
/// soft edge that follows <see cref="Progress"/>, plus the Apple Music held-note letter
/// animation (<see cref="EmphasisMotion"/>) for <see cref="IsEmphasis"/> words.
///
/// It replaces a dimmed base TextBlock with a second TextBlock on top whose Foreground was
/// a new gradient brush every frame: a TextBlock rebuilds its text layout and invalidates
/// its measure when Foreground changes, so every frame of a sweep ran a layout pass up the
/// lyrics list (measured: 30 of 30 frames). Here the brushes are created once per layout
/// and only their stops move; a Progress change is a render-only invalidation.
///
/// Measure, arrange, fonts and styles are the TextBlock's own (it IS one, styled as one),
/// so the cell keeps exactly the geometry the template always had.
/// </summary>
public class KaraokeWordText : TextBlock
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<KaraokeWordText, double>(nameof(Progress), KaraokeSweep.InertFuture);

    /// <summary>Opacity of the unsung text (1 on inactive lines, a soft ghost while active).</summary>
    public static readonly StyledProperty<double> BaseOpacityProperty =
        AvaloniaProperty.Register<KaraokeWordText, double>(nameof(BaseOpacity), 1.0);

    public static readonly StyledProperty<bool> LeadInProperty =
        AvaloniaProperty.Register<KaraokeWordText, bool>(nameof(LeadIn));

    public static readonly StyledProperty<bool> TrailOutProperty =
        AvaloniaProperty.Register<KaraokeWordText, bool>(nameof(TrailOut));

    public static readonly StyledProperty<bool> IsEmphasisProperty =
        AvaloniaProperty.Register<KaraokeWordText, bool>(nameof(IsEmphasis));

    public static readonly StyledProperty<double> EmphasisMsProperty =
        AvaloniaProperty.Register<KaraokeWordText, double>(nameof(EmphasisMs), double.NaN);

    public static readonly StyledProperty<double> HeldDurationMsProperty =
        AvaloniaProperty.Register<KaraokeWordText, double>(nameof(HeldDurationMs));

    public static readonly StyledProperty<bool> IsLastInLineProperty =
        AvaloniaProperty.Register<KaraokeWordText, bool>(nameof(IsLastInLine));

    /// <summary>Front position through the word (driven by <see cref="LyricsTimeline"/>): 0..1
    /// inside it, beyond either end on a neighbour of the current word, sentinels far away.</summary>
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double BaseOpacity { get => GetValue(BaseOpacityProperty); set => SetValue(BaseOpacityProperty, value); }
    /// <summary>A rest (or the line start) precedes the word: the edge enters from dark.</summary>
    public bool LeadIn { get => GetValue(LeadInProperty); set => SetValue(LeadInProperty, value); }
    /// <summary>A rest (or the line end) follows the word: the edge has crossed it by its end.</summary>
    public bool TrailOut { get => GetValue(TrailOutProperty); set => SetValue(TrailOutProperty, value); }
    public bool IsEmphasis { get => GetValue(IsEmphasisProperty); set => SetValue(IsEmphasisProperty, value); }
    /// <summary>Time since the word started while its held-note animation runs; NaN at rest.</summary>
    public double EmphasisMs { get => GetValue(EmphasisMsProperty); set => SetValue(EmphasisMsProperty, value); }
    public double HeldDurationMs { get => GetValue(HeldDurationMsProperty); set => SetValue(HeldDurationMsProperty, value); }
    public bool IsLastInLine { get => GetValue(IsLastInLineProperty); set => SetValue(IsLastInLineProperty, value); }

    // Styled exactly like the TextBlocks it replaces (app-wide TextBlock font rules apply).
    protected override Type StyleKeyOverride => typeof(TextBlock);

    static KaraokeWordText()
    {
        AffectsRender<KaraokeWordText>(ProgressProperty, BaseOpacityProperty, LeadInProperty, TrailOutProperty,
            EmphasisMsProperty, HeldDurationMsProperty, IsLastInLineProperty);
    }

    /// <summary>One drawn piece: the whole word, or one letter of a held-note word.</summary>
    private sealed class Piece
    {
        public required TextLayout Layout;
        public required LinearGradientBrush Brush;
        public double X;       // left edge inside the word's glyph run
        public double Width;   // advance width
    }

    private readonly List<Piece> _pieces = new();
    private double _glyphWidth;   // width the edge travels: the visible word, trailing space excluded
    private double _layoutHeight;
    private bool _perLetter;
    private bool _dirty = true;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        if (p == TextProperty || p == FontFamilyProperty || p == FontSizeProperty || p == FontWeightProperty
            || p == FontStyleProperty || p == FontStretchProperty || p == IsEmphasisProperty)
        {
            _dirty = true;
            InvalidateVisual();
        }
        else if (p == ProgressProperty || p == BaseOpacityProperty || p == LeadInProperty || p == TrailOutProperty
                 || p == EmphasisMsProperty || p == HeldDurationMsProperty || p == IsLastInLineProperty
                 || p == ForegroundProperty)
        {
            // Brush stops move here, not in Render: changing a brush while the frame is
            // being recorded would re-dirty the visual it is drawn into.
            if (!_dirty) UpdateStops();
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var piece in _pieces) piece.Layout.Dispose();
        _pieces.Clear();
        _dirty = true;
    }

    private void EnsurePieces()
    {
        if (!_dirty) return;
        _dirty = false;
        foreach (var piece in _pieces) piece.Layout.Dispose();
        _pieces.Clear();

        var sweep = (Text ?? string.Empty).TrimEnd();
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        using var word = new TextLayout(sweep, typeface, FontSize, Brushes.White);
        _glyphWidth = word.WidthIncludingTrailingWhitespace;
        _layoutHeight = word.Height;
        if (sweep.Length == 0) return;

        _perLetter = IsEmphasis;
        if (!_perLetter)
        {
            _pieces.Add(MakePiece(sweep, typeface, 0, _glyphWidth));
            return;
        }

        // Held-note words draw letter by letter, each at the position the whole-word layout
        // gives it (kerning kept), so a letter can swell and lift on its own. Drawn this
        // way at rest too: switching layouts when the animation starts would shift glyphs.
        var lead = sweep.Length - sweep.TrimStart().Length;
        var starts = StringInfo.ParseCombiningCharacters(sweep[lead..]);
        for (int i = 0; i < starts.Length; i++)
        {
            var from = lead + starts[i];
            var to = i + 1 < starts.Length ? lead + starts[i + 1] : sweep.Length;
            var x0 = word.HitTestTextPosition(from).X;
            var x1 = to < sweep.Length ? word.HitTestTextPosition(to).X : _glyphWidth;
            _pieces.Add(MakePiece(sweep[from..to], typeface, x0, x1 - x0));
        }
    }

    private Piece MakePiece(string text, Typeface typeface, double x, double width)
    {
        // Absolute endpoints across the piece's own advance: stop offsets are then plain
        // fractions of it, and only those move per frame.
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(Math.Max(1, width), 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Colors.White, 1) },
        };
        return new Piece
        {
            Layout = new TextLayout(text, typeface, FontSize, brush),
            Brush = brush,
            X = x,
            Width = Math.Max(1, width),
        };
    }

    /// <summary>Where the soft edge is, in word-glyph pixels.</summary>
    private (double Lo, double Hi) Band()
    {
        var feather = KaraokeBand.Feather(_glyphWidth, FontSize);
        var center = KaraokeBand.Center(Progress, feather, LeadIn, TrailOut);
        return ((center - feather) * _glyphWidth, (center + feather) * _glyphWidth);
    }

    private (Color Lit, Color Dim) Colours()
    {
        var color = (Foreground as ISolidColorBrush)?.Color ?? Colors.White;
        var dim = Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(BaseOpacity, 0, 1)), color.R, color.G, color.B);
        return (color, dim);
    }

    /// <summary>A letter's held-note pose as a transform about its centre, in word space.</summary>
    private Matrix LetterTransform(int i, in EmphasisParams parameters, out double scale, out double dx)
    {
        var piece = _pieces[i];
        var pose = EmphasisMotion.CharPose(EmphasisMs, i, _pieces.Count, parameters);
        scale = pose.Scale;
        dx = pose.OffsetXEm * FontSize;
        var dy = pose.OffsetYEm * FontSize;
        var cx = piece.X + piece.Width / 2;
        var cy = _layoutHeight / 2;
        return Matrix.CreateTranslation(piece.X - cx, -cy)
               * Matrix.CreateScale(scale, scale)
               * Matrix.CreateTranslation(cx + dx, cy + dy);
    }

    private void UpdateStops()
    {
        if (_pieces.Count == 0 || _glyphWidth <= 0) return;
        var (lit, dim) = Colours();
        var (lo, hi) = Band();
        if (!_perLetter)
        {
            SetStops(_pieces[0], lo, hi, lit, dim);
            return;
        }

        var parameters = EmphasisMotion.For(HeldDurationMs, IsLastInLine);
        for (int i = 0; i < _pieces.Count; i++)
        {
            var piece = _pieces[i];
            LetterTransform(i, parameters, out var s, out var dx);
            var cx = piece.X + piece.Width / 2;
            // The edge stays in word space (AMLL masks the word and its letters move
            // inside the mask): map the band back through this letter's swell and push.
            double ToLocal(double xWord) => (xWord - dx - cx) / s + cx - piece.X;
            SetStops(piece, ToLocal(lo), ToLocal(hi), lit, dim);
        }
    }

    // TextBlock.Render (sealed) draws the background and resolves where the text goes
    // (padding, alignment); only the text itself is drawn differently.
    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (_dirty)
        {
            EnsurePieces();
            UpdateStops();
        }
        if (_pieces.Count == 0 || _glyphWidth <= 0) return;

        using var offset = context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y));
        if (!_perLetter)
        {
            _pieces[0].Layout.Draw(context, default);
            return;
        }

        var parameters = EmphasisMotion.For(HeldDurationMs, IsLastInLine);
        for (int i = 0; i < _pieces.Count; i++)
        {
            using (context.PushTransform(LetterTransform(i, parameters, out _, out _)))
                _pieces[i].Layout.Draw(context, default);
        }
    }

    /// <summary>Points the piece's two stops at the band (piece-local pixels, unclamped):
    /// clamped to the piece, each carrying the colour the band has at that point.</summary>
    private static void SetStops(Piece piece, double lo, double hi, Color lit, Color dim)
    {
        var w = piece.Width;
        var o0 = Math.Clamp(lo / w, 0, 1);
        var o1 = Math.Clamp(hi / w, 0, 1);
        var c0 = Mix(dim, lit, KaraokeBand.LitAt(o0 * w, lo, hi));
        var c1 = Mix(dim, lit, KaraokeBand.LitAt(o1 * w, lo, hi));
        var stops = piece.Brush.GradientStops;
        Set(stops[0], o0, c0);
        Set(stops[1], o1, c1);

        static void Set(GradientStop stop, double offset, Color c)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (stop.Offset != offset) stop.Offset = offset;
            if (stop.Color != c) stop.Color = c;
        }
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), b.R, b.G, b.B);
}
