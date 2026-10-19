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
/// Search, downloads, imports and recommendations live on native Noctis pages. The shared
/// transfer queue survives navigation and stops when the feature shuts down.
/// </summary>
public sealed class FreeMusicPlugin : INoctisPlugin
{
    internal static FreeMusicPlugin? Current { get; private set; }
    internal SpotifyPlaylists? PlaylistImports => _playlists;
    internal ForYouSource? ForYou => _forYou;
    internal FinderOnlineSearch? OnlineSearch => _online;
    internal DiscoveryActions? Discovery => _discoveryActions;
    internal bool IsSignedIn => _telegram?.IsLoggedIn == true;
    internal string DownloadFolder => Downloader.Folder(_host?.Settings.GetString(DownloadFolderKey), _host!.DataDirectory);
    private const string OpenSearchKey = "openSearch";
    private const string TelegramAccountKey = "telegramAccount";
    private const string ProxyKey = "proxy";
    private const string ShortcutKey = "shortcut";
    private const string AllowMp3Key = "allowMp3";
    private const string LastFmKey = "lastfmApiKey";
    internal const string DownloadFolderKey = "downloadFolder";

    private readonly List<IDisposable> _registrations = new();
    private IPluginHost? _host;
    private TelegramAccount? _telegram;
    private MusicSources? _bot;
    private DeezLoadSource? _deezLoad;
    private CancellationTokenSource _qualityStop = new();
    private PlaylistTransfers? _transfers;
    private SpotifyPlaylists? _playlists;
    private Downloads? _downloads;
    private Noctis.Views.TelegramSignInDialog? _signInDialog;
    private ArtistCatalog? _catalog;
    private DiscoveryActions? _discoveryActions;
    private CatalogPages? _artistPages;

    /// <summary>The catalog behind the app's artist and album pages (null while stopped).</summary>
    internal CatalogPages? CatalogPages => _artistPages;
    private KeyGesture? _shortcut;
    // Read on the threads that connect to Telegram; settings themselves are only read on the UI thread.
    private volatile string _proxy = "";
    internal string MetadataProxy => _proxy;
    private volatile bool _allowMp3 = true;
    private volatile AudioQuality _preferredQuality = AudioQuality.FLAC;
    private volatile string _firstBot = BotSelections.Hunters;
    private string? _streamCache;
    private Timer? _purge;
    private DateTime _lastPurge;
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
        if (_qualityStop.IsCancellationRequested) { _qualityStop.Dispose(); _qualityStop = new(); }
        _host = host;
        _proxy = host.Settings.GetString(ProxyKey) ?? "";
        _allowMp3 = host.Settings.GetBool(AllowMp3Key, true);
        _preferredQuality = host.Settings.GetString("preferredQuality") == "MP3" ? AudioQuality.MP3 : AudioQuality.FLAC;
        _firstBot = host.Settings.GetString("firstBot") == "DeezLoad" ? "DeezLoad" : BotSelections.Hunters;
        _lastFmKey = host.Settings.GetString(LastFmKey)?.Trim() ?? "";
        _telegram = new TelegramAccount(Path.Combine(host.DataDirectory, "telegram.dat"), () => _proxy, Log);
        _deezLoad = new DeezLoadSource(_telegram,Announce, ResolveDeezLoadLinkAsync);
        _bot = new MusicSources(new HunterMusicSource(_telegram,Log, ResolveHunterLinkAsync),_deezLoad,Announce,
            Path.Combine(host.DataDirectory,"source-limits.json"),allowMp3:()=>_preferredQuality == AudioQuality.MP3 || _allowMp3,
            preferFlac: () => _preferredQuality == AudioQuality.FLAC, quality: () => _preferredQuality, firstBot: () => _firstBot);
        _downloads = new Downloads(Log,Path.Combine(host.DataDirectory,"download-queue.json"),_bot.Track);
        // Owner #87: played-only songs live here, never in the library.
        _streamCache = StreamCache.Folder(host.DataDirectory);
        _downloads.CacheFolder = _streamCache;
        Noctis.Services.LibraryExclusions.Register(_streamCache);
        _purge = new Timer(_ => PurgeStreamCache(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
        // A busy listening day must not wait six hours for the size limit: after a new song lands, at most every 5 minutes.
        _downloads.Changed += d =>
        {
            if (d.State == DownloadState.Saved && _downloads?.IsCached(d) == true && DateTime.UtcNow - _lastPurge > TimeSpan.FromMinutes(5))
            { _lastPurge = DateTime.UtcNow; Task.Run(PurgeStreamCache); }
        };
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
        _artistPages = new CatalogPages(() => _catalog!, _discoveryActions, Log, () => _host?.Settings.GetBool("artistCatalogOnPages", true) == true, choicesPath: Path.Combine(host.DataDirectory, "artist-catalog", "artist-choices.json"));
        _forYou = new ForYouSource(() => _catalog ?? throw new InvalidOperationException("Free Music Finder has stopped."), _discoveryActions,
            DiscoverAlbums, new PlaylistLibrary(), NoctisDataOf(host.DataDirectory), Path.Combine(host.DataDirectory, "for-you-hidden.json"), () => _similar, () => _lastFm);
        // Native For you loads when its shelf is visible. A startup background build
        // must not compete with the user's first Search for metadata connections.
        // The app's own Search page reads the catalog and starts downloads through this.
        _online = new FinderOnlineSearch(() => _catalog, _discoveryActions, _artistPages, () => _telegram?.IsLoggedIn == true, ShowSignIn, () => _playlists);
        Current = this;
        Noctis.Services.Search.OnlineMusicSearch.Current = _online;
        _transfers.Changed += Announce;
        _downloads.Finished += Announce;

        _registrations.Add(host.RegisterTrackCommand(
            "Find more by this artist…",
            // A 24×24 SVG path: a magnifying glass.
            "M15.5 14h-.79l-.28-.27A6.47 6.47 0 0 0 16 9.5 6.5 6.5 0 1 0 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19zm-6 0A4.5 4.5 0 1 1 14 9.5 4.5 4.5 0 0 1 9.5 14z",
            // Built into Noctis Plus: the app's own Search page, searched for the artist.
            (Action<TrackInfo>)(track => ShowSearch(track.Artist))));

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
        _qualityStop.Cancel();
        if (ReferenceEquals(Current, this)) Current = null;
        if (_host is not null) _host.Settings.Changed -= OnSettingChanged;
        _purge?.Dispose(); _purge = null;
        if (_streamCache is not null) Noctis.Services.LibraryExclusions.Unregister(_streamCache);
        if (ReferenceEquals(Noctis.Services.Search.OnlineMusicSearch.Current, _online)) Noctis.Services.Search.OnlineMusicSearch.Current = null;
        _online?.Detach(); _online = null;
        foreach (var r in _registrations) r.Dispose();
        _registrations.Clear();

        _signInDialog?.Close();
        _signInDialog = null;
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
        if (key == OpenSearchKey) ShowSearch(null);
        // Signed in or not: the same window connects, shows the account, or disconnects it.
        else if (key == TelegramAccountKey) ShowSignIn();
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
        else if (key == "firstBot") _firstBot = _host?.Settings.GetString(key) == "DeezLoad" ? "DeezLoad" : BotSelections.Hunters;
        else if (key == StreamCache.SettingKey) Task.Run(PurgeStreamCache);
        else if (key == "preferredQuality")
        {
            _preferredQuality = _host?.Settings.GetString(key) == "MP3" ? AudioQuality.MP3 : AudioQuality.FLAC;
            if (_telegram?.IsLoggedIn == true) _ = SynchronizeQualityAsync();
        }
    }

    /// <summary>Owner #87: played-only songs not played for the chosen number of days are deleted. Songs still being
    /// downloaded or being played are kept (a file in use cannot be deleted; its turn comes next time).</summary>
    private void PurgeStreamCache()
    {
        try
        {
            if (_streamCache is not { } cache || _downloads is not { } downloads) return;
            var days = StreamCache.Days(_host?.Settings.GetString(StreamCache.SettingKey));
            var busy = downloads.Active.Where(d => downloads.IsCached(d)).Select(d => d.Track.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _lastPurge = DateTime.UtcNow;
            var removed = StreamCache.Purge(cache, days, DateTime.UtcNow, file => busy.Contains(Path.GetFileNameWithoutExtension(file)),
                StreamCache.Cap(days, StreamCache.FreeSpace(cache)));
            if (removed > 0) Log($"removed {removed} played-only song file{(removed == 1 ? "" : "s")} from the stream cache");
        }
        catch (Exception ex) { Log("stream cache clean-up failed: " + ex.Message); }
    }

    // Owner #81: Music Hunters is never declared unable before it was sent the song's link. The catalog search (cached),
    // then Deezer asked afresh, then DeezLoad's own inline search (slowest, ~7 s), which found SZA "Saturn".
    private Task<string?> ResolveHunterLinkAsync(MusicRequest request, CancellationToken ct)
        => TrackLinkLookup.FirstAsync(request, [CatalogLinks, FreshDeezerLinks, InlineLinks], ct);
    // DeezLoad runs its own inline search after these.
    private Task<string?> ResolveDeezLoadLinkAsync(MusicRequest request, CancellationToken ct)
        => TrackLinkLookup.FirstAsync(request, [CatalogLinks, FreshDeezerLinks], ct);
    private async Task<IEnumerable<MusicRequest>> CatalogLinks(MusicRequest request, CancellationToken ct)
        => _catalog is { } catalog ? (await catalog.SearchSongsAsync(BotQuery.For(request), ct).ConfigureAwait(false)).Value.Select(s => s.Song.Request) : [];
    private async Task<IEnumerable<MusicRequest>> FreshDeezerLinks(MusicRequest request, CancellationToken ct)
        => _catalog is { } catalog ? (await catalog.DeezerTracksAsync(BotQuery.For(request), ct).ConfigureAwait(false)).Select(s => s.Request) : [];
    private async Task<IEnumerable<MusicRequest>> InlineLinks(MusicRequest request, CancellationToken ct)
        => _deezLoad is { } deezLoad ? await deezLoad.SearchAsync(BotQuery.For(request), ct).ConfigureAwait(false) : [];

    private async Task SynchronizeQualityAsync()
    {
        try { if (_bot is not null) await _bot.SynchronizeQualityAsync(_qualityStop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_qualityStop.IsCancellationRequested) { }
        catch (Exception ex) { Announce("Audio quality not confirmed: " + ex.Message); }
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
        ShowSearch(null);
    }

    private ArtistCatalog CreateCatalog() => _catalogFactory(Path.Combine(_host!.DataDirectory, "artist-catalog"), _proxy);
    private SimilarArtists CreateSimilar() => new(Path.Combine(_host!.DataDirectory, "artist-catalog"), PlaylistCatalog.MetadataHandler(_proxy, Log));
    private LastFmSimilar CreateLastFm() => new(Path.Combine(_host!.DataDirectory, "artist-catalog"), PlaylistCatalog.MetadataHandler(_proxy, Log), () => _lastFmKey);
    private IReadOnlyList<string> DiscoverAlbums(string name) => Dispatcher.UIThread.CheckAccess()
        ? _artistPages?.LocalAlbums(name) ?? []
        : Dispatcher.UIThread.InvokeAsync(() => _artistPages?.LocalAlbums(name) ?? []).GetAwaiter().GetResult();
    /// <summary>Noctis's own data folder (two above this plugin's), when the plugin's data sits where Noctis puts it.</summary>
    private static string? NoctisDataOf(string pluginData)
    {
        var mine = Path.GetFullPath(pluginData).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(mine);
        return parent is not null && string.Equals(Path.GetFileName(parent), "plugin-data", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(parent) : null;
    }

    private void ShowSearch(string? query)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => ShowSearch(query)); return; }
        if (_host is null) return;
        if (!Noctis.Services.Search.OnlineMusicSearch.RequestOpen(query))
            Log("Search is not ready yet. Wait for the main window to finish opening.");
    }
    private void ShowSignIn() => ShowSignIn(null);

    internal async void ShowSignIn(Window? requestedOwner)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => ShowSignIn(requestedOwner)); return; }
        if (_host is null || _telegram is null) return;
        if (_signInDialog is not null) { _signInDialog.Activate(); return; }
        var owner = requestedOwner ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;
        try
        {
            _signInDialog = new Noctis.Views.TelegramSignInDialog(_telegram, Log);
            Noctis.Helpers.DialogHelper.SizeToOwner(_signInDialog, owner);
            await _signInDialog.ShowDialog(owner);
        }
        catch (Exception ex) { Log("Could not open Telegram sign-in: " + ex.GetType().Name); }
        finally { _signInDialog = null; }
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

