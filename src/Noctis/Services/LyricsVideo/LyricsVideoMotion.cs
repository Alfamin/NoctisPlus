using LineMotion = Noctis.Helpers.PlusLineMotion;
using Noctis.Helpers;

namespace Noctis.Services.LyricsVideo;

/// <summary>
/// Owner #243: the lyrics page's line motion as a pure function of track time, so any frame of the Reel can be drawn
/// on its own (in parallel, in any order) and still move exactly like the page. The page reacts to line changes as
/// they happen — the active line changes, the list glides there along <see cref="LineMotion"/>, lines below follow
/// with the cascade stagger, opacities and blur ease to their new distance and the active line grows from 0.96× to
/// full size. Here every change is known up front, so each change's starting values are worked out once and a frame
/// only evaluates the curves.
/// </summary>
internal sealed class LyricsVideoMotion
{
    /// <summary>Word-timed lines light this early (the page's 80 ms word lookahead).</summary>
    public const double WordLookahead = 0.080;

    /// <summary>Lines without word timing light earlier, so the glide has half arrived when the voice starts.</summary>
    public static readonly double LineLookahead = 0.080 + LineMotion.HalfTravelMs / 1000.0;

    public const double OpacitySeconds = 0.5;
    public const double ScaleSeconds = LineMotion.DurationMs / 1000.0;
    public const double GlideSeconds = LineMotion.DurationMs / 1000.0;
    public const double BaseFadeSeconds = 0.35;
    public const double InactiveScale = 0.96;
    public const double ActiveBaseOpacity = 0.45;
    private const double CascadeDelaySeconds = 0.035;
    private const int CascadeMaxLines = 8;

    private readonly IReadOnlyList<LyricsVideoLine> _lines;
    private readonly double[] _activation;
    private readonly float[] _tops;
    private readonly float _cascadeStepPx;

    // Per line change k (the line that became active, in order): when, and every line's value at that moment.
    private readonly int[] _eventLine;
    private readonly double[] _eventTime;
    private readonly double[][] _opacityFrom;
    private readonly double[][] _blurFrom;
    private readonly double[][] _scaleFrom;
    private readonly double[][] _baseFrom;
    private readonly double[] _scrollFrom;

    /// <param name="tops">Each line's top in the list, in pixels (line 0 at 0).</param>
    /// <param name="cascadeStepPx">The page's 16 px cascade step at this size.</param>
    public LyricsVideoMotion(IReadOnlyList<LyricsVideoLine> lines, float[] tops, float cascadeStepPx, double fromTime)
    {
        _lines = lines;
        _tops = tops;
        _cascadeStepPx = cascadeStepPx;
        _activation = new double[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            _activation[i] = lines[i].Start - (lines[i].Words is null ? LineLookahead : WordLookahead);

        // Changes from a little before the clip on (earlier ones have long settled): the first is taken as settled.
        var first = 0;
        while (first + 1 < lines.Count && _activation[first + 1] <= fromTime - 3) first++;
        var count = lines.Count - first;
        _eventLine = new int[count];
        _eventTime = new double[count];
        _opacityFrom = new double[count][];
        _blurFrom = new double[count][];
        _scaleFrom = new double[count][];
        _baseFrom = new double[count][];
        _scrollFrom = new double[count];
        for (var k = 0; k < count; k++)
        {
            var line = first + k;
            _eventLine[k] = line;
            _eventTime[k] = _activation[line];
            var n = lines.Count;
            _opacityFrom[k] = new double[n];
            _blurFrom[k] = new double[n];
            _scaleFrom[k] = new double[n];
            _baseFrom[k] = new double[n];
            for (var j = 0; j < n; j++)
            {
                if (k == 0)
                {
                    _opacityFrom[k][j] = OpacityFor(j, line);
                    _blurFrom[k][j] = BlurFor(j, line);
                    _scaleFrom[k][j] = j == line ? 1 : InactiveScale;
                    _baseFrom[k][j] = j == line ? ActiveBaseOpacity : 1;
                }
                else
                {
                    var t = _eventTime[k];
                    _opacityFrom[k][j] = Opacity(k - 1, j, t);
                    _blurFrom[k][j] = Blur(k - 1, j, t);
                    _scaleFrom[k][j] = Scale(k - 1, j, t);
                    _baseFrom[k][j] = Base(k - 1, j, t);
                }
            }
            _scrollFrom[k] = k == 0 ? _tops[line] : Scroll(k - 1, _eventTime[k]);
        }
    }

    /// <summary>The page's opacity ramp by distance from the active line.</summary>
    public static double OpacityFor(int line, int active) => Math.Abs(line - active) switch
    {
        0 => 1.0, 1 => 0.55, 2 => 0.32, 3 => 0.18, 4 => 0.12, 5 => 0.08, 6 => 0.06, 7 => 0.04, 8 => 0.03, 9 => 0.02, _ => 0.0,
    };

    /// <summary>The page's depth blur by distance (at the page's 46 px type; scaled by the caller).</summary>
    public static double BlurFor(int line, int active) => Math.Abs(line - active) switch
    {
        0 => 0.0, 1 => 4.0, 2 => 6.0, 3 => 8.0, _ => 10.0,
    };

    /// <summary>The line change in force at <paramref name="t"/>; -1 before the first.</summary>
    public int EventAt(double t)
    {
        var k = Array.BinarySearch(_eventTime, t);
        if (k < 0) k = ~k - 1;
        else while (k + 1 < _eventTime.Length && _eventTime[k + 1] <= t) k++;
        return k;
    }

    /// <summary>The active line at <paramref name="t"/>, or the clip's first line before any change.</summary>
    public int ActiveAt(double t)
    {
        var k = EventAt(t);
        return k < 0 ? _eventLine.Length > 0 ? _eventLine[0] : 0 : _eventLine[k];
    }

    public double LineOpacity(int line, double t) => Eval(t, (k, tt) => Opacity(k, line, tt), () => _opacityFrom[0][line]);
    public double LineBlur(int line, double t) => Eval(t, (k, tt) => Blur(k, line, tt), () => _blurFrom[0][line]);
    public double LineScale(int line, double t) => Eval(t, (k, tt) => Scale(k, line, tt), () => _scaleFrom[0][line]);
    /// <summary>The unsung word level on <paramref name="line"/>: <see cref="ActiveBaseOpacity"/> while it is sung, 1 at rest.</summary>
    public double LineBase(int line, double t) => Eval(t, (k, tt) => Base(k, line, tt), () => _baseFrom[0][line]);

    /// <summary>Where the list is scrolled: the top (in list pixels) that sits on the anchor.</summary>
    public double ScrollAt(double t) => Eval(t, Scroll, () => _scrollFrom[0]);

    /// <summary>Line <paramref name="line"/>'s cascade lag at <paramref name="t"/>: lines below the new active one settle
    /// top-down, each a little behind the one above (the page's AnimateScroll stagger).</summary>
    public double CascadeLag(int line, double t)
    {
        var k = EventAt(t);
        if (k <= 0) return 0;
        var active = _eventLine[k];
        if (line <= active) return 0;
        var elapsed = t - _eventTime[k];
        if (elapsed >= GlideSeconds + CascadeMaxLines * CascadeDelaySeconds) return 0;
        var delta = _tops[active] - _scrollFrom[k];
        if (Math.Abs(delta) < 8) return 0;
        var eased = LineMotion.Ease(elapsed / GlideSeconds);
        double prev = 0;
        for (var i = active + 1; i <= line; i++)
        {
            var delay = Math.Min(i - active, CascadeMaxLines) * CascadeDelaySeconds;
            var own = LineMotion.Ease(Math.Clamp((elapsed - delay) / GlideSeconds, 0, 1));
            // The list moves ahead of the line: positive pushes a line down while it catches up (the page's sign).
            prev = LineMotion.CascadeStep(delta * (eased - own), prev, _cascadeStepPx);
        }
        return prev;
    }

    /// <summary>True while a glide, scale or fade is still running at <paramref name="t"/> (the frame needs motion blur).</summary>
    public bool IsMoving(double t)
    {
        var k = EventAt(t);
        return k >= 0 && t - _eventTime[k] < GlideSeconds + CascadeMaxLines * CascadeDelaySeconds;
    }

    private double Eval(double t, Func<int, double, double> at, Func<double> before)
    {
        var k = EventAt(t);
        return k < 0 ? before() : at(k, t);
    }

    private double Opacity(int k, int line, double t) =>
        Lerp(_opacityFrom[k][line], OpacityFor(line, _eventLine[k]), CubicInOut((t - _eventTime[k]) / OpacitySeconds));

    private double Blur(int k, int line, double t) =>
        Lerp(_blurFrom[k][line], BlurFor(line, _eventLine[k]), CubicInOut((t - _eventTime[k]) / OpacitySeconds));

    private double Scale(int k, int line, double t) =>
        Lerp(_scaleFrom[k][line], line == _eventLine[k] ? 1 : InactiveScale, LineMotion.Ease((t - _eventTime[k]) / ScaleSeconds));

    private double Base(int k, int line, double t) =>
        Lerp(_baseFrom[k][line], line == _eventLine[k] ? ActiveBaseOpacity : 1, CubicInOut((t - _eventTime[k]) / BaseFadeSeconds));

    private double Scroll(int k, double t) =>
        Lerp(_scrollFrom[k], _tops[_eventLine[k]], LineMotion.Ease((t - _eventTime[k]) / GlideSeconds));

    private static double Lerp(double a, double b, double f) => a + (b - a) * f;

    public static double CubicInOut(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }
}
