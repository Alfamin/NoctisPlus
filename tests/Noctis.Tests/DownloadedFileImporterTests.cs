using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Search;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Noctis.Features.ProgressivePlayback;

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

    [AvaloniaFact]
    public async Task CompletedMp3ImportsWhilePlaybackRetainsItsFile_AndUnreadableAudioHasAPreciseNotice()
    {
        using var data = new TestPersistenceService();
        var audio = await ProgressiveDecoderTests.AudioAsync("mp3", data.DataDirectory, noisy: true);
        var target = Path.Combine(data.DataDirectory, "held.mp3"); var part = target + ".fmf.part";
        var transfer = new ProgressiveTransfer(data.DataDirectory, "held", TimeSpan.FromSeconds(24));
        var writer = transfer.Attach(new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1));
        ((IProgressiveAudioWriter)writer).SetFormatPolicy(_ => true);
        ((IProgressiveAudioWriter)writer).BeginAudio(audio.Length, "mp3");
        var split = audio.Length * 2 / 3; writer.Write(audio.AsSpan(0, split));
        var until = Environment.TickCount64 + 5000;
        while (transfer.ReadyPath is null && Environment.TickCount64 < until) await Task.Delay(15);
        using var lease = ProgressiveTransfer.Acquire(target); Assert.NotNull(lease);
        writer.Write(audio.AsSpan(split)); File.Move(part, target); transfer.End(target);
        var persistence = new PersistenceService(Path.Combine(data.DataDirectory, "profile"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = [], WatchFoldersEnabled = false });
        var library = new LibraryService(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence), new RecordingAudit());
        var notices = new List<string>(); using var importer = new DownloadedFileImporter(library, notices.Add);
        OnlineMusicSearch.PublishSavedFile(target);
        until = Environment.TickCount64 + 6000;
        while (library.AllTracks.Count == 0 && Environment.TickCount64 < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.Single(library.AllTracks); Assert.Empty(notices); Assert.True(lease.Buffer.Complete);
        var broken = Path.Combine(data.DataDirectory, "unreadable.mp3"); File.WriteAllText(broken, "not audio");
        OnlineMusicSearch.PublishSavedFile(broken);
        until = Environment.TickCount64 + 6000;
        while (notices.Count == 0 && Environment.TickCount64 < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.Contains("DOWNLOAD_IMPORT_FAILED", Assert.Single(notices));
        Assert.Contains("audio metadata", notices[0]); Assert.True(File.Exists(broken));
        importer.Dispose(); await importer.Work.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
