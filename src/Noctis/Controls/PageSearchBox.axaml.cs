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
    private bool _filterScheduled;
    private ILocalSearchable? _pendingPage;
    private string? _pendingText;

    public PageSearchBox()
    {
        InitializeComponent();
        // A pending edit belongs to this page instance, never to the next playlist.
        DataContextChanged += (_, _) => CancelPending();
        DetachedFromVisualTree += (_, _) => CancelPending();
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e)
    {
        if (this.FindControl<Button>("ClearButton") is { } clear)
            clear.IsVisible = !string.IsNullOrEmpty(Input.Text);
        CancelPending();
        if (DataContext is ILocalSearchable page && page.SearchText != (Input.Text ?? ""))
        {
            _pendingPage = page;
            _pendingText = Input.Text ?? "";
            if (_filterScheduled) return;
            _filterScheduled = true;
            // No typing delay for a local list. Coalesce this dispatch's edits, then
            // let the page's existing background filter/stale-result guard do the work.
            Dispatcher.UIThread.Post(ApplyPending, DispatcherPriority.Background);
        }
    }

    private void CancelPending() { _pendingPage = null; _pendingText = null; }

    private void ApplyPending()
    {
        _filterScheduled = false;
        var page = _pendingPage;
        var text = _pendingText;
        CancelPending();
        if (page is not null && text is not null && ReferenceEquals(page, DataContext)
            && text == (Input.Text ?? "") && page.SearchText != text)
            page.ApplyFilter(text);
    }

    private void ApplyInput()
    {
        CancelPending();
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
