using System.Diagnostics;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Plugins;

namespace FreeMusicFinder;

/// <summary>
/// The search window. Built in code (no XAML, no bindings) so it depends on nothing but the
/// Avalonia controls Noctis already ships. Every event handler catches its own exceptions:
/// one escaping into Noctis would mark the plugin Failed.
/// The window only shows the downloads; they are run by <see cref="Downloads"/> and go on
/// when it is closed.
/// </summary>
internal sealed class SearchWindow : Window
{
    private readonly IPluginHost _host;
    private readonly TelegramAccount _telegram;
    private readonly Downloads _downloads;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<BotTrack>>> _searchBot;
    private readonly CancellationTokenSource _closed = new();
    private readonly List<Row> _rows = new();
    private CancellationTokenSource? _search;
    private string? _defaultFolder;
    private bool _noLibrary;

    private readonly TextBox _query;
    private readonly Button _searchButton;
    private readonly Button _downloadAllButton;
    private readonly Button _telegramButton;
    private readonly StackPanel _results;
    private readonly TextBlock _status;
    private readonly PlaylistTransfers? _transfers;
    private readonly TextBlock _queueStatus;
    private readonly ProgressBar _queueProgress;
    private readonly Button _pauseButton;
    private readonly DispatcherTimer _queueTimer;
    private readonly TabControl _tabs;
    private readonly TabItem _queueTab;
    private readonly QueueView _queueView;
    private readonly PlaylistView? _playlistView;
    private readonly Border _queueCard;
    private readonly DiscoveryView? _discovery;

    /// <param name="searchBot">Asks the bot for the tracks that match a text.</param>
    public SearchWindow(IPluginHost host, TelegramAccount telegram, Downloads downloads,
        Func<string, CancellationToken, Task<IReadOnlyList<BotTrack>>> searchBot,PlaylistTransfers? transfers=null,SpotifyPlaylists? playlists=null, DiscoveryView? discovery=null)
    {
        _host = host;
        _telegram = telegram;
        _downloads = downloads;
        _searchBot = searchBot;
        _transfers=transfers;
        _discovery = discovery;

        Title = "Free Music Finder";
        FinderTheme.Apply(this);
        Width = 1020;
        Height = 820;
        MinWidth = 480;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _query = new TextBox { PlaceholderText = "Search artists, songs or albums, or paste a Spotify/Deezer link" };
        _query.Classes.Add("finder-search");
        _query.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            StartSearch(_discovery?.BotMode == true);
        };

        _searchButton = new Button { Content = "Search", Margin = new Thickness(8, 0, 0, 0), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        _searchButton.Click += (_, _) => StartSearch(_discovery?.BotMode == true);
        FinderTheme.Primary(_searchButton);
        _searchButton.CornerRadius = new CornerRadius(24); _searchButton.Padding = new Thickness(22, 10);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(_query);
        Grid.SetColumn(_searchButton, 1);
        top.Children.Add(_searchButton);

        _results = new StackPanel { Spacing = 8 };
        var scroller = discovery is null ? new ScrollViewer { Content = _results, Margin = new Thickness(0, 12, 0, 12) } : null;

        _status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        };
        // The line is cut off when it does not fit; pointing at it shows all of it.
        _status.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) ToolTip.SetTip(_status, string.IsNullOrEmpty(_status.Text) ? null : _status.Text);
        };
        _telegramButton = new Button { Margin = new Thickness(8, 0, 0, 0) };
        _telegramButton.Click += (_, _) => OpenTelegramLogin();
        _downloadAllButton = new Button { Content = "Queue all", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        _downloadAllButton.Click += (_, _) => DownloadAll();
        var openFolder = new Button { Content = "Open folder", Margin = new Thickness(8, 0, 0, 0) };
        _telegramButton.Classes.Add("finder-quiet"); openFolder.Classes.Add("finder-quiet");
        openFolder.Click += (_, _) => OpenFolder();

        var bottom = new StackPanel { Spacing = 8 };
        bottom.Children.Add(_status);
        var footerButtons = new WrapPanel();
        _telegramButton.Margin = _downloadAllButton.Margin = openFolder.Margin = new Thickness(0, 0, 8, 4);
        footerButtons.Children.Add(_telegramButton); footerButtons.Children.Add(_downloadAllButton); footerButtons.Children.Add(openFolder);
        bottom.Children.Add(footerButtons);
        if (discovery is not null)
        {
            _downloadAllButton.IsVisible = false;
            discovery.BotSearchRequested += () => StartSearch(true);
            discovery.CatalogSelected += () => { _search?.Cancel(); _searchButton.IsEnabled = true; _downloadAllButton.IsVisible = false; _status.IsVisible = false; };
        }

        _queueStatus=new TextBlock {TextWrapping=TextWrapping.Wrap,Opacity=0.8,Margin=new Thickness(0,8,0,4)};
        _queueProgress=new ProgressBar {Minimum=0,Maximum=100,Height=5,Margin=new Thickness(0,0,0,8)};
        _pauseButton=new Button();
        _pauseButton.Click+=(_,_)=>{try {ToggleQueue();ShowQueue();}catch(Exception ex){_status.Text="Could not save queue: "+ex.Message;}};
        var retry=new Button {Content="Retry failed",Margin=new Thickness(8,0,0,0)};
        retry.Click+=(_,_)=>{try {_downloads.RetryFailed();_transfers?.RetryIncomplete();ShowQueue();}catch(Exception ex){_status.Text="Could not retry: "+ex.Message;}};
        var manage=new Button {Content="Manage queue",Margin=new Thickness(8,0,0,0)};
        manage.Click+=(_,_)=>_tabs!.SelectedIndex=1;
        var queueButtons=new StackPanel {Orientation=Orientation.Horizontal};queueButtons.Children.Add(_pauseButton);queueButtons.Children.Add(retry);queueButtons.Children.Add(manage);
        var queuePanel=new StackPanel { Spacing = 4 };queuePanel.Children.Add(_queueStatus);queuePanel.Children.Add(_queueProgress);queuePanel.Children.Add(queueButtons);
        _queueView=new QueueView(_downloads,()=>
        {
            // Stop the producer first, including any metadata request already in flight.
            _transfers?.Clear();_downloads.Clear();
            _status.Text="Queue cleared. Downloaded files were kept.";ShowQueue();
        },text=>_status.Text=text);
        _queueTab=new TabItem {Header="Queue",Content=_queueView};
        var tabs = new List<TabItem> { new() { Header = "Search", Content = (Control?)discovery ?? scroller }, _queueTab };
        if (playlists is not null)
        {
            _playlistView = new PlaylistView(playlists, downloads, () => DownloadFolder, () => _telegram.IsLoggedIn);
            tabs.Add(new TabItem { Header = "Playlists", Content = _playlistView });
        }
        _tabs=new TabControl {ItemsSource=tabs};
        top.Margin = new Thickness(0, 0, 0, 12);
        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(new TextBlock { Text = "Free Music Finder", FontSize = 21, FontWeight = FontWeight.SemiBold });
        header.Children.Add(top); top.Margin = new Thickness(0, 8, 0, 0);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(26, 20), RowSpacing = 12 };
        root.Children.Add(header);
        _queueCard = FinderTheme.Card(queuePanel, 12);
        Grid.SetRow(_queueCard,1);root.Children.Add(_queueCard);
        _tabs.SelectionChanged += (_, _) =>
        {
            top.IsVisible = _tabs.SelectedIndex == 0;
            _downloadAllButton.IsVisible = _tabs.SelectedIndex == 0 && (_discovery is null || _discovery.BotMode);
            _status.IsVisible = _tabs.SelectedItem is not TabItem { Content: PlaylistView } && (_discovery is null || _tabs.SelectedIndex != 0 || _discovery.BotMode);
            ShowQueue();
        };
        Grid.SetRow(_tabs, 2);
        root.Children.Add(_tabs);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);
        Content = root;
        var welcome = new StackPanel { Spacing = 8, Margin = new Thickness(6, 12) };
        welcome.Children.Add(new TextBlock { Text = "What do you want to listen to?", FontSize = 18, FontWeight = FontWeight.SemiBold });
        welcome.Children.Add(FinderTheme.Caption("Search an artist or song above. Choose Download now for one song, or build a batch with Queue download."));
        _results.Children.Add(FinderTheme.Card(welcome));

        ShowTelegramState();
        _status.Text = _telegram.IsLoggedIn
            ? "Music Hunters for quick searches; DeezLoad for batch imports. Automatic backup is enabled."
            : LoginFirst;

        // Downloads started before the window was last closed are still going.
        if (_downloads.Active.Count > 0) _status.Text = $"{_downloads.Active.Count} pending requests saved in the Queue tab.";
        if (_discovery is not null) _status.IsVisible = false;

        _downloads.Changed += OnDownloadChanged;
        _downloads.Finished += OnDownloadsFinished;
        if(_transfers is not null) _transfers.Changed+=OnTransferChanged;
        _queueTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};_queueTimer.Tick+=(_,_)=>ShowQueue();_queueTimer.Start();ShowQueue();
        Opened += (_, _) => _query.Focus();
        Closed += (_, _) =>
        {
            _downloads.Changed -= OnDownloadChanged;
            _downloads.Finished -= OnDownloadsFinished;
            if(_transfers is not null) _transfers.Changed-=OnTransferChanged;
            _queueTimer.Stop();
            _playlistView?.Dispose();
            _discovery?.Dispose();
            _closed.Cancel();
            _search?.Cancel();
        };
    }

    private const string LoginFirst = "Log in to Telegram to search.";

    /// <summary>The Telegram button's label says who is logged in.</summary>
    private void ShowTelegramState()
        => _telegramButton.Content = _telegram.IsLoggedIn ? "Telegram: " + _telegram.SignedInAs : "Log in to Telegram…";

    private async void OpenTelegramLogin()
    {
        try
        {
            await new TelegramLoginWindow(_telegram, _host).ShowDialog(this);
            ShowTelegramState();
            if (_telegram.IsLoggedIn && _status.Text == LoginFirst) _status.Text = "";
        }
        catch (Exception ex)
        {
            _host.Log("could not open the Telegram login: " + ex.Message);
        }
    }

    private string DownloadFolder
    {
        get
        {
            var configured = _host.Settings.GetString(FreeMusicPlugin.DownloadFolderKey)?.Trim();
            return string.IsNullOrEmpty(configured)
                ? _defaultFolder ??= Downloader.DefaultFolder(_host.DataDirectory)
                : Downloader.Folder(configured, _host.DataDirectory);
        }
    }

    /// <summary>Puts the cursor into the search box, with what is in it selected, ready for a new search.</summary>
    /// <summary>Noctis Plus Search's Downloads button: this window on its download queue.</summary>
    public void ShowDownloads() { _tabs.SelectedItem = _queueTab; ShowQueue(); }

    public void FocusSearchBox()
    {
        _query.Focus();
        _query.SelectAll();
    }

    /// <summary>Fills the search box and runs the search.</summary>
    public void Search(string query)
    {
        _query.Text = query;
        StartSearch();
    }

    public void BrowseArtist(string name)
    {
        Search(name);
    }

    private async void StartSearch(bool downloadBots = false)
    {
        try
        {
            var query = _query.Text?.Trim() ?? "";
            if (query.Length == 0) return;

            _defaultFolder = null; // looked up again per search: the library folders may have changed
            _search?.Cancel();
            var search = _search = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            search.CancelAfter(TimeSpan.FromSeconds(30));
            var ct = search.Token;

            _tabs.SelectedIndex=0;
            _rows.Clear();
            _results.Children.Clear();
            _downloadAllButton.IsEnabled = false;
            _status.IsVisible = true;
            if(_transfers is not null && MusicLink.TryParse(query,out var musicLink))
            {
                if (!_telegram.IsLoggedIn) { _status.Text = "Log in to Telegram to download this link. Catalog searches do not need a login."; return; }
                var job=_transfers.Start(musicLink!.Url,DownloadFolder);
                _status.Text=job.Status;_tabs.SelectedIndex=1;ShowQueue();return;
            }
            if(Uri.TryCreate(query,UriKind.Absolute,out _)) { _status.Text="Use a full Spotify or Deezer track, album or playlist link. Other links are not opened.";return; }

            if (_discovery is not null && !downloadBots)
            {
                _searchButton.IsEnabled = true;
                _downloadAllButton.IsVisible = false;
                _status.IsVisible = false;
                _discovery.Search(query);
                return;
            }
            if (_discovery is not null) { _discovery.ShowBots(_results, query); _downloadAllButton.IsVisible = true; }
            if (!_telegram.IsLoggedIn) { _status.Text = LoginFirst; return; }

            _searchButton.IsEnabled = false;
            _status.Text = "Searching…";
            try
            {
                // Task.Run keeps the waiting on the bot off the UI thread.
                var tracks = await Task.Run(() => _searchBot(query, ct), ct);
                if (search != _search || _discovery is { BotMode: false }) return;
                foreach (var track in tracks) AddRow(track);
                var found = _rows.Count;
                var owned = _rows.Count(r => r.InLibrary);
                _status.Text = found == 0 ? "Nothing found."
                    : $"{found} track{(found == 1 ? "" : "s")} found" + (owned > 0 ? $", {owned} already in your library." : ".");
            }
            catch (Exception ex)
            {
                if (search != _search || _closed.IsCancellationRequested || _discovery is { BotMode: false }) return;
                _host.Log("search failed: " + ex.Message);
                _status.Text = ex is OperationCanceledException ? "The bot did not answer."
                    : ex is TimeoutException or BotAnswerException or FormatException or ProxyRefusedException ? ex.Message
                    : ex is SocketException or IOException ? "Telegram could not be reached. Where it is blocked, turn on a VPN or its system proxy, or set a proxy in the plugin's settings."
                    : "Search failed: " + ex.Message;
            }

            if (search != _search) return;
            _searchButton.IsEnabled = true;
            ShowDownloadAll();
            ShowTelegramState(); // a search can find out that Telegram ended the session
        }
        catch (Exception ex)
        {
            _host.Log("search failed: " + ex.Message);
            _searchButton.IsEnabled = true;
            _status.Text = "Search failed: " + ex.Message;
        }
    }

    private void AddRow(BotTrack track, Download? download = null)
    {
        var folder = download?.Folder ?? DownloadFolder;
        var button = new Button { MinWidth = 110, Margin = new Thickness(12, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var row = new Row(track, button)
        {
            Download = download ?? _downloads.Find(track.FileName, folder),
            OnDisk = Downloader.FindExisting(folder, track.FileName) is not null,
        };
        row.InLibrary = !row.OnDisk && row.Download is null && InLibrary(track);
        row.Show();
        button.Click += (_, _) => Download(row,true);
        var queueButton=new Button {Content="Queue download",Margin=new Thickness(6,0,0,0),VerticalAlignment=VerticalAlignment.Center};
        queueButton.Click+=(_,_)=>Download(row,false);
        row.QueueButton=queueButton;row.Show();

        var title = new TextBlock { Text = track.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var details = track.Artist;
        if (track.Duration is { } d) details += " · " + (d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"));
        if (row.InLibrary) details += " · in your library";
        var subtitle = new TextBlock { Text = details, Opacity = 0.7, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(subtitle);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 2) };
        grid.Children.Add(text);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        Grid.SetColumn(queueButton,2);grid.Children.Add(queueButton);

        _rows.Add(row);
        var line = new Border { Child = grid, Padding = new Thickness(8, 10), CornerRadius = new CornerRadius(8) }; line.Classes.Add("finder-song"); _results.Children.Add(line);
    }

    /// <summary>
    /// Whether the Noctis library already has this song, in any of its folders. Needs the
    /// "library.read" permission; without it (or on a Noctis that has no library search) no
    /// result is marked.
    /// </summary>
    private bool InLibrary(BotTrack track)
    {
        if (_noLibrary) return false;
        try
        {
            // Also the reading the Playlists tab uses: "Song (with X) - Remix" in one place is "Song (Remix)" in the other.
            var wanted = new MusicRequest(track.Title, track.Artist, track.Duration?.TotalSeconds);
            return _host.Library.Search(BotText.Plain(track.Title), 50)
                .Any(t => BotText.SameSong(track.Artist, track.Title, track.Duration, t.Artist, t.Title, t.Duration)
                    || RecordingMatch.Fits(wanted, t.Artist, t.Title, t.Duration > TimeSpan.Zero ? t.Duration : null));
        }
        catch (Exception ex)
        {
            _noLibrary = true;
            _host.Log("results are not checked against the library: " + ex.Message);
            return false;
        }
    }

    /// <summary>Queues every result that is neither saved nor in the library yet.</summary>
    private void DownloadAll()
    {
        try
        {
            foreach (var row in _rows.Where(r => r.CanStart && !r.InLibrary).ToList()) Download(row,false);
        }
        catch (Exception ex)
        {
            _host.Log("download all failed: " + ex.Message);
        }
    }

    private void Download(Row row,bool priority)
    {
        try
        {
            if (!row.CanStart && !(priority && row.Download is {IsActive:true,State:not DownloadState.Running})) return;
            row.Download = _downloads.Start(row.Track, row.Download?.Folder??DownloadFolder,priority);
            _status.Text=priority && row.Download.RetryAt>DateTimeOffset.UtcNow
                ?$"Priority saved. Provider is unavailable until {row.Download.RetryAt.Value.ToLocalTime():ddd HH:mm}."
                :priority?"Downloading next, after any current transfer. Provider limits still apply.":"Added to the saved queue.";
            row.Show();
            ShowQueue();
            ShowDownloadAll();
        }
        catch (Exception ex)
        {
            _host.Log("download failed: " + ex.Message);
            _status.Text="Could not queue download: "+ex.Message;
        }
    }

    private void ShowDownloadAll() => _downloadAllButton.IsEnabled = _rows.Any(r => r.CanStart && !r.InLibrary);

    // From the download thread.
    private void OnDownloadChanged(Download download)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_closed.IsCancellationRequested) return;
            try
            {
                foreach (var row in _rows.Where(r => r.Download == download)) row.Show();
                ShowQueue();
                if (download.State == DownloadState.Failed) _status.Text = $"Could not download \"{download.Track.Title}\": {download.Error}";
                if (!download.IsActive) ShowDownloadAll();
            }
            catch (Exception ex)
            {
                _host.Log("could not show a download: " + ex.Message);
            }
        });

    // From the download thread.
    private void OnDownloadsFinished(string summary)
        => Dispatcher.UIThread.Post(() =>
        {
            if (!_closed.IsCancellationRequested) _status.Text = summary;
        });

    private void OnTransferChanged(string text)=>Dispatcher.UIThread.Post(()=>{if(!_closed.IsCancellationRequested){_status.Text=text;ShowQueue();}});

    /// <summary>
    /// The one button that makes the queue go or stop. Songs queued from a playlist wait for a start of
    /// their own, so lifting the pause alone left them waiting and the button seemed to do nothing:
    /// whatever makes the queue go starts them as well.
    /// </summary>
    private void ToggleQueue()
    {
        var staged = _downloads.Active.Any(d => d.Held);
        if (!staged && !_downloads.Paused) { _downloads.SetPaused(true); return; }
        if (staged && !_telegram.IsLoggedIn) { _status.Text = "Log in to Telegram below, then start queued downloads. Your queue is kept."; return; }
        _downloads.ReleaseHeld(); _downloads.SetPaused(false);
    }
    private void ShowQueue()
    {
        // Saved history prevents duplicate downloads; it is not the current download queue.
        var active = _downloads.Active.Where(d => d.IsActive).ToArray();
        var running = active.FirstOrDefault(d => d.State == DownloadState.Running);
        var pending = active.Length;
        var failed = _downloads.History.Count(d => d.State == DownloadState.Failed);
        var waiting = active.Where(d => d.State == DownloadState.Waiting && !d.Held).ToArray();
        var held = active.Count(d => d.Held);
        var queued = active.Count(d => d.State == DownloadState.Queued && !d.Held);
        var collecting = _transfers?.Jobs.Any(j => !j.Complete) == true;
        var parts = new List<string>();
        if (running is not null)
            parts.Add(running.Progress > 0 ? $"Downloading \"{running.Track.Title}\" · {running.Progress:P0}" : $"Starting \"{running.Track.Title}\"…");
        if (queued > 0) parts.Add($"{queued} queued" + (_downloads.Paused ? " · paused" : ""));
        if (waiting.Length > 0) parts.Add($"{waiting.Length} waiting for provider");
        if (held > 0) parts.Add($"{held} ready: press Start queued downloads");
        if (failed > 0) parts.Add($"{failed} need retry");
        if (collecting) parts.Add("Collecting playlist songs…");
        _queueStatus.Text = parts.Count > 0 ? string.Join(" · ", parts) : "No downloads queued";
        if(waiting.Length>0 && waiting.Min(d=>d.RetryAt) is {} retry) _queueStatus.Text+=$" · next check {retry.ToLocalTime():ddd HH:mm}";
        _queueProgress.IsIndeterminate = running is { Progress: <= 0 } || running is null && collecting;
        _queueProgress.Value = running is not null && double.IsFinite(running.Progress) ? 100 * Math.Clamp(running.Progress, 0, 1) : 0;
        _queueProgress.IsVisible = running is not null || collecting;
        _pauseButton.Content=held>0?"Start queued downloads":_downloads.Paused?"Resume queue":"Pause queue";
        // Coloured while there is something for it to start.
        _pauseButton.Classes.Set("finder-primary",held>0 || _downloads.Paused && pending>0);
        _queueTab.Header=(_discovery is null ? "Queue" : "Downloads") + $" ({pending+failed})";
        _queueView.Refresh();
        _queueCard.IsVisible = _tabs.SelectedItem is not TabItem { Content: PlaylistView } && (pending + failed > 0 || collecting);
    }

    private void OpenFolder()
    {
        try
        {
            var folder = DownloadFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status.IsVisible = true;
            _status.Text = "Could not open the folder: " + ex.Message;
        }
    }

    /// <summary>A result line: the track and the button that doubles as its status.</summary>
    private sealed class Row
    {
        public Row(BotTrack track, Button button)
        {
            Track = track;
            Button = button;
        }

        public BotTrack Track { get; }
        public Button Button { get; }
        public Button? QueueButton {get;set;}

        /// <summary>The track's download, once one was asked for.</summary>
        public Download? Download { get; set; }

        /// <summary>The file was in the download folder already when the row was made.</summary>
        public bool OnDisk { get; init; }

        /// <summary>The Noctis library has this song; it can still be downloaded, but "Download all" leaves it out.</summary>
        public bool InLibrary { get; set; }

        public bool CanStart => !OnDisk && Download?.State is null or DownloadState.Failed or DownloadState.Cancelled or DownloadState.Removed;

        /// <summary>Puts the state of the download on the button.</summary>
        public void Show()
        {
            var state = Download?.State;
            Button.Content = OnDisk ? "Downloaded" : state switch
            {
                DownloadState.Queued => Download!.Priority?"Next in line":"Download now",
                DownloadState.Waiting => Download!.Priority?"Waiting for provider":"Download now",
                DownloadState.Running => Download!.Progress > 0 ? $"{Download.Progress:P0}" : "Starting…",
                DownloadState.Saved => "Downloaded",
                DownloadState.AlreadyThere => "Already saved",
                DownloadState.Failed => "Retry",
                _ => "Download now",
            };
            Button.IsEnabled = CanStart || Download is {State:DownloadState.Queued or DownloadState.Waiting,Priority:false};
            if(QueueButton is not null)
            {
                QueueButton.IsEnabled=CanStart;
                QueueButton.Content=Download?.IsActive==true?"Download queued":"Queue download";
            }
            ToolTip.SetTip(Button, state is DownloadState.Failed or DownloadState.Waiting ? Download!.Error : null);
        }
    }
}
