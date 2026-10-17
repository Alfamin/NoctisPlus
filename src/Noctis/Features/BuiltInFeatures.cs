using System.Reflection;
using Noctis.Services.Plugins;

namespace Noctis.Features;

/// <summary>
/// The Noctis Plus features compiled into the app (Settings → Features). Each keeps the id it had
/// as a separate plugin, so the user's settings and its data folder carry over unchanged.
/// </summary>
internal static class BuiltInFeatures
{
    public static void Register(PluginHost host)
    {
        host.RegisterBuiltIn(Manifest("FreeMusicFinder"), () => new FreeMusicFinder.FreeMusicPlugin());
        host.RegisterBuiltIn(Manifest("LyricMotion"), () => new LyricMotion.LyricMotionFeature());
    }

    /// <summary>The feature's plugin.json, embedded as Noctis.Features.&lt;folder&gt;.plugin.json.</summary>
    private static PluginManifest Manifest(string folder)
    {
        var name = $"Noctis.Features.{folder}.plugin.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded in the app.");
        using var reader = new StreamReader(stream);
        return PluginManifest.Parse(reader.ReadToEnd());
    }
}
