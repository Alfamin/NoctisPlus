namespace Noctis.Services;

/// <summary>
/// A component that keeps an audio file open for as long as it uses it: the player's
/// open input, its staged next track, the visualizer's side decode. On Windows nothing
/// can replace a file while any handle on it is open, so the crash-safe tag save (copy,
/// tag the copy, rename it over the original) failed for the playing track.
/// </summary>
public interface IOpenFileHolder
{
    /// <summary>
    /// Closes every handle this holder keeps on <paramref name="path"/> and returns the
    /// action that opens it again where it was, or null when it holds nothing on that
    /// path. Returns once the handles are closed.
    /// </summary>
    Action? Release(string path);
}

/// <summary>The holders the app registers (the player, the side decode).</summary>
public static class OpenFileHolders
{
    private static readonly object Gate = new();
    private static IOpenFileHolder[] _holders = [];

    public static void Register(IOpenFileHolder holder)
    {
        lock (Gate)
        {
            if (Array.IndexOf(_holders, holder) < 0)
                _holders = [.. _holders, holder];
        }
    }

    public static void Unregister(IOpenFileHolder holder)
    {
        lock (Gate)
            _holders = Array.FindAll(_holders, h => !ReferenceEquals(h, holder));
    }

    /// <summary>
    /// Runs <paramref name="action"/> while no registered holder has <paramref name="path"/>
    /// open, then lets every holder that let go of it open it again, also when the action
    /// throws. Returns how many holders let go of the path.
    /// </summary>
    public static int RunReleased(string path, Action action)
    {
        var reopen = new List<Action>();
        try
        {
            foreach (var holder in Volatile.Read(ref _holders))
            {
                try
                {
                    if (holder.Release(path) is { } r)
                        reopen.Add(r);
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Metadata", $"Release of '{Path.GetFileName(path)}' failed: {ex.Message}");
                }
            }
            action();
        }
        finally
        {
            for (var i = reopen.Count - 1; i >= 0; i--)
            {
                try { reopen[i](); }
                catch (Exception ex)
                {
                    DebugLog.Write("Metadata", $"Reopen of '{Path.GetFileName(path)}' failed: {ex.Message}");
                }
            }
        }
        return reopen.Count;
    }
}
