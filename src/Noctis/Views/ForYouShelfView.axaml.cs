using Avalonia;
using Avalonia.Controls;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ForYouShelfView : UserControl
{
    private ForYouShelfViewModel? _observed;
    private bool _attached;
    public ForYouShelfView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => { _attached = true; UpdateActivity(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; UpdateActivity(); };
        DataContextChanged += (_, _) => UpdateActivity();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdateActivity(); };
    }
    private void UpdateActivity()
    {
        if (!ReferenceEquals(_observed, DataContext)) _observed?.SetActive(false);
        _observed = DataContext as ForYouShelfViewModel;
        _observed?.SetActive(_attached && IsVisible);
    }
}
