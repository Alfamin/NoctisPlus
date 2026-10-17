using Noctis.Plugins;

namespace Noctis.Features.WordLyrics;

public sealed class WordLyricsFeature : INoctisPlugin
{
    private readonly WordLyricsService? _service;
    private IPluginSettings? _settings;
    internal WordLyricsFeature(WordLyricsService? service) => _service = service;
    internal static WordLyricsService? Current { get; private set; }
    public PluginInfo Info { get; } = new("dev.moshi.wordlyrics", "WordLyrics", "1.3.0", "moshi", "Word timing built into Noctis. Uses your installed WordLyrics backend.");
    public void Initialize(IPluginHost host)
    {
        _settings = host.Settings; _settings.Changed += Changed;
        Current = _service; _service?.Start(host);
    }
    private void Changed(object? sender, string key) => _service?.SettingChanged(key);
    public void Shutdown()
    {
        if (_settings is not null) _settings.Changed -= Changed;
        _settings = null; _service?.Dispose();
        if (ReferenceEquals(Current, _service)) Current = null;
    }
}
