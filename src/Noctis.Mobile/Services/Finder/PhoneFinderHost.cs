using FreeMusicFinder;
using Noctis.Mobile.Services.Telegram;
using Noctis.Mobile.ViewModels;
using Noctis.Services;
using Noctis.Services.Search;

namespace Noctis.Mobile.Services.Finder;

/// <summary>
/// The phone's one Music Finder: its own Telegram session (never a copied desktop one) and private
/// music/cache folders, owned by the app host. A saved download joins the library through
/// <see cref="ILibraryService.ImportFilesAsync"/> as an individually added file, so it stays across
/// restarts and rescans; a song only played stays in the stream cache and out of Songs.
/// </summary>
internal sealed class PhoneFinderHost : IDisposable
{
    private readonly ILibraryService _library;
    private readonly HashSet<string> _imported = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _importGate = new(1, 1);

    public FinderSession Session { get; }
    public IPhoneTelegramSignIn SignIn { get; }
    public IOnlineMusicSearch Search => Session.Online;

    /// <param name="dataFolder">Private, not backed up (the session and queue live here).</param>
    /// <param name="musicFolder">Private folder downloads are saved in.</param>
    /// <param name="defaultsPath">The build's packed deployment pair, or a path that does not exist.</param>
    public PhoneFinderHost(string dataFolder, string musicFolder, string defaultsPath, ILibraryService library,
        LibraryViewModel playlists, Func<Func<Task>, Task> onUi, Action<string> log)
    {
        _library = library;
        Session = new FinderSession(dataFolder, musicFolder, new PhonePlaylistLibrary(library, playlists, onUi), () => library,
            log, defaultsPath, completeFilesOnly: true);
        SignIn = new PhoneTelegramSignIn(Session);
        Session.Downloads.Changed += OnDownloadChanged;
        // Files saved while the app was closed (the queue resumes them): make sure each is in Songs.
        foreach (var d in Session.Downloads.History) OnDownloadChanged(d);
    }

    private void OnDownloadChanged(Download d)
    {
        if (d.State is not (DownloadState.Saved or DownloadState.AlreadyThere) || d.SavedPath is not { } path
            || Session.Downloads.IsCached(d) || !IsInside(Session.MusicFolder, path)) return;
        lock (_imported) if (!_imported.Add(path)) return;
        _ = ImportAsync(path);
    }

    private async Task ImportAsync(string path)
    {
        await _importGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_library.AllTracks.Any(t => string.Equals(t.FilePath, path, StringComparison.Ordinal))) return;
            await _library.ImportFilesAsync([path]).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Retried on the next change or start; the file stays where it was saved.
            lock (_imported) _imported.Remove(path);
            DebugLog.Write("Finder", $"Saved song could not join the library: {ex.GetType().Name}");
        }
        finally { _importGate.Release(); }
    }

    internal static bool IsInside(string folder, string path)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Session.Downloads.Changed -= OnDownloadChanged;
        Session.Dispose();
    }
}
