using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The Apple-Music-style page tint the desktop album page uses: the cover's edge colour as a
/// flat background, with the page text flipping dark on light covers. Extraction is a Skia
/// decode, so it runs off the UI thread, and a generation counter drops a result that lands
/// after the page moved to another cover.
/// </summary>
public sealed partial class PageTint : ObservableObject
{
    /// <summary>Luminance above which page text goes dark (the desktop's AlbumDetailViewModel.LightTintThreshold).</summary>
    public const double LightTintThreshold = 0.55;

    /// <summary>What one extraction yields: the colour the picture meets the page with (its
    /// bottom rows), the colour under the status bar at rest (its top rows), and the page colour.</summary>
    public readonly record struct Colours(Color Cover, Color Top, Color Page);

    private readonly Func<string, Colours?> _extract;
    private readonly Func<Func<Color?>, Task<Color?>> _runBackground;
    private int _generation;
    private Colours? _extracted;

    public PageTint(Func<string, Color?>? extract = null, Func<Func<Color?>, Task<Color?>>? runBackground = null)
    {
        _extract = extract != null
            ? path => extract(path) is { } c ? FromCover(c) : null
            : path => HeroFade
                ? FromHero(DominantColorExtractor.ExtractHeroColorsFromFile(path))
                : DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(path) is { } edge ? FromCover(edge) : null;
        _runBackground = runBackground ?? (work => Task.Run(work));
    }

    /// <summary>
    /// The page continues a full-bleed cover (the album hero): the colour comes from the
    /// cover's bottom rows, which the hero fades into, not its four edges, and the page itself
    /// is a deep shade of it (<see cref="DominantColorExtractor.DeepPageColor"/>), Apple Music's
    /// page under a cover: the cover's beige deepens into a brown page with white text, instead
    /// of a flat beige page with black text. The text flips by WCAG contrast
    /// (<see cref="PrefersDarkText"/>) instead of <see cref="LightTintThreshold"/>. Set before
    /// <see cref="Load"/>.
    /// </summary>
    public bool HeroFade { get; set; }

    /// <summary>The page background; null (the theme's surface shows through) without a cover.</summary>
    [ObservableProperty] private IBrush? _background;

    [ObservableProperty] private bool _hasTint;
    [ObservableProperty] private bool _isLight;

    /// <summary>The page colour (the floor the cover's colour deepens into on a hero page); null without one.</summary>
    [ObservableProperty] private Color? _tintColor;

    /// <summary>The colour the picture meets the page with (its bottom rows): where the fade and
    /// the page's ramp start. On a hero page it is lighter than <see cref="TintColor"/>; else the same.</summary>
    [ObservableProperty] private Color? _coverColor;

    /// <summary>The picture's own colour under the status bar at rest (its top rows): the haze
    /// that keeps the bar readable is this colour, not a white (theme) one and not the bottom's.</summary>
    [ObservableProperty] private Color? _topColor;

    /// <summary>
    /// Settings › Appearance › Album Colours: off, the page stays the theme's plain page
    /// (<see cref="HasTint"/> false, no <see cref="Background"/>) whatever the cover; the extracted
    /// colours are kept, so switching back on repaints without another decode.
    /// </summary>
    [ObservableProperty] private bool _enabled = true;

    partial void OnEnabledChanged(bool value) => Apply(_extracted);

    /// <summary>The latest extraction; tests await it.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    public void Load(string? artworkPath)
    {
        var generation = ++_generation;
        if (string.IsNullOrEmpty(artworkPath) || !File.Exists(artworkPath))
        {
            Apply((Colours?)null);
            Ready = Task.CompletedTask;
            return;
        }
        Ready = LoadAsync(artworkPath, generation);
    }

    private async Task LoadAsync(string path, int generation)
    {
        Colours? colours = null;
        Color? cover;
        try
        {
            // The background work hands back the cover colour alone (the shape tests drive);
            // the full set rides along in the capture.
            cover = await _runBackground(() => (colours = _extract(path))?.Cover);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artwork", $"Page tint failed: {ex.Message}");
            cover = null;
        }
        if (generation != _generation) return;   // a newer cover won
        Apply(colours ?? (cover is { } c ? FromCover(c) : null));
    }

    /// <summary>The colours for a cover whose edge colour is <paramref name="cover"/>: a deep
    /// page under a hero, the colour itself elsewhere (the mini player's progress fill).</summary>
    private Colours FromCover(Color cover) =>
        new(cover, cover, HeroFade ? DominantColorExtractor.DeepPageColor(cover) : cover);

    private Colours? FromHero(DominantColorExtractor.HeroColors? hero) =>
        hero == null ? null : new Colours(hero.Bottom, hero.Top, DominantColorExtractor.DeepPageColor(hero.Bottom));

    /// <summary>Paints the page in <paramref name="color"/> as if it were a cover's edge colour.</summary>
    public void Apply(Color? color) => Apply(color is { } c ? FromCover(c) : null);

    private void Apply(Colours? colours)
    {
        _extracted = colours;
        if (!Enabled || colours is not { } c)
        {
            Background = null;
            CoverColor = null;
            TopColor = null;
            TintColor = null;
            HasTint = false;
            IsLight = false;
            return;
        }
        Background = new SolidColorBrush(c.Page);
        CoverColor = c.Cover;
        TopColor = c.Top;
        IsLight = HeroFade ? PrefersDarkText(c.Page) : DominantColorExtractor.GetRelativeLuminance(c.Page) > LightTintThreshold;
        TintColor = c.Page;
        HasTint = true;
    }

    /// <summary>WCAG 2 contrast ratio of two colours, 1 … 21.</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var la = DominantColorExtractor.GetRelativeLuminance(a);
        var lb = DominantColorExtractor.GetRelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Black text reads at least as well as white on <paramref name="background"/>.
    /// The crossover sits at luminance ~0.18, where both give ~4.6:1, so text in the better of
    /// the two always clears WCAG AA (4.5:1).</summary>
    public static bool PrefersDarkText(Color background) =>
        PrefersDarkText(DominantColorExtractor.GetRelativeLuminance(background));

    /// <summary><see cref="PrefersDarkText(Color)"/> for a backdrop's relative luminance.</summary>
    public static bool PrefersDarkText(double luminance) => (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05);

    /// <summary>Opacity floor of secondary text: as dim as Apple's grey labels when the tint allows.</summary>
    public const double SubtleTextMinOpacity = 0.6;

    /// <summary>
    /// Secondary text on <paramref name="background"/>: <paramref name="text"/> blended toward
    /// the background as far as <see cref="SubtleTextMinOpacity"/>, but no further than keeps
    /// 4.5:1 (full strength on the mid tints where even the primary text only just clears it).
    /// White text is added to the background (<see cref="PlusLighter"/>) rather than mixed, so it
    /// keeps the page's hue: Apple's secondary text on its #4C2000 Silk Sonic page is #E5B899,
    /// the page plus 60% white; mixing gave a grey #B6A79B.
    /// </summary>
    public static Color SubtleTextOn(Color background, Color text)
    {
        var additive = text == Colors.White;
        for (var a = SubtleTextMinOpacity; a < 1; a += 0.02)
        {
            var blended = additive ? PlusLighter(background, a) : Mix(background, text, a);
            if (ContrastRatio(blended, background) >= 4.5) return blended;
        }
        return text;
    }

    /// <summary><paramref name="under"/> with white at <paramref name="amount"/> added to each
    /// channel (the plus-lighter blend of Apple's vibrant labels), opaque.</summary>
    public static Color PlusLighter(Color under, double amount)
    {
        byte Add(byte u) => (byte)Math.Min(255, Math.Round(u + 255 * amount));
        return Color.FromRgb(Add(under.R), Add(under.G), Add(under.B));
    }

    /// <summary>A colour at <paramref name="alpha"/> that, laid over <paramref name="under"/>,
    /// lands on <see cref="PlusLighter"/>(<paramref name="under"/>, <paramref name="amount"/>):
    /// a translucent wash or rule in the page's own hue rather than a grey one.</summary>
    public static Color PlusLighterOver(Color under, double amount, double alpha)
    {
        var lit = PlusLighter(under, amount / alpha);
        return Color.FromArgb((byte)Math.Round(alpha * 255), lit.R, lit.G, lit.B);
    }

    /// <summary><paramref name="over"/> at <paramref name="amount"/> over <paramref name="under"/>, opaque.</summary>
    public static Color Mix(Color under, Color over, double amount)
    {
        byte Lerp(byte u, byte o) => (byte)Math.Round(u + (o - u) * amount);
        return Color.FromRgb(Lerp(under.R, over.R), Lerp(under.G, over.G), Lerp(under.B, over.B));
    }
}
