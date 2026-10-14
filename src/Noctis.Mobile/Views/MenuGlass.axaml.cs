using Avalonia;
using Avalonia.Media;
using Noctis.Controls;

namespace Noctis.Mobile.Views;

/// <summary>
/// The glass card of every pop-up on the phone (owner 10-14: "make sure it's applied to ALL the
/// options menus and ALL the pop-ups"): the one place its look is set, so the ⋯ menus, the sort
/// and filter menus, the album's quality note and the Add to Playlist sheet can never drift
/// apart. The glass settings and theme resources are MenuGlass.axaml's; this adds the soft
/// <see cref="Sheen"/> down from the top edge, faded with the glass (<see cref="GlassPanel.Fade"/>).
/// </summary>
public partial class MenuGlass : GlassPanel
{
    /// <summary>The light falling down from the card's top edge, drawn over the glass and under
    /// the content.</summary>
    public static readonly StyledProperty<IBrush?> SheenProperty =
        AvaloniaProperty.Register<MenuGlass, IBrush?>(nameof(Sheen));

    static MenuGlass()
    {
        AffectsRender<MenuGlass>(SheenProperty);
    }

    public MenuGlass()
    {
        InitializeComponent();
    }

    public IBrush? Sheen { get => GetValue(SheenProperty); set => SetValue(SheenProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var fade = Math.Clamp(Fade, 0, 1);
        if (Sheen is not { } sheen || fade <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        using (fade < 1 ? context.PushOpacity(fade) : default)
            context.DrawRectangle(sheen, null, new RoundedRect(new Rect(Bounds.Size), CornerRadius));
    }
}
