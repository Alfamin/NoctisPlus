using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FreeMusicFinder;

/// <summary>Queue management stays separate from search results. Paging keeps all saved requests reachable.</summary>
internal sealed class QueueView : Grid
{
    private const int PageSize = 100;
    private readonly Downloads _downloads;
    private readonly Action _clear;
    private readonly Action<string> _status;
    private readonly TextBox _filter = new() { PlaceholderText = "Find a song in the queue…" };
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly StackPanel _confirmation;
    private readonly TextBlock _confirmText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _page = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous = new() { Content = "Previous" }, _next = new() { Content = "Next" };
    private readonly Dictionary<Download, QueueRow> _rows = new();
    private Download[] _visible = [];
    private int _index;

    public QueueView(Downloads downloads, Action clear, Action<string> status)
    {
        _downloads = downloads; _clear = clear; _status = status;
        RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto");
        var clearButton = new Button { Content = "Clear queue…", HorizontalAlignment = HorizontalAlignment.Right };
        clearButton.Click += (_, _) => { _confirmText.Text = "Clear all queued requests, failed requests and pending download imports? The current transfer will stop. Created Noctis playlists and downloaded files stay."; _confirmation!.IsVisible = true; };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8) };
        top.Children.Add(_filter); Grid.SetColumn(clearButton, 1); top.Children.Add(clearButton); Children.Add(top);
        _filter.TextChanged += (_, _) => { _index = 0; Refresh(); };
        var confirm = new Button { Content = "Yes, clear queue" };
        var keep = new Button { Content = "Keep queue" };
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        choices.Children.Add(confirm); choices.Children.Add(keep);
        _confirmation = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 12), IsVisible = false };
        _confirmation.Children.Add(_confirmText); _confirmation.Children.Add(choices);
        confirm.Click += (_, _) => Act(() => { _clear(); _confirmation.IsVisible = false; });
        keep.Click += (_, _) => _confirmation.IsVisible = false;
        Grid.SetRow(_confirmation, 1); Children.Add(_confirmation);
        var scroll = new ScrollViewer { Content = _list };
        Grid.SetRow(scroll, 2); Children.Add(scroll);
        var pages = new Grid { ColumnDefinitions=new ColumnDefinitions("Auto,*,Auto"),ColumnSpacing=12, Margin = new Thickness(0, 8) };
        Grid.SetColumn(_page,1);Grid.SetColumn(_next,2);
        pages.Children.Add(_previous); pages.Children.Add(_page); pages.Children.Add(_next);
        _previous.Click += (_, _) => { _index--; Refresh(); };
        _next.Click += (_, _) => { _index++; Refresh(); };
        Grid.SetRow(pages, 3); Children.Add(pages); Refresh();
    }
    private void Act(Action action)
    { try { action(); Refresh(); } catch (Exception ex) { _status("Queue change could not be saved: " + ex.Message); } }
    public void Refresh()
    {
        var filter = _filter.Text?.Trim() ?? "";
        var all = _downloads.Active.Concat(_downloads.History.Where(d => d.State == DownloadState.Failed)).Distinct()
            .Where(d => filter.Length == 0 || (d.Track.Artist + " " + d.Track.Title).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        _index = Math.Clamp(_index, 0, Math.Max(0, (all.Length - 1) / PageSize));
        var visible = all.Skip(_index * PageSize).Take(PageSize).ToArray();
        if (!_visible.SequenceEqual(visible))
        {
            _list.Children.Clear(); _rows.Clear();
            foreach (var item in visible)
            {
                var row = new QueueRow(item, () => Act(() => _downloads.DownloadNext(item)), () => Act(() => _downloads.Remove(item)));
                var line = new Border { Child = row, Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(8) }; line.Classes.Add("finder-song");
                _rows.Add(item, row); _list.Children.Add(line);
            }
            _visible = visible;
        }
        foreach (var row in _rows.Values) row.Refresh();
        _page.Text = all.Length == 0 ? (filter.Length == 0 ? "Queue is empty. Search for a song or paste a playlist link." : "No matching requests.")
            : $"{_index * PageSize + 1}–{Math.Min(all.Length, (_index + 1) * PageSize)} of {all.Length}";
        _page.TextWrapping = TextWrapping.Wrap;
        _previous.IsEnabled = _index > 0; _next.IsEnabled = (_index + 1) * PageSize < all.Length;
    }
    private sealed class QueueRow : Grid
    {
        private readonly Download _item;
        private readonly TextBlock _state = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private readonly Button _next = new() { Content = "Download now", VerticalAlignment = VerticalAlignment.Center };
        public QueueRow(Download item, Action next, Action remove)
        {
            _item = item; ColumnDefinitions = new ColumnDefinitions("*,Auto"); Margin = new Thickness(4, 6);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = item.Track.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(title, item.Track.Artist + " — " + item.Track.Title);
            text.Children.Add(title); text.Children.Add(_state); Children.Add(text);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 0, 0, 0) };
            var removeButton = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
            _next.Click += (_, _) => next(); removeButton.Click += (_, _) => remove();
            controls.Children.Add(_next); controls.Children.Add(removeButton); Grid.SetColumn(controls, 1); Children.Add(controls); Refresh();
        }
        public void Refresh()
        {
            _state.Text = _item.Track.Artist + " · " + (_item.State switch
            {
                DownloadState.Running => _item.Progress > 0 ? $"Downloading {_item.Progress:P0}" : "Requesting file…",
                DownloadState.Waiting => $"Waiting · {_item.RetryAt?.ToLocalTime():ddd HH:mm}",
                DownloadState.Failed => "Failed · " + _item.Error,
                _ => _item.Held ? "Ready · press Start queued downloads" : _item.Priority ? "Next to download" : "Queued",
            });
            ToolTip.SetTip(_state, string.IsNullOrEmpty(_item.Error) ? null : _item.Error);
            _next.IsEnabled = _item.State==DownloadState.Failed || (_item.IsActive && _item.State != DownloadState.Running && !_item.Priority);
        }
    }
}
