using System;
using System.IO;
using System.Linq;
using Noctis.Services;

namespace Noctis.Tests;

/// <summary>
/// CI (linux 10-10, windows 10-18) once found 1 of the 2 fixture songs a test had just written,
/// never reproduced locally. A test that checks what its scan found reports this on a miss
/// instead of a bare count: the songs that came in, each file under the folder (in the
/// library or not, its size, whether it reads now), the static switches a scan reads, and the
/// session log's last scan lines ("skipped" names a file the tag read refused).
/// </summary>
internal static class ScanMissReport
{
    public static string Describe(ILibraryService library, string folder)
    {
        var inLibrary = library.AllTracks.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(folder, "*.mp3", SearchOption.AllDirectories).Select(f =>
        {
            var state = $"{(inLibrary.Contains(f) ? "in library" : "MISSING")}, {new FileInfo(f).Length} bytes";
            try
            {
                using var file = TagLib.File.Create(f);
                return $"{Path.GetFileName(f)} ({state}) reads now";
            }
            catch (Exception ex)
            {
                return $"{Path.GetFileName(f)} ({state}): {ex.GetType().Name}: {ex.Message}";
            }
        });
        // The scan's count line, the files it named as unreadable (and why the tag read refused
        // them), and the guards that keep or roll back a library.
        var scanLog = DebugLog.Snapshot().Split(Environment.NewLine)
            .Where(l => l.Contains("[Scan] published") || l.Contains("unreadable:") || l.Contains("[Metadata]")
                        || l.Contains("[Library] Scan"))
            .TakeLast(10);
        return $"{library.AllTracks.Count} track(s), {library.Tracks.Count} visible "
               + $"[{string.Join(", ", library.AllTracks.Select(t => t.Title))}]; "
               + $"files: {string.Join("; ", files)}; "
               + $"dev log {(DebugLogger.IsEnabled ? "on" : "off")}, roots [{string.Join(", ", MetadataService.MusicRootFolders)}]; "
               + $"log: {string.Join(" | ", scanLog)}";
    }
}
