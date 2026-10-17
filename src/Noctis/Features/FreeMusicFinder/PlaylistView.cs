using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FreeMusicFinder;

internal sealed class PlaylistView : Grid, IDisposable
{
    private readonly SpotifyPlaylists _imports;
    private readonly Downloads _downloads;
    private readonly Func<string> _folder;
    private readonly Func<bool> _loggedIn;
    private readonly TextBox _links = new() { AcceptsReturn = true, Height = 82, TextWrapping = TextWrapping.Wrap,
        PlaceholderText = "Paste Spotify playlist links here, one per line" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _lists = new() { Spacing = 8 };
    private readonly StackPanel _missing = new() { Spacing = 4 };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _page = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _create = new() { Content = "Create playlists" };
    private readonly Button _queue = new() { Content = "Queue all missing songs" };
    private readonly Button _queueOne = new() { Content = "Queue these songs", IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _start = new() { Content = "Start queued downloads" };
    private readonly Button _check = new() { Content = "Check library again" };
    private readonly Button _cancel = new() { Content = "Stop import", IsVisible = false };
    private readonly Button _previous = new() { Content = "Previous" }, _next = new() { Content = "Next" };
    private readonly ProgressBar _progress = new() { Height = 4, IsVisible = false };
    private SpotifyPlaylist? _selected;
    private int _index;
    private string _listStamp = "";
    private CancellationTokenSource? _importStop;
    private bool _disposed, _posted, _stale;
    private long _progressShown;
    public PlaylistView(SpotifyPlaylists imports, Downloads downloads, Func<string> folder, Func<bool> loggedIn)
    {
        _imports = imports; _downloads = downloads; _folder = folder; _loggedIn = loggedIn;
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"); RowSpacing = 8; Margin = new Thickness(0, 8, 0, 0);
        FinderTheme.Primary(_create); FinderTheme.Primary(_start);
        var intro = new StackPanel { Spacing = 8 };
        intro.Children.Add(new TextBlock { Text = "Bring your playlists over", FontSize = 19, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        intro.Children.Add(FinderTheme.Caption("Paste links → match your library → fill the gaps. Shared songs use the same local file."));
        intro.Children.Add(_links);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { _create, _queue, _start, _check, _cancel }) { button.Margin = new Thickness(0, 0, 8, 6); actions.Children.Add(button); }
        intro.Children.Add(actions); Children.Add(FinderTheme.Card(intro));
        Grid.SetRow(_progress, 1); Children.Add(_progress);
        _status.Margin = new Thickness(0, 8); Grid.SetRow(_status, 2); Children.Add(_status);
        var content = new StackPanel { Spacing = 12 };
        // The playlist picked with its "Missing songs" button: its name and counts, and the button for its songs alone.
        var picked = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        _details.VerticalAlignment = VerticalAlignment.Center;
        picked.Children.Add(_details); Grid.SetColumn(_queueOne, 1); picked.Children.Add(_queueOne);
        ToolTip.SetTip(_queue, "The missing songs of every imported playlist. A song several playlists share is queued once.");
        ToolTip.SetTip(_queueOne, "Only this playlist's missing songs. Nothing downloads until you press Start queued downloads.");
        content.Children.Add(_lists); content.Children.Add(picked); content.Children.Add(_missing);
        var pages = new WrapPanel();
        foreach (var control in new Control[] { _previous, _page, _next }) { control.Margin = new Thickness(0, 0, 8, 6); pages.Children.Add(control); }
        content.Children.Add(pages);
        var scroll = new ScrollViewer { Content = content }; Grid.SetRow(scroll, 3); Children.Add(scroll);
        _create.Click += (_, _) => Act(async () =>
        {
            _importStop = new();
            try { await _imports.ImportAsync(_links.Text ?? "", _importStop.Token); }
            finally { _importStop.Dispose(); _importStop = null; }
        });
        _queue.Click += (_, _) => Act(async () => { await _imports.QueueMissingAsync(_folder()); });
        _queueOne.Click += (_, _) => { if (_selected is {} job) Act(async () => { await _imports.QueueMissingAsync(_folder(), only: job); }); };
        _start.Click += (_, _) => Act(() =>
        {
            if (!_loggedIn()) throw new InvalidOperationException("Log in to Telegram below, then start queued downloads. Your imported playlists are ready.");
            _downloads.ReleaseHeld(); _downloads.SetPaused(false);
            _status.Text = "Downloads started. Playlists fill as Noctis detects the new music. Manage requests in the Queue tab.";
            return Task.CompletedTask;
        });
        _check.Click += (_, _) => Act(() => _imports.CheckLibraryAsync());
        _cancel.Click += (_, _) => _importStop?.Cancel();
        _previous.Click += (_, _) => { _index--; RefreshMissing(); };
        _next.Click += (_, _) => { _index++; RefreshMissing(); };
        _imports.Changed += Schedule; _downloads.Changed += OnDownload;
        AttachedToVisualTree += (_, _) => { if (_stale && !_disposed) { _stale = false; Refresh(); } };
        Refresh();
    }
    private async void Act(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "Import stopped. Finished playlists and metadata progress were saved; paste the same links to continue."; }
        catch (Exception ex) { if (!_disposed) _status.Text = SpotifyPlaylists.Explain(ex); }
        finally { if (!_disposed) Refresh(false); }
    }
    private void OnDownload(Download download)
    {
        // A transfer reports its progress many times a second; the missing list shows it once a second.
        if (download.State == DownloadState.Running)
        {
            var now = Environment.TickCount64;
            lock (this) { if (now - _progressShown < 1000) return; _progressShown = now; }
        }
        Schedule();
    }
    private void Schedule()
    {
        // Metadata updates can arrive in parallel; show one UI refresh per dispatcher turn.
        lock (this) { if (_disposed || _posted) return; _posted = true; }
        Dispatcher.UIThread.Post(() =>
        {
            lock (this) _posted = false;
            if (_disposed) return;
            // Another tab is open: the list is drawn when this one is shown again, not on every change meanwhile.
            if (TopLevel.GetTopLevel(this) is null) { _stale = true; return; }
            Refresh();
        });
    }
    private void Refresh(bool status = true)
    {
        var jobs = _imports.Jobs;
        if (status) _status.Text = _imports.Status;
        _create.IsEnabled = _queue.IsEnabled = _check.IsEnabled = !_imports.Busy;
        _queue.IsEnabled &= jobs.Any(j => j.Created && !j.FillingStopped && j.Found < j.Total);
        _check.IsEnabled &= jobs.Count > 0;
        _start.IsEnabled = _downloads.Active.Any(d => d.Held) || _downloads.Paused && _downloads.Active.Count > 0;
        _cancel.IsVisible = _importStop is not null;
        _progress.IsVisible = _progress.IsIndeterminate = _imports.Busy;
        if (_selected is null || !jobs.Contains(_selected)) _selected = jobs.FirstOrDefault(j => j.Manifest is { CompleteIds: true });
        var stamp = string.Join("\n", jobs.Select(j => j.LibraryId + "|" + j.Manifest?.Name + "|" + j.Status + "|" + _imports.Busy + "|" + ReferenceEquals(j, _selected)));
        if (stamp != _listStamp)
        {
            _listStamp = stamp; _lists.Children.Clear();
            foreach (var job in jobs)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = job.Manifest is {} m ? SpotifyPlaylists.DisplayName(m.Name) : "Spotify playlist", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                text.Children.Add(new TextBlock { Text = job.Status, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
                var show = new Button { Content = "Missing songs", IsEnabled = job.Manifest is { CompleteIds: true } };
                ToolTip.SetTip(show, "List this playlist's missing songs below. From there they can be queued on their own.");
                void Pick()
                {
                    _selected = job; _index = 0; Refresh(false);
                    Dispatcher.UIThread.Post(() => { if (!_disposed) _queueOne.BringIntoView(); });
                }
                show.Click += (_, _) => Pick();
                var remove = new Button { Content = "Remove import", IsEnabled = !_imports.Busy };
                ToolTip.SetTip(remove, "Stop filling this import. Its Noctis playlist and music are kept.");
                remove.Click += (_, _) => Act(() => _imports.RemoveAsync(job));
                row.Children.Add(text); Grid.SetColumn(show, 1); row.Children.Add(show); Grid.SetColumn(remove, 2); row.Children.Add(remove);
                // The one whose missing songs are listed below is marked.
                var card = FinderTheme.Card(row, 12); if (ReferenceEquals(job, _selected)) card.Classes.Add("finder-selected");
                if (show.IsEnabled)
                {
                    // A click anywhere on the card picks the playlist, as its "Missing songs" button does; the buttons keep their own clicks.
                    card.Classes.Add("finder-pick"); card.Cursor = new Cursor(StandardCursorType.Hand);
                    card.Tapped += (_, e) => { if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is null) Pick(); };
                }
                _lists.Children.Add(card);
            }
        }
        RefreshMissing();
    }
    private void RefreshMissing()
    {
        var job = _selected; _missing.Children.Clear();
        if (job?.Manifest is not {} manifest) { _details.Text = ""; _page.Text = ""; _previous.IsVisible = _next.IsVisible = _queueOne.IsVisible = false; return; }
        var missing = manifest.Entries.Where(e => !job.Matches.ContainsKey(e.Position)).ToArray();
        _queueOne.IsVisible = true; _queueOne.IsEnabled = !_imports.Busy && job.Created && !job.FillingStopped && missing.Length > 0;
        _details.Text = SpotifyPlaylists.DisplayName(manifest.Name) + $" · {job.Found}/{job.Total} present · {missing.Length} missing";
        _index = Math.Clamp(_index, 0, Math.Max(0, (missing.Length - 1) / 100));
        var history = _downloads.History;
        foreach (var entry in missing.Skip(_index * 100).Take(100))
        {
            var request = entry.Request;
            var download = request is null ? null : history.LastOrDefault(d => d.Track.Request is {} r && SpotifyPlaylists.SameRecording(request, r));
            var state = download is null ? request is null ? entry.Issue : job.MissingReasons.GetValueOrDefault(entry.Position, "Missing from your library") : download.State switch
            {
                DownloadState.Saved or DownloadState.AlreadyThere => "Saved · waiting for Noctis to scan the download folder; use Rescan Library if folder watching is off",
                DownloadState.Failed => "Unavailable · " + download.Error,
                DownloadState.Waiting => "Waiting · " + download.Error,
                DownloadState.Running => $"Downloading · {download.Progress:P0}",
                DownloadState.Removed => "Removed from queue",
                _ => download.Held ? "Queued · ready to start" : "Queued",
            };
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = $"{entry.Position}. " + (request is null ? "Unavailable entry" : request.Title), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(FinderTheme.Caption((request is null ? "" : request.Artist + " · ") + state));
            _missing.Children.Add(FinderTheme.Card(row, 10));
        }
        _page.Text = missing.Length == 0 ? "All songs are in Noctis." : $"{_index * 100 + 1}–{Math.Min(missing.Length, (_index + 1) * 100)} of {missing.Length} missing";
        _previous.IsVisible = _next.IsVisible = missing.Length > 100;
        _previous.IsEnabled = _index > 0; _next.IsEnabled = (_index + 1) * 100 < missing.Length;
    }
    public void Dispose()
    {
        _disposed = true; _imports.Changed -= Schedule; _downloads.Changed -= OnDownload;
        // Closing the window leaves the import running; only Stop import cancels it.
    }
}
