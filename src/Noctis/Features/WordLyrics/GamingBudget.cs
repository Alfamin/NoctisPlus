using System.Runtime.InteropServices;

namespace Noctis.Features.WordLyrics;

/// <summary>Conservative Windows fullscreen workload signal; not a universal/windowed-game detector.</summary>
internal static class GamingBudget
{
    // Microsoft documents 2 as fullscreen/busy and 3 as exclusive Direct3D fullscreen.
    // https://learn.microsoft.com/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state
    internal static bool ProtectsFullscreen(int state) => state is 2 or 3;
    internal static string Speed(string selected, bool protect) => protect ? "Light" : selected;

    internal static bool IsActive()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return SHQueryUserNotificationState(out var state) >= 0 && ProtectsFullscreen(state); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHQueryUserNotificationState(out int state);
}
