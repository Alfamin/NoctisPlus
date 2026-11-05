using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class ForYouView : UserControl
{
    public static readonly StyledProperty<bool> PreviewOnlyProperty = AvaloniaProperty.Register<ForYouView, bool>(nameof(PreviewOnly));
    public bool PreviewOnly { get => GetValue(PreviewOnlyProperty); set => SetValue(PreviewOnlyProperty, value); }
    public ForYouView() { InitializeComponent(); }
    private void RowViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (sender is Control { DataContext: OnlineSongItem item, IsEffectivelyVisible: true } row
            && DataContext is ForYouViewModel vm && row.Bounds.Height > 0
            && e.EffectiveViewport.Intersect(new Rect(row.Bounds.Size)).Height >= row.Bounds.Height * .5)
            vm.RecordVisible(item.Song.Id);
    }
}
