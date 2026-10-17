using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace FreeMusicFinder;

/// <summary>Unified metadata search; artists and albums open Noctis's existing pages.
/// Only explicit song actions write to the download queue.</summary>
internal sealed class DiscoveryView : UserControl, IDisposable
{
    private readonly Func<ArtistCatalog> _catalog;
    private readonly Action<CatalogArtist> _open;
    private readonly Func<string, IReadOnlyList<string>>? _localAlbums;
    private readonly Func<CatalogRelease, CancellationToken, Task>? _openAlbum;
    private readonly DiscoveryActions? _actions;
    private readonly ForYouSource? _forYouSource;
    private DiscoverResult? _forYou;
    private Dictionary<string, string> _because = new();
    private bool _forYouStarted;
    private bool _newPicksReady;
    private Button? _forYouRefresh;
    private readonly Border _undoToast = new() { IsVisible = false, Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(8) };
    private readonly TextBlock _undoText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private CancellationTokenSource? _undoStop;
    private readonly TimeSpan _undoDuration;
    private readonly System.Diagnostics.Stopwatch _undoClock = new();
    private (DiscoverSong Song, int Index, DiscoverResult Page)? _dismissed;
    private readonly TextBlock _status = FinderTheme.Caption("Search artists, songs and albums above. Browsing does not need Telegram.");
    private readonly StackPanel _results = new() { Spacing = 8 };
    // Noctis enables horizontal scrolling globally. An unconstrained song grid puts its Auto action column off-screen.
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly ProgressBar _loading = new() { IsIndeterminate = true, Height = 3, IsVisible = false };
    private readonly Dictionary<string, Button> _filters = new();
    private readonly Dictionary<string, (Border Card, ArtistPicture Picture, FinderHit Hit, TextBlock Detail)> _cards = new();
    private readonly Dictionary<string, List<(Button Now, Button Queue, TextBlock Detail, FinderHit Hit)>> _songButtons = new();
    private readonly List<(Grid Row, WrapPanel Actions)> _songLayouts = [];
    private CancellationTokenSource? _search;
    private string _query = "", _filter = "All";
    private List<CatalogArtist> _artists = [];
    private List<CatalogSearchSong> _songs = [];
    private List<CatalogRelease> _albums = [], _recent = [];
    private List<FinderHit> _hits = [];
    private Dictionary<string, DiscoveryMatch> _matches = new();
    private ArtistIdentity? _identity;
    private bool _disposed, _matching;
    public event Action? BotSearchRequested;
    public event Action? CatalogSelected;
    public bool BotMode => _filter == "Download bots";
    public string Query => _query;
    public DiscoveryView(Func<ArtistCatalog> catalog, Action<CatalogArtist> open, Func<string, IReadOnlyList<string>>? localAlbums = null,
        DiscoveryActions? actions = null, Func<CatalogRelease, CancellationToken, Task>? openAlbum = null, ForYouSource? forYou = null, TimeSpan? undoDuration = null)
    {
        _catalog = catalog; _open = open; _localAlbums = localAlbums; _actions = actions; _openAlbum = openAlbum; _forYouSource = forYou;
        _undoDuration = undoDuration ?? TimeSpan.FromSeconds(8);
        var undo = new Button { Content = "Undo", Margin = new Thickness(12, 0, 0, 0) }; undo.Classes.Add("finder-quiet"); undo.Click += (_, _) => UndoDismissal();
        var undoRow = new Grid { ColumnDefinitions = new("*,Auto") }; undoRow.Children.Add(_undoText); Grid.SetColumn(undo, 1); undoRow.Children.Add(undo);
        _undoToast.Child = undoRow; _undoToast.Bind(Border.BackgroundProperty, FinderTheme.Resource("FinderPanel"));
        var filters = new WrapPanel();
        foreach (var label in (forYou is null ? new[] { "All" } : new[] { "For you", "All" }).Concat(new[] { "Artists", "Songs", "Albums", "Download bots" }))
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 5) };
            button.Classes.Add("finder-chip");
            button.Click += (_, _) => SelectFilter(label); _filters[label] = button; filters.Children.Add(button);
        }
        ToolTip.SetTip(_filters["Download bots"], "Search Music Hunters and DeezLoad for unreleased songs or missing catalog results. Requires Telegram.");
        var header = new StackPanel { Spacing = 7 }; header.Children.Add(filters); header.Children.Add(_loading); header.Children.Add(_status); header.Children.Add(_undoToast);
        _scroll.Content = _results; _scroll.ScrollChanged += (_, _) => LoadPictures(); _scroll.SizeChanged += (_, _) => LoadPictures();
        SizeChanged += (_, _) => LayoutSongActions();
        var root = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 }; root.Children.Add(header); Grid.SetRow(_scroll, 1); root.Children.Add(_scroll); Content = root;
        var welcome = new StackPanel { Spacing = 12, Margin = new Thickness(20, 72), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 440 };
        welcome.Children.Add(new TextBlock { Text = "♫", FontSize = 48, Opacity = .4, HorizontalAlignment = HorizontalAlignment.Center });
        welcome.Children.Add(new TextBlock { Text = "What do you want to listen to?", FontSize = 26, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var hint = FinderTheme.Caption("Find artists, explore albums, or search a song and its artist. Your library and downloads stay together."); hint.TextAlignment = TextAlignment.Center; welcome.Children.Add(hint);
        _results.Children.Add(welcome);
        if (forYou is not null)
        {
            forYou.PicksReady += OnPicksReady;
            // Before any search, show what the listener does not have yet. Loading starts once the page is on screen.
            _filter = "For you";
            AttachedToVisualTree += (_, _) => { if (!_forYouStarted && _filter == "For you" && _query.Length == 0) LoadForYou(false); };
        }
        UpdateFilters();
        if (_actions is not null) _actions.Downloads.Changed += DownloadChanged;
    }
    public bool ForYouMode => _filter == "For you";
    /// <summary>The songs on screen: the search's, or For you's.</summary>
    private List<CatalogSearchSong> CurrentSongs() => ForYouMode ? _forYou?.Songs.Select(s => s.Track).ToList() ?? [] : _songs;

    private async void LoadForYou(bool refresh)
    {
        if (_forYouSource is null || _disposed) return;
        _forYouStarted = true;
        if (_forYou is not null && !refresh)
        {
            // Back from another tab, whose leaving cancelled the page's token: the buttons need a live one again.
            if (_search is null || _search.IsCancellationRequested) { _search?.Dispose(); _search = new CancellationTokenSource(); }
            var shown = _search;
            SetForYouStatus(_forYou);
            Render();
            if (_actions is null || _forYou.Songs.Count == 0) return;
            try { var matches = await _actions.MatchAsync(CurrentSongs().Select(s => s.Song), shown.Token); if (!_disposed && ReferenceEquals(shown, _search) && ForYouMode) { _matches = matches; RefreshSongs(); } }
            catch (Exception) { /* the next download action rechecks */ }
            return;
        }
        _search?.Cancel(); _search?.Dispose(); var source = _search = new CancellationTokenSource();
        if (_forYou is null) { ClearCards(); _results.Children.Clear(); _scroll.Offset = default; }
        _loading.IsVisible = true;
        _status.Text = _forYou is null ? "Learning your taste…" : "Refreshing your picks…";
        bool Current() => !_disposed && ReferenceEquals(source, _search);
        var progress = new Progress<string>(text => { if (Current() && ForYouMode) _status.Text = text; });
        try
        {
            if (!refresh && await _forYouSource.SavedAsync(source.Token) is { } saved)
            {
                if (!Current()) return;
                ShowForYou(saved);
                await MatchForYou();
                _forYouSource.WarmUp(TimeSpan.Zero);
                return;
            }
            var result = await _forYouSource.BuildAsync(refresh, progress, source.Token);
            if (!Current()) return;
            if (_forYou is not null && result.Songs.Count == 0 && result.Notice.Length > 0)
                _status.Text = "Keeping your previous picks. " + result.Notice;
            else ShowForYou(result);
            await MatchForYou();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current() && ForYouMode) _status.Text = "For you: " + ArtistCatalog.Explain(ex); }
        finally { if (Current()) _loading.IsVisible = false; }

        async Task MatchForYou()
        {
            if (!Current() || !ForYouMode) return;
            var result = _forYou;
            if (_actions is not null && result is { Songs.Count: > 0 })
            {
                _matches = await _actions.MatchAsync(CurrentSongs().Select(s => s.Song), source.Token);
                if (Current()) RefreshSongs();
            }
        }
    }

    private void ShowForYou(DiscoverResult result)
    {
        if (_forYou is not null && !result.Songs.Select(s => s.Track.Song.Id).SequenceEqual(_forYou.Songs.Select(s => s.Track.Song.Id))) ClearCards();
        _forYou = result; _newPicksReady = false;
        _because = result.Songs.GroupBy(s => s.Track.Song.Id).ToDictionary(g => g.Key, g => g.First().Because);
        SetForYouStatus(result);
        if (ForYouMode) Render();
    }

    private void SetForYouStatus(DiscoverResult result) => _status.Text = result.Notice.Length > 0 ? result.Notice
        : $"{result.Songs.Count} songs you don't have yet, picked from the artists you play. Nothing downloads until you choose.";

    private void OnPicksReady(DiscoverResult result) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || _forYou is null || result.Songs.Count == 0 || result.Songs.Select(s => s.Track.Song.Id).SequenceEqual(_forYou.Songs.Select(s => s.Track.Song.Id))) return;
        _newPicksReady = true;
        if (_forYouRefresh is not null) _forYouRefresh.Content = "New picks ready";
    });

    private void RenderForYou()
    {
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 18, 0, 5) };
        header.Children.Add(new TextBlock { Text = "Songs for you", FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var refresh = _forYouRefresh = new Button { Content = _newPicksReady ? "New picks ready" : "Refresh", FontSize = 12 }; refresh.Classes.Add("finder-quiet");
        ToolTip.SetTip(refresh, "Pick again from what you have played since, without the songs you downloaded or dismissed.");
        refresh.Click += (_, _) => { if (_newPicksReady) { _forYou = null; LoadForYou(false); } else LoadForYou(true); };
        Grid.SetColumn(refresh, 1); header.Children.Add(refresh); _results.Children.Add(header);
        if (_forYou is not { } forYou) return;
        foreach (var song in forYou.Songs)
            Add(new FinderHit("Songs", song.Track.Song.Id, song.Track.Song.Title, song.Because, song.Score, Track: song.Track), "row");
        if (forYou.Artists.Count > 0)
        {
            Heading("Artists for you");
            Shelf(forYou.Artists.Select(a => new FinderHit("Artists", a.Artist.Id, a.Artist.Name, a.Because, 0, Artist: a.Artist)));
        }
        if (_forYou?.LastFm == true || _forYouSource?.UsesLastFm == true)
        {
            var credit = new Button { Content = "Artist suggestions · Last.fm ↗", FontSize = 11, Margin = new Thickness(0, 10, 0, 0) };
            credit.Classes.Add("finder-quiet"); credit.Click += (_, _) => OpenLastFm("https://www.last.fm/"); _results.Children.Add(credit);
        }
    }

    private void OpenLastFm(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { _status.Text = "Could not open the browser. Visit Last.fm to explore this artist."; }
    }

    private void NotForMe(CatalogSearchSong track, Border card)
    {
        if (_forYouSource is null || _forYou is null) return;
        int index = _forYou.Songs.FindIndex(s => s.Track.Song.Id == track.Song.Id);
        if (index < 0) return;
        var dismissed = _forYou.Songs[index]; bool added = _forYouSource.Hidden.Hide(track.Song);
        _forYouSource.Forget();
        _newPicksReady = false; if (_forYouRefresh is not null) _forYouRefresh.Content = "Refresh";
        _forYou = _forYou with { Songs = _forYou.Songs.Where(s => !_forYouSource.Hidden.HidesSong(s.Track.Song)).ToList() };
        ExpireUndo();
        if (added)
        {
            _dismissed = (dismissed, index, _forYou); _undoText.Text = "Hidden “" + track.Song.Title + "”";
            _undoClock.Restart(); _undoToast.IsVisible = ForYouMode;
            _ = ExpireUndoAsync(_undoStop = new CancellationTokenSource());
        }
        SetForYouStatus(_forYou); Render();
    }
    private void ExpireUndo() { _undoStop?.Cancel(); _undoStop?.Dispose(); _undoStop = null; _dismissed = null; _undoToast.IsVisible = false; }
    private async Task ExpireUndoAsync(CancellationTokenSource stop)
    {
        try
        {
            await Task.Delay(_undoDuration, stop.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => { if (!_disposed && ReferenceEquals(_undoStop, stop)) ExpireUndo(); });
        }
        catch (OperationCanceledException) { }
    }
    private void UndoDismissal()
    {
        if (_dismissed is not { } dismissed || _forYouSource is null || _undoClock.Elapsed >= _undoDuration) { ExpireUndo(); return; }
        _forYouSource.Hidden.Undo(dismissed.Song.Track.Song); _forYouSource.Forget();
        if (_forYou is { } page && ReferenceEquals(page, dismissed.Page))
        {
            var songs = page.Songs.ToList(); songs.Insert(Math.Min(dismissed.Index, songs.Count), dismissed.Song);
            _forYou = page with { Songs = songs };
        }
        ExpireUndo();
        if (ForYouMode && _forYou is not null)
        {
            // A refresh that began before Undo must not replace the restored row with its older snapshot.
            _search?.Cancel(); _loading.IsVisible = false;
            SetForYouStatus(_forYou); LoadForYou(false);
        }
    }
    public void SelectFilter(string filter)
    {
        if (_disposed || !_filters.ContainsKey(filter) || _filter == filter) return;
        if (ForYouMode && filter != "For you") { _search?.Cancel(); _loading.IsVisible = false; }
        _filter = filter; UpdateFilters(); _scroll.Offset = default;
        if (BotMode) { _search?.Cancel(); _loading.IsVisible = false; if (_query.Length > 0) BotSearchRequested?.Invoke(); }
        else if (ForYouMode) { CatalogSelected?.Invoke(); LoadForYou(false); }
        else { CatalogSelected?.Invoke(); if ((_search?.IsCancellationRequested == true || _forYouStarted && _hits.Count == 0) && _query.Length > 0) Search(_query); else Render(); }
    }
    private void UpdateFilters() { foreach (var (name, button) in _filters) button.Classes.Set("finder-primary", name == _filter); _undoToast.IsVisible = ForYouMode && _dismissed is not null; }
    public void ShowBots(Control content, string? query = null)
    {
        if (query is not null) _query = query.Trim();
        _filter = "Download bots"; UpdateFilters(); _search?.Cancel(); _loading.IsVisible = false;
        _results.Children.Clear(); _results.Children.Add(content);
        _status.Text = "Download bot results";
    }
    public async void Search(string name)
    {
        name = name.Trim(); if (_disposed || name.Length == 0) return;
        _query = name; if (BotMode || ForYouMode) _filter = "All"; UpdateFilters();
        _search?.Cancel(); _search?.Dispose(); var source = _search = new CancellationTokenSource();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(source.Token); deadline.CancelAfter(TimeSpan.FromSeconds(90)); var ct = deadline.Token;
        ClearCards(); _results.Children.Clear(); _scroll.Offset = default;
        _artists = []; _songs = []; _albums = []; _recent = []; _hits = []; _matches = new(); _identity = null;
        _loading.IsVisible = true; _status.Text = "Finding artists, songs and albums…";
        var warnings = new List<string>(); var query = FinderQuery.Parse(name);
        try
        {
            // The shared catalog enforces pacing and caches. Each reader observes its own
            // failure so one unavailable section cannot erase the sections already found.
            await Task.WhenAll(
                Read("Artists", () => _catalog().SearchAsync(query.Artist.Length > 0 ? query.Artist : query.Text, false, ct), value => _artists = value),
                Read("Songs", () => _catalog().SearchSongsAsync(query.Term, ct), value => _songs = value),
                Read("Albums", () => _catalog().SearchAlbumsAsync(query.Term, ct), value => _albums = value));
            if (!Current()) return;
            if (_localAlbums?.Invoke(name) is { Count: > 0 } local && _artists.Any(a => BotText.Key(a.Name) == BotText.Key(name)))
            {
                try { _identity = await _catalog().IdentifyAsync(name, local, null, ct, _artists); }
                catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("Artist identity: " + ArtistCatalog.Explain(ex)); }
                if (!Current()) return; Render();
            }
            if (_hits.FirstOrDefault() is { Artist: {} leading } && BotText.Key(leading.Name) == BotText.Key(name))
            {
                await Task.WhenAll(ArtistSongs(), RecentReleases());
                async Task ArtistSongs()
                {
                    try
                    {
                        var artistSongs = await _catalog().ArtistSongsAsync(leading, ct); if (!Current()) return;
                        _songs = artistSongs.Value.Concat(_songs).DistinctBy(s => s.Song.Id).ToList(); Render();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("Artist songs: " + ArtistCatalog.Explain(ex)); }
                }
                async Task RecentReleases()
                {
                    try
                    {
                        var profile = await _catalog().ArtistAsync(leading, false, ct); if (!Current()) return;
                        _recent = profile.Value.Releases.Where(r => r.Artist.Id == leading.Id).Take(6).ToList();
                        if (profile.Warning.Length > 0) warnings.Add(profile.Warning);
                        Render();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("Recent releases: " + ArtistCatalog.Explain(ex)); }
                }
            }
            if (!Current()) return; Render();
            if (_actions is not null && _songs.Count > 0)
            {
                try { _matches = await _actions.MatchAsync(_songs.Select(s => s.Song), ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("Library check: " + ex.Message); }
                if (!Current()) return; RefreshSongs();
            }
            _status.Text = $"{SearchRanking.Artists(_hits).Count()} artists · {SearchRanking.DeduplicateSongs(_songs).Count()} songs · {_albums.Count} albums";
            if (_hits.Count == 0) _status.Text = "No catalog matches. Try another spelling or Download bots for unreleased songs.";
            if (warnings.Count > 0) _status.Text += " · " + string.Join(" ", warnings.Distinct());
        }
        catch (OperationCanceledException) { if (ReferenceEquals(source, _search) && !_disposed && !BotMode) _status.Text = "Search took too long. Results already found are kept; check your VPN/proxy or try Download bots."; }
        catch (Exception ex) { if (!_disposed && ReferenceEquals(source, _search) && !BotMode) _status.Text = ArtistCatalog.Explain(ex); }
        finally { if (ReferenceEquals(source, _search) && !_disposed) _loading.IsVisible = false; }
        bool Current() { if (_disposed || !ReferenceEquals(source, _search) || BotMode) return false; ct.ThrowIfCancellationRequested(); return true; }
        async Task Read<T>(string section, Func<Task<CatalogResult<List<T>>>> read, Action<List<T>> publish)
        {
            try
            {
                var data = await read(); if (data.Warning.Length > 0) warnings.Add(data.Warning);
                if (Current()) { publish(data.Value); Render(); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add(section + ": " + ArtistCatalog.Explain(ex)); }
            catch (OperationCanceledException) { }
        }
    }
    private void Render()
    {
        if (_disposed || BotMode) return;
        foreach (var item in _cards.Values) if (item.Card.Parent is Panel parent) parent.Children.Remove(item.Card);
        _results.Children.Clear();
        if (ForYouMode)
        {
            RenderForYou();
            LayoutSongActions(); RefreshSongs(); Dispatcher.UIThread.Post(LoadPictures, DispatcherPriority.Loaded);
            return;
        }
        _hits = SearchRanking.Build(FinderQuery.Parse(_query), _artists, _songs, _albums, _identity);
        if (_filter == "All" && _hits.FirstOrDefault() is {} top)
        {
            Heading("Top result"); Add(top, "hero");
            if (top.Artist is not null && _recent.Count > 0)
            {
                Heading("Recent releases", artist: top.Artist);
                Shelf(_recent.Select(release => new FinderHit("Albums", release.Id, release.Title, release.Date.Split('-')[0] + " · " + release.Kind, 0, Album: release)));
            }
        }
        var sections = _filter == "All" ? new[] { "Songs", "Artists", "Albums" } : new[] { _filter };
        foreach (var section in sections)
        {
            var sectionHits = section == "Artists" ? SearchRanking.Artists(_hits) : _hits.Where(h => h.Kind == section);
            var rows = sectionHits.Where(h => _filter != "All" || h.Id != _hits[0].Id || h.Kind != _hits[0].Kind)
                .Where(h => _filter != "All" || !_recent.Any(a => a.Id == h.Id && section == "Albums")).Take(_filter == "All" ? 6 : 100).ToList();
            if (rows.Count == 0) continue; Heading(section, _filter == "All" ? section : null);
            if (section == "Songs") foreach (var hit in rows) Add(hit, "row");
            else if (_filter == "All") Shelf(rows);
            else
            {
                var tiles = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var hit in rows) Add(hit, "tile", tiles); _results.Children.Add(tiles);
            }
        }
        if (_results.Children.Count == 0 && !_loading.IsVisible) _results.Children.Add(FinderTheme.Caption("No matches in this filter. Try All or Download bots."));
        LayoutSongActions(); RefreshSongs(); Dispatcher.UIThread.Post(LoadPictures, DispatcherPriority.Loaded);
    }
    private void Heading(string text, string? filter = null, CatalogArtist? artist = null)
    {
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 18, 0, 5) };
        heading.Children.Add(new TextBlock { Text = text, FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (filter is not null || artist is not null)
        {
            var more = new Button { Content = artist is null ? "Show all →" : "View artist →", FontSize = 12 }; more.Classes.Add("finder-quiet");
            more.Click += (_, _) => { try { if (artist is not null) _open(artist); else SelectFilter(filter!); } catch (Exception ex) { _status.Text = ex.Message; } };
            Grid.SetColumn(more, 1); heading.Children.Add(more);
        }
        _results.Children.Add(heading);
    }
    private void Shelf(IEnumerable<FinderHit> hits)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var hit in hits) Add(hit, "tile", strip);
        var shelf = new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        shelf.ScrollChanged += (_, _) => LoadPictures(); _results.Children.Add(shelf);
    }
    private void Add(FinderHit hit, string presentation, Panel? target = null)
    {
        target ??= _results;
        var key = hit.Kind + ":" + hit.Id + ":" + presentation;
        if (_cards.TryGetValue(key, out var old))
        {
            old.Detail.Text = Subtitle(hit); ToolTip.SetTip(old.Card, hit.Subtitle); target.Children.Add(old.Card); return;
        }
        bool hero = presentation == "hero", tile = presentation == "tile";
        var by = hit.Artist ?? hit.Album?.Artist ?? hit.Track!.Song.Artists[0];
        var size = tile ? 132 : hero ? 104 : 44;
        var picture = new ArtistPicture(by, size, hit.Artist is not null ? null : hit.Album?.Artwork ?? hit.Track?.Artwork ?? "");
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (hero) text.Children.Add(new TextBlock { Text = hit.Kind == "Artists" ? "ARTIST" : hit.Kind == "Albums" ? "ALBUM" : "SONG", FontSize = 10, FontWeight = FontWeight.SemiBold, Opacity = .6, Margin = new Thickness(0, 0, 0, 4) });
        var title = new TextBlock { Text = hit.Title, FontSize = hero ? 32 : tile ? 14 : 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = hero ? 2 : tile ? 2 : 1, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Children.Add(title);
        var subtitle = FinderTheme.Caption(Subtitle(hit)); subtitle.Name = "HitSubtitle"; text.Children.Add(subtitle);
        if (tile) { subtitle.MaxLines = 2; subtitle.TextTrimming = TextTrimming.CharacterEllipsis; }
        var buttons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        if (hit.Track is {} track)
        {
            var now = new Button { Content = "Download now", Margin = new Thickness(0, 3, 6, 0) };
            var queue = new Button { Content = "Queue download", Margin = new Thickness(0, 3, 0, 0) };
            if (hero) FinderTheme.Primary(now); else now.Classes.Add("finder-quiet"); queue.Classes.Add("finder-quiet");
            now.Click += async (_, _) => await Queue(track, true); queue.Click += async (_, _) => await Queue(track, false);
            buttons.Children.Add(now); buttons.Children.Add(queue);
            if (ForYouMode && _forYouSource is not null && presentation == "row")
            {
                var notForMe = new Button { Content = "Not for me", Margin = new Thickness(6, 3, 0, 0) }; notForMe.Classes.Add("finder-quiet");
                ToolTip.SetTip(notForMe, "Don't suggest this song again. After two, its artist is left out too.");
                notForMe.Click += (_, _) => { if (_cards.TryGetValue(hit.Kind + ":" + hit.Id + ":" + presentation, out var self)) NotForMe(track, self.Card); };
                buttons.Children.Add(notForMe);
            }
            if (!_songButtons.TryGetValue(hit.Id, out var set)) _songButtons[hit.Id] = set = [];
            set.Add((now, queue, subtitle, hit));
        }
        var card = new Border();
        if (tile)
        {
            var content = new StackPanel { Spacing = 9 }; content.Children.Add(picture); content.Children.Add(text);
            var open = new Button { Content = content, Width = 152, VerticalContentAlignment = VerticalAlignment.Top };
            open.Classes.Add("finder-tile"); Avalonia.Automation.AutomationProperties.SetName(open, "Open " + hit.Title + (hit.Artist is not null ? " artist in Noctis" : " album in Noctis"));
            ToolTip.SetTip(open, hit.Title + " · " + hit.Subtitle); open.Click += async (_, _) => await Open(hit, open);
            card.Child = open; card.Margin = new Thickness(0, 0, 3, 0);
        }
        else if (hero)
        {
            if (hit.Track is null)
            {
                var open = new Button { Content = hit.Artist is not null ? "Open in Noctis →" : "Open album →", Margin = new Thickness(0, 8, 0, 0) }; FinderTheme.Primary(open);
                open.Click += async (_, _) => await Open(hit, open); buttons.Children.Add(open);
            }
            text.Children.Add(buttons);
            var row = new Grid { ColumnDefinitions = new("104,*"), ColumnSpacing = 24 }; row.Children.Add(picture); Grid.SetColumn(text, 1); row.Children.Add(text);
            card.Child = row; card.Padding = new Thickness(24); card.CornerRadius = new CornerRadius(16); card.MinHeight = 170;
            card.Bind(Border.BackgroundProperty, FinderTheme.Resource("FinderHero"));
        }
        else
        {
            var row = new Grid { ColumnDefinitions = new("44,*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 12, MinHeight = 62 };
            row.Children.Add(picture); Grid.SetColumn(text, 1); row.Children.Add(text); Grid.SetColumn(buttons, 2); row.Children.Add(buttons);
            _songLayouts.Add((row, buttons)); card.Child = row; card.Padding = new Thickness(8, 5); card.CornerRadius = new CornerRadius(7); card.Classes.Add("finder-song");
        }
        ToolTip.SetTip(card, hit.Subtitle); _cards[key] = (card, picture, hit, subtitle); target.Children.Add(card);
        if (ForYouMode && (_forYou?.LastFm == true || _forYouSource?.UsesLastFm == true))
        {
            var artistName = hit.Artist?.Name ?? hit.Track?.Song.Artists.FirstOrDefault()?.Name;
            if (artistName is not null)
            {
                var link = new MenuItem { Header = "View artist on Last.fm ↗" };
                link.Click += (_, _) => OpenLastFm("https://www.last.fm/music/" + Uri.EscapeDataString(artistName));
                card.ContextMenu = new ContextMenu { ItemsSource = new[] { link } };
            }
        }
    }
    private async Task Open(FinderHit hit, Button button)
    {
        var source = _search; if (source is null || source.IsCancellationRequested) return;
        try
        {
            if (hit.Artist is {} artist) _open(artist);
            else if (_openAlbum is not null) { button.IsEnabled = false; _status.Text = "Opening " + hit.Title + "…"; await _openAlbum(hit.Album!, source.Token); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && ReferenceEquals(source, _search)) _status.Text = ArtistCatalog.Explain(ex); }
        finally { if (!_disposed) button.IsEnabled = true; }
    }
    private void LayoutSongActions()
    {
        var wide = Bounds.Width >= 700;
        foreach (var (row, buttons) in _songLayouts)
        {
            row.ColumnDefinitions = new(wide ? "44,*,Auto" : "44,*");
            Grid.SetColumn(buttons, wide ? 2 : 1); Grid.SetRow(buttons, wide ? 0 : 1);
        }
    }
    private string Subtitle(FinderHit hit)
    {
        if (hit.Artist is {} artist) return artist.Genre.Length > 0 ? artist.Genre : "Artist";
        var text = hit.Track is {} song ? string.Join(", ", song.Song.Artists.Select(a => a.Name)) + (song.Song.Seconds is {} seconds ? " · " + TimeSpan.FromSeconds(seconds).ToString(@"m\:ss") : "") : hit.Subtitle;
        var explicitFlag = hit.Track is {} track ? track.Song.Explicit : hit.Album?.Explicit;
        var reason = ForYouMode && hit.Track is not null && _because.TryGetValue(hit.Id, out var why) ? " · " + why : "";
        return text + (explicitFlag == true ? " · Explicit" : explicitFlag == false ? " · Clean version" : "") + reason;
    }
    private async Task Queue(CatalogSearchSong track, bool priority)
    {
        if (_actions is null || _search is not {} source) return;
        try
        {
            var result = await _actions.QueueAsync([track.Song], priority, source.Token);
            if (_disposed || !ReferenceEquals(source, _search)) return;
            _status.Text = result.Owned > 0 ? "Already in your library." : result.Added > 0 ? priority ? "Downloading next, after any current transfer." : "Added to the download queue." : "Already queued or saved.";
            var matches = await _actions.MatchAsync(CurrentSongs().Select(s => s.Song), source.Token); if (!_disposed && ReferenceEquals(source, _search)) { _matches = matches; RefreshSongs(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && ReferenceEquals(source, _search)) _status.Text = ex.Message; }
    }
    private void RefreshSongs()
    {
        foreach (var (id, set) in _songButtons)
        foreach (var buttons in set)
        {
            var match = _matches.GetValueOrDefault(id); var owned = match?.Local is not null; var download = match?.Download;
            buttons.Now.Content = owned ? "In your library" : download?.State is DownloadState.Saved or DownloadState.AlreadyThere ? "Saved" : download is { State: DownloadState.Running } ? "Downloading…" : "Download now";
            buttons.Now.IsEnabled = _actions is not null && !owned && download?.State is not (DownloadState.Running or DownloadState.Saved or DownloadState.AlreadyThere);
            buttons.Queue.Content = download?.IsActive == true ? "Download queued" : "Queue download";
            buttons.Queue.IsEnabled = buttons.Now.IsEnabled && download?.IsActive != true;
            buttons.Queue.IsVisible = !owned && download?.State is not (DownloadState.Saved or DownloadState.AlreadyThere or DownloadState.Running);
            if (match is not null)
            {
                var detail = download?.State switch
                {
                    DownloadState.Running => $"Downloading · {download.Progress:P0}",
                    DownloadState.Saved or DownloadState.AlreadyThere => "Saved · waiting for Noctis to scan",
                    DownloadState.Failed => "Needs retry · " + download.Error,
                    DownloadState.Waiting => download.Error,
                    DownloadState.Cancelled or DownloadState.Removed => "Download removed",
                    _ => download is null ? "" : match.Detail,
                };
                buttons.Detail.Text = Subtitle(buttons.Hit) + (detail.Length > 0 ? " · " + detail : "");
                ToolTip.SetTip(buttons.Now, match.Detail); ToolTip.SetTip(buttons.Queue, match.Detail);
            }
        }
    }
    private void DownloadChanged(Download download) => Dispatcher.UIThread.Post(async () =>
    {
        var songs = CurrentSongs();
        if (_disposed || _actions is null || songs.Count == 0 || _search is not {} source || source.IsCancellationRequested) return;
        if (_matches.Values.Any(m => ReferenceEquals(m.Download, download))) { RefreshSongs(); return; }
        if (_matching || download.Track.Request is not {} request || !songs.Any(s => SpotifyPlaylists.SameRecording(s.Song.Request, request))) return;
        _matching = true;
        try { var matches = await _actions.MatchAsync(songs.Select(s => s.Song), source.Token); if (!_disposed && ReferenceEquals(source, _search)) { _matches = matches; RefreshSongs(); } }
        catch (Exception) { /* The next explicit download rechecks and reports library failures. */ }
        finally { _matching = false; }
    });
    private void LoadPictures()
    {
        if (_disposed || _search is null || _scroll.Bounds.Height <= 0) return;
        foreach (var (_, (card, picture, hit, _)) in _cards)
            if (card.Parent is not null && picture.TranslatePoint(default, _scroll) is {} p && p.Y + picture.Height >= 0 && p.Y <= _scroll.Bounds.Height && p.X + picture.Width >= 0 && p.X <= _scroll.Bounds.Width)
                picture.Load(_catalog(), hit.Artist ?? hit.Album?.Artist ?? hit.Track!.Song.Artists[0], _search.Token);
    }
    private void ClearCards() { foreach (var (_, item) in _cards) item.Picture.Dispose(); _cards.Clear(); _songButtons.Clear(); _songLayouts.Clear(); }
    public void Dispose() { if (_disposed) return; _disposed = true; ExpireUndo(); _search?.Cancel(); _search?.Dispose(); _search = null; ClearCards(); if (_actions is not null) _actions.Downloads.Changed -= DownloadChanged; if (_forYouSource is not null) _forYouSource.PicksReady -= OnPicksReady; }
}
