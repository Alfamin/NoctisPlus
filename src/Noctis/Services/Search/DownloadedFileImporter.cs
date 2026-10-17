using System.Threading.Channels;
using Avalonia.Threading;

namespace Noctis.Services.Search;

/// <summary>Completed downloads enter the library even with Watch Folders off or a custom
/// destination. Importing reads metadata; it never moves files or scans the whole library.</summary>
internal sealed class DownloadedFileImporter : IDisposable
{
    private readonly Channel<string> _files = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILibraryService _library;
    private readonly Action<string> _notice;
    internal Task Work { get; }
    internal DownloadedFileImporter(ILibraryService library, Action<string> notice)
    {
        _library = library; _notice = notice;
        OnlineMusicSearch.FileSaved += Saved; Work = Task.Run(RunAsync);
    }
    private void Saved(object? sender, string path)
    {
        lock (_pending)
        {
            if (_stop.IsCancellationRequested || !_pending.Add(path)) return;
            if (!_files.Writer.TryWrite(path)) _pending.Remove(path);
        }
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var path in _files.Reader.ReadAllAsync(_stop.Token))
            {
                // LibraryUpdated is UI-owned. The service does its file work asynchronously,
                // but the call must originate on the dispatcher so its publish returns there.
                try { await Dispatcher.UIThread.InvokeAsync(() => _library.ImportFilesAsync([path], _stop.Token)); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { _notice($"DOWNLOAD_IMPORT_FAILED: {Path.GetFileName(path)} is saved, but could not be added to the library ({ex.Message}). Open Downloads and choose Add to library to retry."); }
                finally { lock (_pending) _pending.Remove(path); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        OnlineMusicSearch.FileSaved -= Saved;
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel(); _files.Writer.TryComplete();
    }
}
