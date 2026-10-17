using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.Plugins;

namespace FreeMusicFinder;

/// <summary>
/// Searches a Telegram music bot through the user's own Telegram account and downloads tracks
/// into a folder.
/// API 1.1 has no hook for a page or sidebar entry, so the plugin opens its own window: with a
/// keyboard shortcut anywhere in Noctis, from the track menu, or from the "Open the search
/// window" switch in its settings. The downloads belong to the plugin: they go on when the
/// window is closed and stop with the plugin.
/// </summary>
public sealed class FreeMusicPlugin : INoctisPlugin
{
    private const string OpenSearchKey = "openSearch";
    private const string ProxyKey = "proxy";
    private const string ShortcutKey = "shortcut";
    private const string AllowMp3Key = "allowMp3";
    private const string LastFmKey = "lastfmApiKey";
    internal const string DownloadFolderKey = "downloadFolder";

    private readonly List<IDisposable> _registrations = new();
    private IPluginHost? _host;
    private TelegramAccount? _telegram;
    private MusicSources? _bot;
    private PlaylistTransfers? _transfers;
    private SpotifyPlaylists? _playlists;
    private Downloads? _downloads;
    private SearchWindow? _window;
    private ArtistCatalog? _catalog;
    private DiscoveryActions? _discoveryActions;
    private NativeCatalogPages? _artistPages;
    private KeyGesture? _shortcut;
    // Read on the threads that connect to Telegram; settings themselves are only read on the UI thread.
    private volatile string _proxy = "";
    private volatile bool _allowMp3 = true;
    private readonly Func<string, string, ArtistCatalog> _catalogFactory;
    private ForYouSource? _forYou;
    private FinderOnlineSearch? _online;
    private SimilarArtists? _similar;
    private LastFmSimilar? _lastFm;
    private volatile string _lastFmKey = "";

    internal const string FeatureVersion = "2.11.1";

    public FreeMusicPlugin() : this((folder, proxy) => new ArtistCatalog(folder, PlaylistCatalog.MetadataHandler(proxy))) { }
    internal FreeMusicPlugin(Func<string, string, ArtistCatalog> catalogFactory) => _catalogFactory = catalogFactory;

    public PluginInfo Info { get; } = new(
        Id: "dev.moshi.freemusicfinder",
        Name: "Free Music Finder",
        // Built into Noctis Plus now, so the assembly carries the app's version; Finder's own
        // version is the one in its plugin.json (FeatureVersion, checked by the tests).
        Version: FeatureVersion,
        Author: "moshi",
        Description: "Find songs and import Spotify/Deezer playlists with Music Hunters and DeezLoad, with saved queues and automatic fallback.");

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _proxy = host.Settings.GetString(ProxyKey) ?? "";
        _allowMp3 = host.Settings.GetBool(AllowMp3Key, true);
        _lastFmKey = host.Settings.GetString(LastFmKey)?.Trim() ?? "";
        _telegram = new TelegramAccount(Path.Combine(host.DataDirectory, "telegram.dat"), () => _proxy, Log);
        _bot = new MusicSources(new HunterMusicSource(_telegram,Log),new DeezLoadSource(_telegram,Announce),Announce,
            Path.Combine(host.DataDirectory,"source-limits.json"),allowMp3:()=>_allowMp3);
        _downloads = new Downloads(Log,Path.Combine(host.DataDirectory,"download-queue.json"),_bot.Track);
        _transfers = new PlaylistTransfers(_telegram,_bot,_downloads,Path.Combine(host.DataDirectory,"playlist-queue.json"),Owns,
            new PlaylistCatalog(PlaylistCatalog.MetadataHandler(_proxy,Log)));
        _playlists = new SpotifyPlaylists(Path.Combine(host.DataDirectory, "spotify-playlists.json"), new PlaylistLibrary(), _downloads, _bot.Track,
            new PlaylistCatalog(PlaylistCatalog.MetadataHandler(_proxy, Log)));
        _catalog = CreateCatalog();
        _similar = CreateSimilar();
        _lastFm = CreateLastFm();
        if (_lastFmKey.Length > 0 && !LastFmSimilar.ValidKey(_lastFmKey)) Announce("Last.fm API key not understood: paste the 32-character API key, not the secret. For you will use the other catalogs.");
        _discoveryActions = new DiscoveryActions(new PlaylistLibrary(), _downloads, _bot.Track,
            () => Downloader.Folder(host.Settings.GetString(DownloadFolderKey), host.DataDirectory), () => _telegram?.IsLoggedIn == true);
        _artistPages = new NativeCatalogPages(() => _catalog!, _discoveryActions, Log, () => _host?.Settings.GetBool("artistCatalogOnPages", true) == true, choicesPath: Path.Combine(host.DataDirectory, "artist-catalog", "artist-choices.json"));
        _forYou = new ForYouSource(() => _catalog ?? throw new InvalidOperationException("Free Music Finder has stopped."), _discoveryActions,
            DiscoverAlbums, new PlaylistLibrary(), NoctisDataOf(host.DataDirectory), Path.Combine(host.DataDirectory, "for-you-hidden.json"), () => _similar, () => _lastFm);
        _forYou.WarmUp();
        // The app's own Search page reads the catalog and starts downloads through this.
        _online = new FinderOnlineSearch(() => _catalog, _discoveryActions, _artistPages, () => _telegram?.IsLoggedIn == true, () => ShowWindow(null));
        Noctis.Services.Search.OnlineMusicSearch.Current = _online;
        _transfers.Changed += Announce;
        _downloads.Finished += Announce;

        _registrations.Add(host.RegisterTrackCommand(
            "Find more by this artist…",
            // A 24×24 SVG path: a magnifying glass.
            "M15.5 14h-.79l-.28-.27A6.47 6.47 0 0 0 16 9.5 6.5 6.5 0 1 0 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19zm-6 0A4.5 4.5 0 1 1 14 9.5 4.5 4.5 0 0 1 9.5 14z",
            // Built into Noctis Plus: the app's own Search page, searched for the artist.
            (Action<TrackInfo>)(track => { if (!Noctis.Services.Search.OnlineMusicSearch.RequestOpen(track.Artist)) ShowWindow(track.Artist, discover: true); })));

        host.Settings.Changed += OnSettingChanged;

        // The plugin kit has no shortcuts, but the plugin runs inside Noctis: a class handler sees
        // the keys pressed in any of its windows. It only takes a key press nobody has handled,
        // so a shortcut Noctis itself uses keeps working.
        ApplyShortcut();
        _registrations.Add(InputElement.KeyDownEvent.AddClassHandler<Window>(OnKeyDown));

        // What a download left behind when Noctis was last closed or crashed in the middle of it.
        var configured = host.Settings.GetString(DownloadFolderKey);
        var data = host.DataDirectory;
        Task.Run(() =>
        {
            var folder = Downloader.Folder(configured, data);
            var removed = Downloader.CleanLeftovers(folder);
            if (removed > 0) Log($"removed {removed} unfinished download{(removed == 1 ? "" : "s")} left in {folder}");
        });
    }

    public void Shutdown()
    {
        if (_host is not null) _host.Settings.Changed -= OnSettingChanged;
        if (ReferenceEquals(Noctis.Services.Search.OnlineMusicSearch.Current, _online)) Noctis.Services.Search.OnlineMusicSearch.Current = null;
        _online?.Detach(); _online = null;
        foreach (var r in _registrations) r.Dispose();
        _registrations.Clear();

        _window?.Close();
        _window = null;
        _forYou?.Dispose(); _forYou = null;
        _artistPages?.Dispose(); _artistPages = null;
        _catalog?.Dispose(); _catalog = null;
        _discoveryActions = null;
        _similar?.Dispose(); _similar = null;
        _lastFm?.Dispose(); _lastFm = null;
        _playlists?.Dispose(); _playlists = null;
        if (_downloads is not null) _downloads.Finished -= Announce;
        _downloads?.Dispose();
        _downloads = null;
        if(_transfers is not null) _transfers.Changed -= Announce;
        _transfers?.Dispose(); _transfers=null;
        _bot = null;
        _telegram?.Dispose();
        _telegram = null;
        _host = null;
    }

    private void OnSettingChanged(object? sender, string key)
    {
        if (key == OpenSearchKey) ShowWindow(null);
        else if (key == ProxyKey)
        {
            _proxy = _host?.Settings.GetString(ProxyKey) ?? "";
            // Said now; a search or a playlist import with this setting says it again.
            try { ProxyChoice.Parse(_proxy); }
            catch (FormatException ex) { Announce("Free Music Finder: " + ex.Message); }
            try{_transfers?.UpdateProxy(_proxy,Log);}catch(Exception ex){Announce("Metadata proxy: "+ex.Message);}
            UpdatePlaylistProxy();
            _catalog?.Dispose();
            _catalog = CreateCatalog();
            _similar?.Dispose(); _similar = CreateSimilar();
            _lastFm?.Dispose(); _lastFm = CreateLastFm();
        }
        else if (key == LastFmKey)
        {
            _lastFmKey = _host?.Settings.GetString(LastFmKey)?.Trim() ?? "";
            _lastFm?.Dispose(); _lastFm = CreateLastFm();
            if (_lastFmKey.Length > 0 && !LastFmSimilar.ValidKey(_lastFmKey)) Announce("Last.fm API key not understood: paste the 32-character API key, not the secret. For you will use the other catalogs.");
            _forYou?.Forget();
        }
        else if (key == ShortcutKey) ApplyShortcut();
        else if (key == AllowMp3Key) _allowMp3 = _host?.Settings.GetBool(AllowMp3Key, true) ?? true;
    }

    private void ApplyShortcut()
    {
        if (_host is null) return;
        try
        {
            _shortcut = Shortcut.Parse(_host.Settings.GetString(ShortcutKey), OperatingSystem.IsMacOS());
        }
        catch (FormatException ex)
        {
            _shortcut = null;
            _host.Log("shortcut: " + ex.Message);
            Announce("Free Music Finder: " + ex.Message);
        }
    }

    private void OnKeyDown(Window window, KeyEventArgs e)
    {
        if (e.Handled || _shortcut is not { } shortcut || !shortcut.Matches(e)) return;
        e.Handled = true;
        // Built into Noctis Plus: the shortcut opens the app's Search page.
        if (!Noctis.Services.Search.OnlineMusicSearch.RequestOpen(null)) ShowWindow(null);
    }

    private ArtistCatalog CreateCatalog() => _catalogFactory(Path.Combine(_host!.DataDirectory, "artist-catalog"), _proxy);
    private SimilarArtists CreateSimilar() => new(Path.Combine(_host!.DataDirectory, "artist-catalog"), PlaylistCatalog.MetadataHandler(_proxy, Log));
    private LastFmSimilar CreateLastFm() => new(Path.Combine(_host!.DataDirectory, "artist-catalog"), PlaylistCatalog.MetadataHandler(_proxy, Log), () => _lastFmKey);
    private IReadOnlyList<string> DiscoverAlbums(string name) => Dispatcher.UIThread.CheckAccess()
        ? _artistPages?.LocalAlbums(name) ?? []
        : Dispatcher.UIThread.InvokeAsync(() => _artistPages?.LocalAlbums(name) ?? []).GetAwaiter().GetResult();
    private DiscoveryView CreateDiscovery() => new(() => _catalog ?? throw new InvalidOperationException("Free Music Finder has stopped."),
        artist => { _artistPages!.OpenArtist(artist); _window?.Close(); }, name => _artistPages?.LocalAlbums(name) ?? [],
        _discoveryActions, async (album, ct) => { await _artistPages!.OpenAlbumAsync(album, ct); _window?.Close(); }, _forYou);

    /// <summary>Noctis's own data folder (two above this plugin's), when the plugin's data sits where Noctis puts it.</summary>
    private static string? NoctisDataOf(string pluginData)
    {
        var mine = Path.GetFullPath(pluginData).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(mine);
        return parent is not null && string.Equals(Path.GetFileName(parent), "plugin-data", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(parent) : null;
    }

    private void ShowWindow(string? query, bool discover = false)
    {
        if (_host is null || _telegram is null || _bot is null || _downloads is null) return;
        try
        {
            if (_window is null)
            {
                _window = new SearchWindow(_host, _telegram, _downloads, _bot.SearchAsync,_transfers,_playlists, CreateDiscovery());
                _window.Closed += (_, _) => _window = null;

                var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (owner is not null) _window.Show(owner);
                else _window.Show();
            }
            else
            {
                if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
                _window.Activate();
            }

            if (!string.IsNullOrWhiteSpace(query)) { if (discover) _window.BrowseArtist(query); else _window.Search(query); }
            else _window.FocusSearchBox();
        }
        catch (Exception ex)
        {
            // An exception out of a callback marks the plugin Failed; a window that will not open is not worth that.
            _host.Log("could not open the search window: " + ex.Message);
        }
    }

    private bool Owns(BotTrack track)
    {
        try
        {
            return Dispatcher.UIThread.InvokeAsync(()=>_host?.Library.Search(BotText.Plain(track.Title),100)
                .Any(t=>File.Exists(t.FilePath) && RecordingMatch.Fits(new MusicRequest(track.Title,track.Artist,track.Duration?.TotalSeconds),t.Artist,t.Title,t.Duration))??false).GetAwaiter().GetResult();
        }
        catch(Exception ex) {Log("Could not check existing library: "+ex.Message);return false;}
    }
    private async void UpdatePlaylistProxy()
    {
        try { if (_playlists is {} playlists) await playlists.UpdateProxyAsync(_proxy, Log); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Announce("Playlist metadata proxy: " + ex.Message); }
    }

    /// <summary>Tells the user what was saved. Called from the download thread.</summary>
    private void Announce(string message)
        => Dispatcher.UIThread.Post(() =>
        {
            // Noctis shows at most 200 characters, and drops notices sent faster than one a second; that is fine here.
            try { _host?.Notify(message.Length <= 200 ? message : message[..199] + "…"); }
            catch (Exception ex) { _host?.Log("notify failed: " + ex.Message); }
        });

    /// <summary>Writes to the Noctis log from any thread.</summary>
    private void Log(string message)
        => Dispatcher.UIThread.Post(() =>
        {
            try { _host?.Log(message); }
            catch (Exception) { }
        });
}
