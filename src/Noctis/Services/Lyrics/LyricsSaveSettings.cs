using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Services.Lyrics;

/// <summary>
/// The live "Save lyrics to" choice (Settings ▸ Lyrics, GitHub #135) for code that saves lyrics
/// without a settings object of its own. No shell (tests, early startup) reads as the default,
/// lyrics files beside the song — the behaviour from before the setting existed.
/// </summary>
public static class LyricsSaveSettings
{
    public static LyricsSaveTarget CurrentTarget()
    {
        try
        {
            var settings = App.Services?.GetService<MainWindowViewModel>()?.Settings.GetSettings();
            return LyricsSaveOptions.ParseTarget(settings?.LyricsSaveTarget);
        }
        catch
        {
            return LyricsSaveTarget.Sidecar;
        }
    }
}
