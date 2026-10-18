using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Features.WordLyrics;
using Noctis.Models;
using Noctis.Services.Plugins;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

[Collection("MetadataServiceStatics")] // shares the process-wide saved-download event with importer/pipeline checks
public class NativeWordLyricsTests
{
    private sealed class Rig : IDisposable
    {
        internal readonly TestPersistenceService Data = new();
        internal readonly FakeLibraryService Library = new();
        internal readonly AppSettings Settings = new() { CommunityPluginsEnabled = false };
        internal readonly WordLyricsService Service;
        internal readonly PluginHost Host;
        internal Rig(bool off = false, bool initialSong = true, bool finderOff = false)
        {
            Settings.MusicFolders = [Data.DataDirectory];
            Settings.PluginSettingValues = new() { ["dev.moshi.wordlyrics"] = new() { ["auto"] = off ? "false" : "true", ["speed"] = "Light", ["folder"] = Path.Combine(Data.DataDirectory, "absent backend") } };
            if (finderOff)
            {
                Settings.DisabledPlugins = ["dev.moshi.freemusicfinder"];
                Settings.PluginSettingValues["dev.moshi.freemusicfinder"] = new() { ["proxy"] = "socks5://127.0.0.1:9" };
            }
            if (initialSong) Library.TrackList.Add(Song("Existing"));
            Service = new(Library, () => Settings);
            Host = new(null, Data.DataDirectory, () => Settings, () => { }, "1.6.3", Library);
            Noctis.Features.BuiltInFeatures.Register(Host, words: Service); Host.LoadAll();
        }
        internal Track Song(string title)
        {
            var path = Path.Combine(Data.DataDirectory, title + ".mp3"); File.WriteAllText(path, "synthetic fixture");
            return new() { Title = title, Artist = "Synthetic", FilePath = path };
        }
        public void Dispose() { Host.UnloadAll(); Data.Dispose(); }
    }
    private static void Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(6)) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Assert.True(condition());
    }
    [AvaloniaFact]
    public void AutomaticTimingStatus_IsPerSong_AndCancelClearsItWithoutDownloading()
    {
        using var rig = new Rig();
        var song = rig.Song("New download");
        rig.Service.Presenter = (_, _) => { };
        var other = rig.Song("Other song");
        OnlineMusicSearch.PublishSavedFile(song.FilePath);
        Until(() => rig.Service.StatusFor(song.FilePath) == SongTimingStatus.SetupNeeded);
        Assert.Equal(SongTimingStatus.None, rig.Service.StatusFor(other.FilePath));
        var duplicate = rig.Service.TimeSongAsync(song);
        Until(() => duplicate.IsCompleted); duplicate.GetAwaiter().GetResult();
        Assert.Equal(1, rig.Service.WaitingCount);
        var cancel = rig.Service.CancelAsync(); Until(() => cancel.IsCompleted);
        Until(() => rig.Service.StatusFor(song.FilePath) == SongTimingStatus.None);
        Assert.False(Directory.Exists(Path.Combine(rig.Data.DataDirectory, "absent backend")));
    }
    [AvaloniaFact]
    public void NativeMigrationRunsBeforeDefaults_AndNewChoiceKeepsRollbackKeys()
    {
        using var rig = new Rig(off: true);
        var plugin = rig.Host.FindById("dev.moshi.wordlyrics")!;
        var mode = Assert.Single(plugin.SettingItems, s => s.Key == "defaultMode");
        Assert.Equal("Off", mode.Value); Assert.Null(plugin.AssemblyPath);
        mode.SelectedChoice = "Medium";
        var raw = rig.Settings.PluginSettingValues!["dev.moshi.wordlyrics"];
        Assert.Equal("true", raw["auto"]); Assert.Equal("Half", raw["speed"]);
        Assert.Equal("Medium", raw["defaultMode"]);
    }

    [AvaloniaFact]
    public void LyricsPageTimeWords_QueuesOnlyThatSong_AndMissingSetupNeverDownloads()
    {
        using var rig = new Rig(off: true);
        var song = rig.Library.TrackList[0];
        Track? shown = null;
        rig.Service.Presenter = (library, track) => { Assert.False(library); shown = track; };
        var request = rig.Service.TimeSongAsync(song);
        Until(() => request.IsCompleted); request.GetAwaiter().GetResult();
        Assert.Equal(1, rig.Service.WaitingCount);
        Assert.Same(song, shown);
        Assert.False(Directory.Exists(Path.Combine(rig.Data.DataDirectory, "absent backend")));
        Assert.False(File.Exists(Path.Combine(rig.Service.DataDirectory, "setup.cmd")));
        Assert.True(File.Exists(song.FilePath));
    }

    [AvaloniaFact]
    public void LyricsPageTimeWords_RejectsRemoteSongsWithoutOpeningSetup()
    {
        using var rig = new Rig(off: true);
        var shown = false;
        rig.Service.Presenter = (_, _) => shown = true;
        var request = rig.Service.TimeSongAsync(new Track { SourceType = SourceType.Navidrome, FilePath = "https://example.invalid/song" });
        Until(() => request.IsCompleted); request.GetAwaiter().GetResult();
        Assert.Equal(0, rig.Service.WaitingCount);
        Assert.False(shown);
    }
    [AvaloniaFact]
    public void ExistingLibraryIsNotBackfilled_AndNewDownloadsAreQueuedOnce()
    {
        using var rig = new Rig();
        Assert.Equal(0, rig.Service.WaitingCount);
        var song = rig.Song("Downloaded");
        OnlineMusicSearch.PublishSavedFile(song.FilePath);
        OnlineMusicSearch.PublishSavedFile(song.FilePath);
        Until(() => rig.Service.WaitingCount == 1);
        rig.Library.TrackList.Add(song); rig.Library.RaiseLibraryUpdated(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, rig.Service.WaitingCount);
    }
    [AvaloniaFact]
    public void FirstNewSongInAnInitiallyEmptyLibraryIsNotLost_AndOffDoesNoAutomaticWork()
    {
        using (var rig = new Rig(initialSong: false))
        {
            var song = rig.Song("First new song");
            File.SetCreationTimeUtc(song.FilePath, DateTime.UtcNow.AddMilliseconds(-500)); // coarse filesystem timestamp
            rig.Library.TrackList.Add(song); rig.Library.RaiseLibraryUpdated();
            Until(() => rig.Service.WaitingCount == 1);
        }
        using (var rig = new Rig(off: true))
        {
            var song = rig.Song("New"); OnlineMusicSearch.PublishSavedFile(song.FilePath);
            Thread.Sleep(100); Dispatcher.UIThread.RunJobs(); Assert.Equal(0, rig.Service.WaitingCount);
        }
    }
    [AvaloniaFact]
    public void ProgressDialogCannotAccidentallyQueueTheLibrary_AndCancelKeepsAudio()
    {
        using var rig = new Rig();
        using var vm = new WordLyricsViewModel(rig.Service, false, null, _ => Task.FromResult(true));
        Assert.False(vm.CanQueue); Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(vm.Indeterminate);
        var path = rig.Library.TrackList[0].FilePath;
        var queued = rig.Service.QueueAsync([path]); Until(() => queued.IsCompleted); queued.GetAwaiter().GetResult();
        Until(() => rig.Service.WaitingCount == 1);
        var cancel = rig.Service.CancelAsync(); Until(() => cancel.IsCompleted);
        Assert.Equal(0, rig.Service.WaitingCount); Assert.True(File.Exists(path));
    }
    [AvaloniaFact]
    public void NativeDialogLoadsAndCustomDraftPreservesLineGuides()
    {
        using var rig = new Rig();
        var song = rig.Library.TrackList[0];
        File.WriteAllText(Path.ChangeExtension(song.FilePath, ".elrc"), "[00:01.00]<00:01.00>hello <00:02.00>world");
        var dialog = new WordLyricsDialog(rig.Service, false, song);
        var vm = Assert.IsType<WordLyricsViewModel>(dialog.DataContext);
        dialog.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            vm.ActionIndex = 3; vm.Words = "\n[00:01.00]hello world\n\n.done\n";
            Assert.True(vm.IsCustom); Assert.True(vm.CanQueue);
            var draft = LyricDrafts.Save(rig.Service.DataDirectory, vm.Words);
            Assert.Equal("[00:01.00]hello world\n", File.ReadAllText(draft));
            Assert.Contains("<00:01.00>", File.ReadAllText(Path.ChangeExtension(song.FilePath, ".elrc")));
        }
        finally { dialog.Close(); }
    }
    [Fact]
    public void EmbeddedWordReaderAndDraftsUseNativeTypesAndRejectEmptyText()
    {
        Assert.True(EmbeddedWordLyrics.HasWords("fixture", _ => "[00:01.00]<00:01.00>hello"));
        Assert.False(EmbeddedWordLyrics.HasWords("fixture", _ => "[00:01.00]hello"));
        Assert.Equal("[00:02.00]hello", LyricDrafts.Normalize("\r\n [00:02.00]hello \r\n\r\n.done\r\n"));
        using var data = new TestPersistenceService();
        Assert.Throws<InvalidOperationException>(() => LyricDrafts.Save(data.DataDirectory, "\n .done \n"));
    }
    [AvaloniaFact]
    public void WordLyricsKeepsFindersProxyEvenWhenFinderIsDisabled()
    {
        using var rig = new Rig(finderOff: true);
        Assert.Equal("socks5://127.0.0.1:9", rig.Service.MetadataProxy);
        var proxy = rig.Host.FindById("dev.moshi.freemusicfinder")!.SettingItems.Single(s => s.Key == "proxy");
        proxy.TextValue = "direct";
        Assert.Equal("direct", rig.Service.MetadataProxy);
    }
    [AvaloniaFact]
    public void SetupRequiresExplicitConsent_AndCancelCreatesNoInstaller()
    {
        using var rig = new Rig(); string? prompt = null;
        var request = rig.Service.SetupAsync(text => { prompt = text; return Task.FromResult(false); });
        Until(() => request.IsCompleted); request.GetAwaiter().GetResult();
        Assert.Contains("1.33 GB", prompt); Assert.Contains("optional", prompt);
        Assert.Equal(2, prompt!.Split('\n').Length);
        Assert.All(prompt.Split('\n'), line => Assert.True(line.Length <= 50));
        Assert.False(File.Exists(Path.Combine(rig.Service.DataDirectory, "setup.cmd")));
        Assert.False(Directory.Exists(Path.Combine(rig.Data.DataDirectory, "absent backend")));
    }
    [AvaloniaFact]
    public void LongErrorsWrapInsideTheNativeDialog()
    {
        using var rig = new Rig();
        rig.Service.SetPreviewState(new("Waiting", string.Join(" ", Enumerable.Repeat("Detailed permission error and a long folder path", 40)), Error: true, Finished: true));
        Dispatcher.UIThread.RunJobs();
        var dialog = new WordLyricsDialog(rig.Service, true, null); dialog.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var scroll = dialog.WordPanel.FindControl<ScrollViewer>("WordBodyScroll")!;
            Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"{scroll.Extent.Width} > {scroll.Viewport.Width}");
        }
        finally { dialog.Close(); }
    }
    [AvaloniaFact]
    public void LockedFileGetsAVisibleRetryStateInsteadOfSilentWaiting()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing semantics, including Windows 10.
        using var data = new TestPersistenceService(); var path = Path.Combine(data.DataDirectory, "locked.mp3");
        File.WriteAllText(path, "synthetic fixture"); var states = new ConcurrentQueue<ProgressState>();
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var worker = new Worker(Path.Combine(data.DataDirectory, "work"), () => [data.DataDirectory],
            new(true, "Light", false, Path.Combine(data.DataDirectory, "absent backend"), ""), _ => { }, _ => { }, states.Enqueue);
        worker.Ask(path); Until(() => states.Any(s => s.Detail.Contains("WORDLYRICS_FILE_ACCESS")));
        Assert.Equal(1, worker.WaitingCount); Assert.True(File.Exists(path));
    }
    [Fact]
    public void GeniusParserReadsOnlyCompleteContainersAndValidatesPages()
    {
        var html = "<h1>Page header</h1><div data-lyrics-container=\"true\">One &amp; two<br/>Three <a>four</a></div><div class='annotation'>Ignore me</div><div data-lyrics-container='true'>Last line</div>";
        Assert.Equal("One & two\nThree four\nLast line", LyricSources.GeniusText(html));
        Assert.Equal("", LyricSources.GeniusText("<div data-lyrics-container='true'>Incomplete"));
        Assert.Equal("genius.com", LyricSources.GeniusUrl("https://genius.com/Song-lyrics?utm=test").Host);
        foreach (var bad in new[] { "http://genius.com/Song-lyrics", "https://genius.com.evil.test/Song-lyrics", "https://user:pass@genius.com/Song-lyrics", "https://127.0.0.1/Song-lyrics", "https://genius.com/artist", "https://genius.com:444/Song-lyrics" })
            Assert.Throws<InvalidOperationException>(() => LyricSources.GeniusUrl(bad));
    }
    [Fact]
    public async Task BackendProxyRequiresAuthentication_AndTunnelsBytesWithoutTlsInterception()
    {
        var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        int connections = 0;
        using var proxy = new BackendProxy(() => "direct", async (host, port, ct) =>
        {
            Assert.Equal("example.test", host); Assert.Equal(443, port); Interlocked.Increment(ref connections);
            var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)upstream.LocalEndpoint).Port, ct); return tcp;
        });
        var uri = new Uri(proxy.Address);
        try
        {
            using (var denied = new TcpClient())
            {
                await denied.ConnectAsync(uri.Host, uri.Port);
                await denied.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT example.test:443 HTTP/1.1\r\n\r\n"));
                var data = new byte[200]; var read = await denied.GetStream().ReadAsync(data);
                Assert.Contains("407", Encoding.ASCII.GetString(data, 0, read)); Assert.Equal(0, connections);
            }
            var accepting = upstream.AcceptTcpClientAsync();
            using var client = new TcpClient(); await client.ConnectAsync(uri.Host, uri.Port);
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes(Uri.UnescapeDataString(uri.UserInfo)));
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("CONNECT example.test:443 HTTP/1.1\r\nProxy-Authorization: Basic " + auth + "\r\n\r\n"));
            using var server = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
            var header = new byte[100]; var count = await client.GetStream().ReadAsync(header);
            Assert.Contains("200 Connection Established", Encoding.ASCII.GetString(header, 0, count));
            await client.GetStream().WriteAsync("synthetic encrypted bytes"u8.ToArray());
            var payload = new byte[25]; var got = await server.GetStream().ReadAsync(payload);
            Assert.Equal("synthetic encrypted bytes", Encoding.ASCII.GetString(payload, 0, got));
            proxy.Dispose();
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { upstream.Stop(); }
    }
}
