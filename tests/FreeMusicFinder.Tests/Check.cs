using Avalonia.Threading;

namespace FreeMusicFinder.Tests;

/// <summary>The few checks the tests need; each prints one line.</summary>
internal static class Check
{
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "FreeMusicFinder.Tests-" + Guid.NewGuid().ToString("N")[..8]);

    public static int Passed { get; private set; }
    public static int Failed { get; private set; }

    public static void Section(string name) => Console.WriteLine("\n" + name);

    public static void True(bool condition, string what)
    {
        if (condition) Passed++;
        else Failed++;
        Console.WriteLine((condition ? "  ok    " : "  WRONG ") + what);
    }

    public static void False(bool condition, string what) => True(!condition, what);

    public static void Equal<T>(T expected, T actual, string what)
    {
        var same = EqualityComparer<T>.Default.Equals(expected, actual);
        True(same, same ? what : $"{what}: expected \"{expected}\", got \"{actual}\"");
    }

    public static void Throws<T>(Action action, string what) where T : Exception
    {
        try
        {
            action();
            True(false, what + ": nothing was thrown");
        }
        catch (T)
        {
            True(true, what);
        }
    }

    public static async Task ThrowsAsync<T>(Func<Task> action, string what) where T : Exception
    {
        try
        {
            await action();
            True(false, what + ": nothing was thrown");
        }
        catch (T)
        {
            True(true, what);
        }
        catch (Exception ex)
        {
            True(false, $"{what}: {ex.GetType().Name} was thrown ({ex.Message})");
        }
    }

    /// <summary>Waits up to ten seconds for something that happens on another thread.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(10);
        True(condition(), what);
    }

    /// <summary>
    /// The same for the window tests, which run on the UI thread: keeps the UI's own work going
    /// while it waits.
    /// </summary>
    public static void Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) break;
            Thread.Sleep(10);
        }
        True(condition(), what);
    }

    /// <summary>An empty folder for one test, under the system's temporary folder.</summary>
    public static string NewFolder(string name)
    {
        var folder = Path.Combine(Work, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void RemoveFolders()
    {
        try { if (Directory.Exists(Work)) Directory.Delete(Work, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
