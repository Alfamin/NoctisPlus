using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Search;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Noctis.Tests;

[Collection("MetadataServiceStatics")]
public class DownloadedFileImporterTests
{
    private sealed class RecordingAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
    [AvaloniaFact]
    public async Task SavedFileOutsideAllFoldersImportsWithoutAWatcher_AndSurvivesRescan()
    {
        using var data = new TestPersistenceService();
        var music = Directory.CreateDirectory(Path.Combine(data.DataDirectory, "music")).FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(data.DataDirectory, "custom downloads")).FullName;
        var song = Path.Combine(downloads, "synthetic.wav"); File.WriteAllBytes(song, ProgressiveDecoderTests.Wave(1));
        var persistence = new PersistenceService(Path.Combine(data.DataDirectory, "profile"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = [music], WatchFoldersEnabled = false });
        var library = new LibraryService(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence), new RecordingAudit());
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.LibraryUpdated += (_, _) => { if (library.AllTracks.Any(t => t.FilePath == song)) done.TrySetResult(); };
        var notices = new List<string>();
        using var importer = new DownloadedFileImporter(library, notices.Add);
        OnlineMusicSearch.PublishSavedFile(song); OnlineMusicSearch.PublishSavedFile(song);
        var until = Environment.TickCount64 + 6000;
        while (!done.Task.IsCompleted && Environment.TickCount64 < until)
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(done.Task.IsCompleted, string.Join("\n", notices));
        Assert.Single(library.AllTracks); Assert.Empty(notices);
        await library.ScanAsync([music]); Assert.Single(library.AllTracks);
        Assert.True(File.Exists(song));
        importer.Dispose(); await importer.Work.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
