using System.Reflection;
using Noctis.Services.Plugins;

namespace Noctis.Features;

/// <summary>
/// The Noctis Plus features compiled into the app (Settings → Features). Each keeps the id it had
/// as a separate plugin, so the user's settings and its data folder carry over unchanged.
/// </summary>
internal static class BuiltInFeatures
{
    public static void Register(PluginHost host, TrueShuffle.TrueShuffleService? shuffle = null, WordLyrics.WordLyricsService? words = null)
    {
        words?.AttachPreferences(host);
        var finder = Manifest("FreeMusicFinder");
        host.RegisterBuiltIn(finder, () => new FreeMusicFinder.FreeMusicPlugin(),
            stored => FreeMusicFinder.QualityDefault.Settle(stored, Path.Combine(host.PluginDataRoot, finder.Id)));
        host.RegisterBuiltIn(Manifest("LyricMotion"), () => new LyricMotion.LyricMotionFeature());
        host.RegisterBuiltIn(Manifest("TrueShuffle"), () => new TrueShuffle.TrueShuffleFeature(shuffle));
        host.RegisterBuiltIn(Manifest("WordLyrics"), () => new WordLyrics.WordLyricsFeature(words), WordLyrics.WordTimingSettings.Migrate);
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
