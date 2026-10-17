using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using static FreeMusicFinder.NativeCatalogBridge;

namespace FreeMusicFinder;

/// <summary>Displayed in the native DiscGroupList. Owned rows reuse Noctis's track template;
/// unavailable-on-disk rows only expose explicit downloads, never fake playback tracks.</summary>
internal sealed class NativeAlbumRows : StackPanel, IDisposable
{
    private readonly object _main, _vm;
    private readonly CatalogAlbum _metadata;
    private readonly DiscoveryActions _actions;
    private readonly Action<CatalogArtist> _artist;
    private readonly IDataTemplate? _ownedTemplate;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = .7 };
    private readonly HashSet<string> _selected = new();
    private readonly IReadOnlyList<object> _originalTracks;
    private readonly List<object> _extraTracks = new();
    private Dictionary<string, DiscoveryMatch> _matches = new();
    private readonly WrapPanel _confirmation = new();
    private readonly DispatcherTimer _timer;
    private readonly Button _headerAction;
    private Panel? _headerPanel;
    private Panel? _headerParent;
    private WrapPanel? _headerWrap;
    private Panel? _nativeHeader;
    private Thickness _nativeHeaderMargin;
    private string _signature = "";
    private bool _disposed, _matching, _writing;
    public NativeAlbumRows(object main, object vm, CatalogAlbum metadata, DiscoveryActions actions, IDataTemplate? groups, Action<CatalogArtist> artist)
    {
        _main = main; _vm = vm; _metadata = metadata; _actions = actions; _artist = artist; Spacing = 8;
        _originalTracks = Values(Get(vm, "Tracks"));
        _ownedTemplate = TrackTemplate(vm, groups) ?? throw Unsupported("Noctis track row template");
        _headerAction = Button("Download all missing…", () => { if (_selected.Count > 0) Queue(metadata.Songs.Where(s => _selected.Contains(s.Id)).ToArray(), false); else ConfirmAll(); });
        ToolTip.SetTip(_headerAction, "Download the missing songs in this album. Ctrl+click rows to choose several downloads.");
        Children.Add(_status); Children.Add(_confirmation); Children.Add(_rows);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; _timer.Tick += (_, _) => { _ = RefreshAsync(); };
        AttachedToVisualTree += (_, _) => { if (!_disposed) { _timer.Start(); Dispatcher.UIThread.Post(MountHeaderAction); } }; DetachedFromVisualTree += (_, _) => _timer.Stop();
    }
    private void MountHeaderAction()
    {
        if (_disposed || _headerPanel is not null) return;
        var page = this.GetVisualAncestors().OfType<UserControl>().FirstOrDefault(p => ReferenceEquals(p.DataContext, _vm));
        var play = page?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => ReferenceEquals(b.Command, Get(_vm, "PlayAllCommand")));
        if (play?.Parent is not Panel panel) return;
        _headerAction.Padding = play.Padding; _headerAction.CornerRadius = play.CornerRadius; _headerAction.FontFamily = play.FontFamily; _headerAction.FontSize = play.FontSize; _headerAction.FontWeight = play.FontWeight;
        if (panel.Parent is Panel outer)
        {
            // Preserve the actual native action bar and its controls, allowing the added
            // download action to wrap below it when the album column is narrow.
            var at = outer.Children.IndexOf(panel); _nativeHeaderMargin = panel.Margin;
            var wrap = new WrapPanel { ItemSpacing = 12, LineSpacing = 8, Margin = panel.Margin, VerticalAlignment = panel.VerticalAlignment };
            foreach (var name in panel.Classes) wrap.Classes.Add(name);
            outer.Children.Remove(panel); panel.Margin = default; wrap.Children.Add(panel); wrap.Children.Add(_headerAction); outer.Children.Insert(at, wrap);
            _headerPanel = wrap; _headerParent = outer; _headerWrap = wrap; _nativeHeader = panel;
        }
        else { panel.Children.Add(_headerAction); _headerPanel = panel; }
        ShowSelection();
    }
    private void ShowSelection()
    {
        _headerAction.Content = _selected.Count > 0 ? $"Queue {_selected.Count} downloads" : "Download all missing…";
        _headerAction.IsEnabled = _selected.Count > 0 || _metadata.Notice.Length == 0 && _matches.Values.Any(m => m.Local is null && m.Download is null);
    }
    private static IDataTemplate? TrackTemplate(object vm, IDataTemplate? groups)
    {
        if (groups is null) return null;
        var trackType = Get(vm, "Album")!.GetType().GetProperty("Tracks")!.PropertyType.GenericTypeArguments.Single();
        var groupType = vm.GetType().Assembly.GetType("Noctis.ViewModels.DiscGroup") ?? throw Unsupported("native disc group");
        var collection = Activator.CreateInstance(typeof(System.Collections.ObjectModel.ObservableCollection<>).MakeGenericType(trackType));
        var group = Activator.CreateInstance(groupType, [1, "", collection]);
        var built = groups.Build(group); if (built is null) return null; built.DataContext = group;
        return built.GetLogicalDescendants().OfType<ListBox>().FirstOrDefault()?.ItemTemplate;
    }
    public async Task RefreshAsync(bool requireSuccess = false)
    {
        if (_disposed || _matching) return; _matching = true;
        try
        {
            var matches = await _actions.MatchAsync(_metadata.Songs, CancellationToken.None); if (_disposed) return; _matches = matches;
            var current = string.Join("|", _metadata.Songs.Select(s => s.Id + ":" + matches[s.Id].Local?.Id + ":" + matches[s.Id].Detail));
            if (current != _signature) { UpdateOwnedTracks(); Render(); _signature = current; }
        }
        catch (Exception ex) { if (!_disposed) { _signature = ""; _matches.Clear(); _status.Text = "Could not check your library: " + ex.Message; _rows.IsEnabled = false; _headerAction.IsEnabled = false; } if (requireSuccess) throw; }
        finally { _matching = false; }
    }
    private void UpdateOwnedTracks()
    {
        var original = Get(_vm, "Album")!; var album = Activator.CreateInstance(original.GetType())!;
        foreach (var name in new[] { "Id", "Name", "Artist", "Year", "ArtworkPath" }) Set(album, name, Get(original, name));
        var locals = LocalTracks(_main).ToDictionary(t => (Guid)Get(t, "Id")!);
        var owned = _metadata.Songs.Select(s => _matches[s.Id].Local?.Id).Where(id => id is not null).Distinct().Where(id => locals.ContainsKey(id!.Value)).Select(id => locals[id!.Value]).ToArray();
        _extraTracks.Clear(); _extraTracks.AddRange(_originalTracks.Where(t => !owned.Contains(t) && Get(t, "FilePath") is string path && File.Exists(path)));
        owned = owned.Concat(_extraTracks).ToArray();
        var type = original.GetType().GetProperty("Tracks")!.PropertyType.GenericTypeArguments.Single();
        Set(album, "Tracks", List(type, owned)); Set(album, "TrackCount", _metadata.Release.Count > 0 ? _metadata.Release.Count : _metadata.Songs.Count);
        Set(album, "TotalDuration", TimeSpan.FromSeconds(_metadata.Songs.Sum(s => s.Seconds ?? 0)));
        var view = _vm.GetType().GetField("_library", Private)!.GetValue(_vm);
        if (view is not CatalogAlbumLibrary overlay) throw Unsupported("isolated album library view");
        overlay.Album = album; Set(_vm, "Album", album);
        var tracks = Get(_vm, "Tracks")!;
        if (!Values(tracks).SequenceEqual(owned))
        {
            tracks.GetType().GetMethod("ReplaceAll")!.Invoke(tracks, [List(type, owned)]);
            _vm.GetType().GetMethod("BuildDiscGroups", Private)!.Invoke(_vm, null);
        }
    }
    private void Render()
    {
        _rows.Children.Clear(); _rows.IsEnabled = true;
        _selected.RemoveWhere(id => !_matches.TryGetValue(id, out var m) || m.Local is not null || m.Download is not null); ShowSelection();
        _status.Text = $"{_matches.Values.Count(m => m.Local is not null)} of {_metadata.Songs.Count} in your library · {_matches.Values.Count(m => m.Local is null && m.Download is not null)} queued/saved · {_matches.Values.Count(m => m.Local is null && m.Download is null)} ready to download";
        if (_metadata.Notice.Length > 0) _status.Text += " · " + _metadata.Notice;
        var local = LocalTracks(_main).ToDictionary(t => (Guid)Get(t, "Id")!);
        int disc = -1;
        foreach (var song in _metadata.Songs)
        {
            if (!_matches.TryGetValue(song.Id, out var match)) continue;
            if (_metadata.Songs.Any(s => s.Disc > 1) && disc != song.Disc) { disc = song.Disc; _rows.Children.Add(new TextBlock { Text = "Disc " + disc, FontSize = 17, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 18, 0, 6) }); }
            var owned = match.Local;
            var display = owned is not null && local.TryGetValue(owned.Id, out var actual) ? actual : DisplayTrack(song);
            var native = _ownedTemplate?.Build(display) ?? throw Unsupported("Noctis track row template"); native.DataContext = display;
            var grid = native.GetLogicalDescendants().OfType<Grid>().FirstOrDefault(g => ReferenceEquals(g.Tag, display));
            // The template's Tag binding can activate only after attachment; its first grid is the same contract.
            grid ??= native.GetLogicalDescendants().OfType<Grid>().FirstOrDefault(g => g.ColumnDefinitions.Count == 3 && g.Children.Count >= 3);
            if (grid is null) throw Unsupported("native track row layout");
            var number = grid.Children.OfType<Panel>().FirstOrDefault(c => Grid.GetColumn(c) == 0);
            var title = grid.Children.OfType<StackPanel>().FirstOrDefault(c => Grid.GetColumn(c) == 1);
            var options = grid.Children.OfType<Grid>().FirstOrDefault(c => Grid.GetColumn(c) == 2);
            if (number?.Children.OfType<TextBlock>().FirstOrDefault() is {} index) index.Text = (song.Number > 0 ? song.Number : _metadata.Songs.IndexOf(song) + 1).ToString();
            if (owned is null)
            {
                if (title is not null) title.Opacity = .65;
                grid.Tag = null; // display metadata is neither a draggable file nor a native editable track
                if (options is null) throw Unsupported("native track actions");
                var duration = options.Children.OfType<TextBlock>().FirstOrDefault();
                foreach (var control in options.Children) control.IsVisible = ReferenceEquals(control, duration);
                options.ColumnDefinitions = new ColumnDefinitions("Auto,Auto,52");
                if (duration is not null) Grid.SetColumn(duration, 2);
                var now = Button(match.Download?.State == DownloadState.Failed ? "Retry download" : match.Download?.State == DownloadState.Running ? "Downloading…" : "Download now", () => Queue([song], true));
                now.IsEnabled = match.Download is null || match.Download.IsActive && match.Download.State != DownloadState.Running || match.Download.State == DownloadState.Failed;
                var queue = Button(match.Download is null ? "Queue download" : "Download queued / saved", () => Queue([song], false)); queue.IsEnabled = match.Download is null;
                options.Children.Add(now); Grid.SetColumn(queue, 1); options.Children.Add(queue); ToolTip.SetTip(native, match.Detail);
            }
            native.DoubleTapped += (_, e) =>
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; return; }
                if (e.Source is Control c && c.GetVisualAncestors().OfType<Button>().Any()) return; e.Handled = true;
                if (owned is null) { Queue([song], true); return; }
                try { Play(_main, _vm, owned); } catch (Exception ex) { _status.Text = ex.Message; }
            };
            // Real owned tracks keep native selection/edit/context behavior. Only missing
            // containers use CatalogSong, so the host cannot mistake them for local files.
            var item = new ListBoxItem { Content = native, DataContext = owned is not null ? display : song };
            item.Classes.Set("ctrl-selected", _selected.Contains(song.Id));
            if (owned is not null) item.ContextRequested += (_, e) =>
            {
                if (e.Handled) return;
                var page = this.GetVisualAncestors().OfType<UserControl>().FirstOrDefault(p => ReferenceEquals(p.DataContext, _vm));
                page?.GetType().GetMethod("OnTrackItemContextRequested", Private)?.Invoke(page, [item, e]);
            };
            item.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
                if (e.Source is Control source && (source is Button || source.GetVisualAncestors().TakeWhile(a => !ReferenceEquals(a, item)).OfType<Button>().Any())) return;
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) { _selected.Clear(); foreach (var other in _rows.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.DataContext is CatalogSong)) other.Classes.Remove("ctrl-selected"); ShowSelection(); return; }
                if (owned is not null) return;
                e.Handled = true;
                if (match.Download is not null) return;
                if (!_selected.Add(song.Id)) _selected.Remove(song.Id); item.Classes.Set("ctrl-selected", _selected.Contains(song.Id)); ShowSelection();
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            var list = new ListBox { ItemsSource = new[] { item }, Background = Brushes.Transparent, ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel()) };
            ScrollViewer.SetVerticalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            list.Classes.Add("track-list"); _rows.Children.Add(list);
        }
        if (_extraTracks.Count > 0)
        {
            _rows.Children.Add(new TextBlock { Text = "Other downloaded tracks in this album", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 18, 0, 8) });
            foreach (var track in _extraTracks)
            {
                var row = _ownedTemplate!.Build(track)!; row.DataContext = track;
                var song = new LocalSong((Guid)Get(track, "Id")!, Get(track, "Artist") as string ?? "", Get(track, "Title") as string ?? "", (string)Get(track, "FilePath")!, (TimeSpan)Get(track, "Duration")!);
                row.DoubleTapped += (_, e) => { e.Handled = true; try { Play(_main, _vm, song); } catch (Exception ex) { _status.Text = ex.Message; } }; _rows.Children.Add(row);
            }
        }
    }
    private object DisplayTrack(CatalogSong song)
    {
        var type = Get(_vm, "Album")!.GetType().GetProperty("Tracks")!.PropertyType.GenericTypeArguments.Single(); var track = Activator.CreateInstance(type)!;
        Set(track, "Id", Id("display:" + song.Id)); Set(track, "Title", song.Title); Set(track, "Artist", string.Join(", ", song.Artists.Select(a => a.Name)));
        Set(track, "AlbumArtist", _metadata.Release.Artist.Name); Set(track, "Album", _metadata.Release.Title); Set(track, "AlbumId", Get(Get(_vm, "Album")!, "Id"));
        Set(track, "Duration", TimeSpan.FromSeconds(song.Seconds ?? 0)); Set(track, "TrackNumber", song.Number); Set(track, "DiscNumber", song.Disc); Set(track, "IsExplicit", song.Explicit == true);
        return track;
    }
    private void ConfirmAll()
    {
        var missing = _metadata.Songs.Where(s => _matches.TryGetValue(s.Id, out var m) && m.Local is null && m.Download is null).ToArray();
        _confirmation.Children.Clear();
        if (missing.Length == 0) { _status.Text = "No missing songs to download. Existing files and queue were kept."; return; }
        _confirmation.Children.Add(new TextBlock { Text = $"Download {missing.Length} missing songs?", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _confirmation.Children.Add(Button("Download " + missing.Length + " songs", () => { _confirmation.Children.Clear(); Queue(missing, false); }));
        _confirmation.Children.Add(Button("Cancel", () => _confirmation.Children.Clear()));
    }
    private async void Queue(CatalogSong[] songs, bool priority)
    {
        if (_disposed || _writing) return; if (songs.Length == 0) { _status.Text = "Select one or more missing songs first."; return; } _writing = true;
        try { var result = await _actions.QueueAsync(songs, priority, CancellationToken.None); if (!_disposed) _status.Text = $"{result.Added} queued · {result.Existing} already queued/saved · {result.Owned} already owned. Download progress is in Free Music Finder → Queue."; }
        catch (Exception ex) { if (!_disposed) _status.Text = ex.Message; }
        finally { _writing = false; if (!_disposed) { _signature = ""; await RefreshAsync(); } }
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 2), Padding = new Thickness(12, 5), CornerRadius = new CornerRadius(999), FontWeight = FontWeight.Bold, BorderThickness = new Thickness(1) };
        button.Classes.Add("accent-btn"); button.Classes.Add("album-header-action");
        button.Click += (_, e) => { e.Handled = true; action(); }; return button;
    }
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _headerPanel?.Children.Remove(_headerAction);
        if (_headerWrap is {} wrap && _headerParent is {} parent && _nativeHeader is {} native)
        {
            var at = parent.Children.IndexOf(wrap); wrap.Children.Remove(native); native.Margin = _nativeHeaderMargin;
            if (at >= 0) { parent.Children.Remove(wrap); parent.Children.Insert(at, native); }
        }
        _headerPanel = null; _headerParent = null; _headerWrap = null; _nativeHeader = null;
    }
}
