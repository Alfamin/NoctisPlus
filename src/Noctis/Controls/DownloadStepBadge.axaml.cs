using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Services.Search;

namespace Noctis.Controls;

/// <summary>
/// A running download's ring with what it is doing inside (owner #54): asking the bot (magnifier, pulsing), trying
/// the other bot (sync arrows, turning), receiving the file (arrow, ring filling) and saving (disk, ring full).
/// No text on screen; the tooltip names the step.
/// </summary>
public partial class DownloadStepBadge : UserControl
{
    public static readonly StyledProperty<OnlineDownloadStep> StepProperty =
        AvaloniaProperty.Register<DownloadStepBadge, OnlineDownloadStep>(nameof(Step));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<DownloadStepBadge, double>(nameof(Progress));

    /// <summary>The step icon's size (the player's larger ring uses a larger icon).</summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<DownloadStepBadge, double>(nameof(IconSize), 8);

    static DownloadStepBadge()
    {
        StepProperty.Changed.AddClassHandler<DownloadStepBadge>((badge, _) => badge.Update());
        ProgressProperty.Changed.AddClassHandler<DownloadStepBadge>((badge, _) => badge.Update());
        IconSizeProperty.Changed.AddClassHandler<DownloadStepBadge>((badge, _) => badge.Update());
    }

    public DownloadStepBadge()
    {
        InitializeComponent();
        Update();
    }

    public OnlineDownloadStep Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    /// <summary>The tooltip for a step; also the accessible name.</summary>
    internal static string Describe(OnlineDownloadStep step) => step switch
    {
        OnlineDownloadStep.Switching => "Trying the other bot",
        OnlineDownloadStep.Receiving => "Downloading",
        OnlineDownloadStep.Saving => "Saving",
        _ => "Asking the bot",
    };

    // Every song row carries a hidden badge: its icon animates only while the badge is on screen.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property.Name == nameof(IsEffectivelyVisible)) Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Update();
    }

    private void Update()
    {
        var step = Step;
        var receiving = step == OnlineDownloadStep.Receiving;
        var live = IsEffectivelyVisible && VisualRoot is not null;
        Glyph.Width = Glyph.Height = Arrow.Width = Arrow.Height = IconSize;
        Ring.IsIndeterminate = step is OnlineDownloadStep.None or OnlineDownloadStep.Asking or OnlineDownloadStep.Switching;
        Ring.Value = step == OnlineDownloadStep.Saving ? 1 : Progress;
        Arrow.IsVisible = receiving;
        Arrow.Classes.Set("bob", live && receiving);
        Glyph.IsVisible = !receiving;
        Glyph.Data = Icon(step switch
        {
            OnlineDownloadStep.Switching => "SyncIcon",
            OnlineDownloadStep.Saving => "SaveDiskIcon",
            _ => "SearchIcon",
        });
        Glyph.Classes.Set("spin", live && step == OnlineDownloadStep.Switching);
        Glyph.Classes.Set("pulse", live && !receiving && step != OnlineDownloadStep.Switching);
        var text = Describe(step);
        ToolTip.SetTip(this, receiving && Progress > 0 ? $"{text} · {Progress:P0}" : text);
        AutomationProperties.SetName(this, text);
    }

    private Geometry? Icon(string key)
        => this.TryFindResource(key, out var value) || (Application.Current?.TryFindResource(key, out value) ?? false) ? value as Geometry : null;
}
