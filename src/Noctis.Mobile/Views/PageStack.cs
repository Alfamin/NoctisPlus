using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// Shows the top page of a stack, each page through its own ContentControl (ShellView's
/// DataTemplates resolve the view), and moves pages with <see cref="Motion"/>: a push slides
/// the new page in from the right over the old one, Back slides it away again; as a sheet the
/// whole stack rises from the bottom and drops back. While a page moves, the page it covers
/// stays on screen (the tab roots, <see cref="Root"/>, under the first page) and neither takes
/// touches; once it lands only the top page is left, so a page's view is re-created when it
/// comes back to the top (ScrollMemory keeps its place), as before. Pages stack by ZIndex,
/// never by reordering Children: a view taken out of the tree and put back would re-attach.
/// </summary>
public sealed class PageStack : Panel
{
    public static readonly StyledProperty<IBrush?> PageBackgroundProperty =
        AvaloniaProperty.Register<PageStack, IBrush?>(nameof(PageBackground));

    /// <summary>Behind each page, so a page sliding over another hides it (the tab pages draw on
    /// the shell's colour); null for sheets, whose rounded tops show what lies behind.</summary>
    public IBrush? PageBackground
    {
        get => GetValue(PageBackgroundProperty);
        set => SetValue(PageBackgroundProperty, value);
    }

    /// <summary>What shows when no page does (the tab roots): it drifts under the first page
    /// pushed and is hidden once that page lands. Null for the sheet stack.</summary>
    public Control? Root { get; set; }

    /// <summary>The page on top, or null.</summary>
    public MobilePage? Top { get; private set; }

    /// <summary>The sheet is on its way down.</summary>
    public bool IsDismissing { get; private set; }

    private readonly Dictionary<MobilePage, ContentControl> _views = new();
    private readonly Border _shade = new() { Background = Brushes.Black, Opacity = 0, IsHitTestVisible = false, IsVisible = false };
    private int _z;

    public PageStack()
    {
        Children.Add(_shade);
    }

    /// <summary>The view showing <paramref name="page"/>, while the stack holds one. Internal for tests.</summary>
    internal ContentControl? ViewOf(MobilePage page) => _views.GetValueOrDefault(page);

    private double Across => Bounds.Width > 0 ? Bounds.Width : 412;
    private double Down => TopLevel.GetTopLevel(this)?.Bounds.Height is > 0 and var h ? h : Bounds.Height > 0 ? Bounds.Height : 915;

    /// <summary>Shows <paramref name="page"/> at once: no motion, nothing else left (a tab switch, the first layout).</summary>
    public void Reset(MobilePage? page)
    {
        foreach (var view in _views.Values.ToList())
            if (view.Content != page) Remove(view);
        Motion.Stop(_shade);
        _shade.Opacity = 0;
        _shade.IsVisible = false;
        if (page != null)
        {
            var view = ViewFor(page);
            Motion.Stop(view);
            Rest(view);
        }
        if (Root != null)
        {
            Motion.Stop(Root);
            Rest(Root);
            Root.IsVisible = page == null;
        }
        Top = page;
    }

    /// <summary>Slides <paramref name="page"/> in from the right over the page on top.</summary>
    public void Push(MobilePage page)
    {
        var under = Top != null ? ViewOf(Top) : Root;
        var view = ViewFor(page);
        Top = page;
        var width = Across;
        _shade.ZIndex = view.ZIndex - 1;
        view.IsHitTestVisible = false;
        // A pixel inside the edge: wholly off screen, a list laid out in that frame would drop its rows.
        Motion.To(view, Motion.Push, Motion.TranslateX(0), from: Motion.TranslateX(width - 1), enter: true,
            done: () => view.IsHitTestVisible = true);
        Shade(Motion.PushShade, from: 0);
        if (under == null) return;
        under.IsHitTestVisible = false;
        under.IsVisible = true;
        Motion.To(under, Motion.Push, Motion.TranslateX(-width * Motion.PushShift), from: Motion.IsRunning(under) ? null : Motion.Identity,
            done: () => Retire(under));
    }

    /// <summary>Back: the top page slides away to the right and <paramref name="newTop"/> (or the
    /// root) comes back from where a push left it.</summary>
    public void Pop(MobilePage? newTop)
    {
        var leaving = Top != null ? ViewOf(Top) : null;
        if (leaving == null)
        {
            Reset(newTop);
            return;
        }
        Top = newTop;
        var width = Across;
        leaving.IsHitTestVisible = false;
        Motion.To(leaving, Motion.Push, Motion.TranslateX(width), from: Motion.IsRunning(leaving) ? null : Motion.Identity,
            done: () => Remove(leaving));

        // The page coming back: still on screen when the push it was under is cut short, else
        // made again, a little to the left under the shade, as the push left it.
        Control? under;
        bool fresh;
        if (newTop == null) (under, fresh) = (Root, Root is { IsVisible: false });
        else if (ViewOf(newTop) is { } existing) (under, fresh) = (existing, false);
        else (under, fresh) = (ViewFor(newTop, z: leaving.ZIndex - 2), true);
        if (under == null) return;
        _shade.ZIndex = leaving.ZIndex - 1;
        Shade(0, from: Motion.IsRunning(_shade) ? null : Motion.PushShade);
        under.IsVisible = true;
        under.IsHitTestVisible = false;
        var landing = under;
        Motion.To(under, Motion.Push, Motion.TranslateX(0), from: fresh ? Motion.TranslateX(-width * Motion.PushShift) : null,
            done: () => Rest(landing));
    }

    /// <summary>The stack with <paramref name="page"/> on it rises from below the screen.</summary>
    public void Present(MobilePage page)
    {
        IsDismissing = false;
        Reset(page);
        IsHitTestVisible = false;
        Motion.To(this, Motion.Sheet, Motion.TranslateY(0), from: Motion.IsRunning(this) ? null : Motion.TranslateY(Down), enter: true,
            done: () => IsHitTestVisible = true);
    }

    /// <summary>The stack drops below the screen, whatever pages it holds, then empties.</summary>
    public void Dismiss()
    {
        Top = null;
        IsDismissing = true;
        IsHitTestVisible = false;
        Motion.To(this, Motion.Sheet, Motion.TranslateY(Down), done: () =>
        {
            IsDismissing = false;
            Reset(null);
            RenderTransform = null;
            IsHitTestVisible = true;
        });
    }

    private void Shade(double opacity, double? from)
    {
        _shade.IsVisible = true;
        Motion.To(_shade, Motion.Push, opacity: opacity, fromOpacity: from,
            done: () => _shade.IsVisible = _shade.Opacity > 0);
    }

    /// <summary>A page or the root that has slid out of sight under the page on top.</summary>
    private void Retire(Control under)
    {
        if (under == Root)
        {
            Rest(under);
            under.IsVisible = Top == null;
        }
        else Remove((ContentControl)under);
    }

    private static void Rest(Control view)
    {
        view.RenderTransform = null;
        view.IsHitTestVisible = true;
    }

    private void Remove(ContentControl view)
    {
        Motion.Stop(view);
        if (view.Content is MobilePage page && _views.TryGetValue(page, out var v) && v == view) _views.Remove(page);
        Children.Remove(view);
        view.Content = null;
    }

    /// <summary>The view for <paramref name="page"/>: the one the stack holds, else a new one on
    /// top (or at <paramref name="z"/>).</summary>
    private ContentControl ViewFor(MobilePage page, int? z = null)
    {
        if (_views.TryGetValue(page, out var view)) return view;
        view = new ContentControl
        {
            Content = page,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            ZIndex = z ?? (_z += 2),
        };
        view.Bind(BackgroundProperty, this.GetObservable(PageBackgroundProperty));
        _views[page] = view;
        Children.Add(view);
        return view;
    }
}
