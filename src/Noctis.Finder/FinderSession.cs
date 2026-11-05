using Noctis.Services;

namespace FreeMusicFinder;

/// <summary>One host-owned Finder engine. No windows, reflection or desktop globals.</summary>
internal sealed class FinderSession : IDisposable
{
    private readonly ArtistCatalog _catalog;
    private readonly MusicSources _sources;
    private readonly DiscoveryActions _actions;
    private readonly Action<string> _log;
    private string _proxy = "";
    private ForYouSource? _forYou;
    private SimilarArtists? _similar;
    public Noctis.Services.Search.IOnlineDiscovery? Discovery { get; }
    public AudioQuality Quality { get; set; } = AudioQuality.FLAC;
    public string FirstBot { get; set; } = BotSelections.Hunters;
    public TelegramAccount Telegram { get; }
    public Downloads Downloads { get; }
    public FinderOnlineSearch Online { get; }
    public CatalogPages Pages { get; }
    public SpotifyPlaylists Playlists { get; }
    public string MusicFolder { get; }
    public string DataFolder { get; }
    public event Action<string>? Notice;

    public FinderSession(string data, string music, IPlaylistLibrary library, Func<ILibraryService?> service,
        Action<string> log, string? defaultsPath = null, bool completeFilesOnly = false, string? profileData = null)
    {
        DataFolder = Path.GetFullPath(data); MusicFolder = Path.GetFullPath(music); _log = log;
        Directory.CreateDirectory(data); Directory.CreateDirectory(music);
        Telegram = new TelegramAccount(Path.Combine(data, "telegram.dat"), () => _proxy, log, defaultsPath);
        _catalog = new ArtistCatalog(Path.Combine(data, "artist-catalog"), PlaylistCatalog.MetadataHandler("", log));
        async Task<IEnumerable<MusicRequest>> CatalogLinks(MusicRequest request, CancellationToken ct)
            => (await _catalog.SearchSongsAsync(BotQuery.For(request), ct).ConfigureAwait(false)).Value.Select(s => s.Song.Request);
        async Task<IEnumerable<MusicRequest>> DeezerLinks(MusicRequest request, CancellationToken ct)
            => (await _catalog.DeezerTracksAsync(BotQuery.For(request), ct).ConfigureAwait(false)).Select(s => s.Request);
        var deezLoad = new DeezLoadSource(Telegram, log,
            (request, ct) => TrackLinkLookup.FirstAsync(request, [CatalogLinks, DeezerLinks], ct));
        async Task<IEnumerable<MusicRequest>> InlineLinks(MusicRequest request, CancellationToken ct)
            => await deezLoad.SearchAsync(BotQuery.For(request), ct).ConfigureAwait(false);
        var hunters = new HunterMusicSource(Telegram, log,
            (request, ct) => TrackLinkLookup.FirstAsync(request, [CatalogLinks, DeezerLinks, InlineLinks], ct));
        _sources = new MusicSources(hunters, deezLoad, Say,
            Path.Combine(data, "source-limits.json"), allowMp3: () => true, preferFlac: () => Quality == AudioQuality.FLAC,
            quality: () => Quality, firstBot: () => FirstBot);
        BotLibrary.Current = new BotLibrary(Path.Combine(data, "bot-songs.json"), new TelegramBotChats(Telegram));
        _sources.Chats = BotLibrary.Current;
        PartialAudio.Folder = Path.Combine(data, "stream-partial");
        Downloads = new Downloads(log, Path.Combine(data, "download-queue.json"), _sources.Track, StreamCache.Folder(data));
        Downloads.ResetConnection = () => Telegram.RetireCurrentConnectionAsync();
        Downloads.AskAhead = (download, ct) => download.Track.Fetch(new AskOnlyStream(), null, ct);
        Downloads.Finished += Say;
        _actions = new DiscoveryActions(library, Downloads, _sources.Track, () => MusicFolder, () => Telegram.IsLoggedIn);
        Pages = new CatalogPages(() => _catalog, _actions, log, () => true, service, Path.Combine(data, "artist-choices.json"));
        Playlists = new SpotifyPlaylists(Path.Combine(data, "spotify-playlists.json"), library, Downloads, _sources.Track,
            new PlaylistCatalog(PlaylistCatalog.MetadataHandler("", log)));
        Online = new FinderOnlineSearch(() => _catalog, _actions, Pages, () => Telegram.IsLoggedIn,
            () => Say("Sign in to Telegram"), () => Playlists)
            { RequireCompleteFile = completeFilesOnly, ForYouHidden = () => _forYou?.Hidden };
        if (profileData is not null)
        {
            _similar = new SimilarArtists(data, PlaylistCatalog.MetadataHandler("", log));
            _forYou = new ForYouSource(() => _catalog, _actions,
                artist => service()?.Albums.Where(a => string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Name).ToArray() ?? [], library, profileData, Path.Combine(data, "for-you-hidden.json"), () => _similar);
            Discovery = new FinderDiscovery(_forYou, Online, library);
        }
    }

    public void SetProxy(string? server, string? secret)
    {
        var value = server?.Trim() ?? "";
        if (!string.IsNullOrEmpty(secret) && value.Length > 0 && !value.Contains("://", StringComparison.Ordinal))
        {
            var address = new Uri("http://" + value);
            value = "https://t.me/proxy?server=" + Uri.EscapeDataString(address.Host.Trim('[', ']'))
                + "&port=" + (address.IsDefaultPort ? 443 : address.Port)
                + "&secret=" + Uri.EscapeDataString(secret);
        }
        _ = ProxyChoice.Parse(value); // Same validation and IPv6/protocol handling as desktop.
        _proxy = value;
    }
    private void Say(string text) { _log(text); Notice?.Invoke(text); }
    public void Dispose()
    {
        _forYou?.Dispose(); _similar?.Dispose();
        Online.Detach(); Playlists.Dispose(); Pages.Dispose(); Downloads.Dispose(); _catalog.Dispose(); Telegram.Dispose();
    }
}
