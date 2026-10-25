using Microsoft.Extensions.DependencyInjection;
using Noctis.Services;

namespace FreeMusicFinder;

/// <summary>Native For you integration, isolated from downloads/search/True Shuffle. Deliberately opt-in until the
/// offline promotion gate and TEST-app check pass. No automatic install or model download. The validated model's
/// distribution/setup UI is a separate promotion step; absence keeps the current For you byte-for-byte.</summary>
internal sealed class SonicFeature : IDiscoverSonic, IDisposable
{
    internal const string SettingKey = "sonicForYou";
    internal const string ModelFile = "music-clap-int8-v1.onnx";
    internal const string ModelSpace = "laion/larger_clap_music@a0b4534:audio-int8-v1:slaney50-14k:3x10";
    internal const string ModelSha256 = "53887eb9e065cd45add21d4162e7bd10664333165467385b9b3f954ea5b28644";
    private readonly string _folder;
    private readonly Func<bool> _enabled;
    private readonly Action<string> _log;
    private SonicService _service;
    private ArtistCatalog _catalog;

    public SonicFeature(string folder, Func<bool> enabled, string proxy, Action<string> log)
    {
        _folder = folder; _enabled = enabled; _log = log;
        (_service, _catalog) = Create(proxy);
    }

    private (SonicService, ArtistCatalog) Create(string proxy)
    {
        // A separate metadata connection lane: taste resolution must not occupy Search's slots.
        var catalog = new ArtistCatalog(Path.Combine(_folder, "sonic-metadata"), PlaylistCatalog.MetadataHandler(proxy));
        var cache = new SonicCache(Path.Combine(_folder, "sonic-embeddings"), ModelSpace);
        var encoder = new SonicEncoder(Path.Combine(_folder, "sonic-models", ModelFile), ModelSpace,
            () => Noctis.App.Services?.GetService<IAudioConverterService>()?.GetFfmpegPath(), ModelSha256);
        var service = new SonicService(cache, encoder, new SonicPreviews(PlaylistCatalog.MetadataHandler(proxy)), Resolver(catalog),
            Noctis.Features.WordLyrics.GamingBudget.IsActive, Log);
        return (service, catalog);
    }

    /// <summary>A liked song's catalog recording: the same identity, so its preview is that song.</summary>
    internal static Func<TasteRecording, CancellationToken, Task<CatalogSong?>> Resolver(ArtistCatalog catalog) => async (like, ct) =>
    {
        var key = DiscoverIdentity.Key(like.Title, like.Artist);
        var result = await catalog.SearchSongsAsync(like.Artist + " " + like.Title, ct).ConfigureAwait(false);
        return result.Value.Select(s => s.Song).FirstOrDefault(s => DiscoverIdentity.Key(s) == key);
    };

    // The plugin log is memory-only; the session log keeps the lane's few status/count lines (no paths, keys or song names).
    private void Log(string line) { _log(line); Noctis.Services.DebugLog.Write("ForYou", line); }

    public Task<IReadOnlyDictionary<string, double>> RankAsync(IReadOnlyList<CatalogSong> candidates,
        IReadOnlyList<TasteRecording> liked, IReadOnlyList<TastePlay> plays, CancellationToken ct)
    {
        _service.Enabled = _enabled();
        return _service.RankAsync(candidates, liked, plays, ct);
    }

    public void Changed(bool enabled) => _service.Enabled = enabled;
    public void UpdateProxy(string proxy)
    {
        var old = _service; var catalog = _catalog;
        (_service, _catalog) = Create(proxy);
        old.Dispose();
        _ = old.Completion.ContinueWith(_ => catalog.Dispose(), TaskScheduler.Default);
    }
    public void Dispose()
    {
        _service.Dispose();
        var catalog = _catalog;
        _ = _service.Completion.ContinueWith(_ => catalog.Dispose(), TaskScheduler.Default);
    }
}
