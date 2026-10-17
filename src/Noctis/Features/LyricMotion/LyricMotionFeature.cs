using Noctis.Plugins;

namespace Noctis.Features.LyricMotion;

/// <summary>Settings/lifetime only. The lyrics views own their renderers and frame subscriptions.</summary>
public sealed class LyricMotionFeature : INoctisPlugin
{
    private IPluginSettings? _settings;
    internal static Tuning Current { get; private set; } = Tuning.From(null);
    internal static bool IsEnabled { get; private set; }
    internal static event Action? Changed;

    public PluginInfo Info { get; } = new("dev.moshi.lyricmotion", "Lyric Motion", "1.5.0", "moshi",
        "Word and letter motion built into the lyrics renderer.");

    public void Initialize(IPluginHost host)
    {
        _settings = host.Settings;
        _settings.Changed += OnChanged;
        IsEnabled = true;
        Apply();
    }

    private void OnChanged(object? sender, string key) => Apply();
    private void Apply()
    {
        Current = Tuning.From(_settings);
        Changed?.Invoke();
    }

    public void Shutdown()
    {
        if (_settings is not null) _settings.Changed -= OnChanged;
        _settings = null;
        IsEnabled = false;
        Changed?.Invoke();
    }
}
