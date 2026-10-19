using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctis.ViewModels;

namespace Noctis.Controls;

/// <summary>Local list filtering only; never opens unified Search or requests online results.</summary>
public partial class PageSearchBox : UserControl
{
    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<PageSearchBox, string>(nameof(Placeholder), "Search this list");

    public string Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public PageSearchBox()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) => ApplyInput();
        // A pending edit belongs to this page instance, never to the next playlist.
        DataContextChanged += (_, _) => _debounce.Stop();
        DetachedFromVisualTree += (_, _) => _debounce.Stop();
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e)
    {
        if (this.FindControl<Button>("ClearButton") is { } clear)
            clear.IsVisible = !string.IsNullOrEmpty(Input.Text);
        _debounce.Stop();
        if (DataContext is ILocalSearchable page && page.SearchText != (Input.Text ?? ""))
            _debounce.Start();
    }

    private void ApplyInput()
    {
        _debounce.Stop();
        if (DataContext is ILocalSearchable page && page.SearchText != (Input.Text ?? ""))
            page.ApplyFilter(Input.Text ?? "");
    }

    private void OnFocusClick(object? sender, RoutedEventArgs e) => FocusSearch();
    public void FocusSearch() { Input.Focus(); Input.SelectAll(); }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        Input.SetCurrentValue(TextBox.TextProperty, "");
        ApplyInput();
        Input.Focus();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Input.SetCurrentValue(TextBox.TextProperty, "");
        ApplyInput();
        e.Handled = true;
    }
}
