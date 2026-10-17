using Noctis.Plugins;

namespace Noctis.Features.TrueShuffle;

/// <summary>Settings and lifetime only; the player owns the typed service and native menus.</summary>
public sealed class TrueShuffleFeature : INoctisPlugin
{
    private readonly TrueShuffleService? _service;
    private IPluginSettings? _settings;
    internal TrueShuffleFeature(TrueShuffleService? service) => _service = service;
    public PluginInfo Info { get; } = new("dev.moshi.trueshuffle", "True Shuffle", "1.6.0", "moshi",
        "Shuffle modes built into Noctis's player and queue.");
    public void Initialize(IPluginHost host)
    {
        _settings = host.Settings; _settings.Changed += Changed;
        _service?.Start(host.Settings, host.DataDirectory);
    }
    private void Changed(object? sender, string key) => _service?.SettingChanged(key);
    public void Shutdown()
    {
        if (_settings is not null) _settings.Changed -= Changed;
        _settings = null; _service?.Dispose();
    }
}
