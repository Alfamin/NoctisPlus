#if DEBUG
using Avalonia.Controls;
using FreeMusicFinder;
using Noctis.Helpers;
using Noctis.ViewModels;
using Noctis.Views;

namespace Noctis.Features;

// A real-app visual check with synthetic data. Never reads credentials, opens a network
// connection, or writes a Telegram store. Not compiled into Release builds.
internal static class NativeFeaturePreview
{
    internal static async Task ShowAsync(Window owner, string preview)
    {
        // Explicit profile override is required for any developer preview.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOCTIS_PLUS_DATA_DIR"))) return;
        if (preview.StartsWith("progressive-", StringComparison.Ordinal))
        { await ProgressivePlayback.ProgressivePlaybackPreview.ShowAsync(owner, preview); return; }
        if (preview == "downloads-recovery" && owner.DataContext is MainWindowViewModel main)
        {
            var page = new DownloadsViewModel(); page.Attach(new RecoveryQueue()); main.CurrentView = page;
            return;
        }
        if (preview == "artist-avenoir")
        {
            FreeMusicPlugin.Current?.OnlineSearch?.OpenArtist(new("deezer:11235313", "Avenoir"));
            return;
        }
        if (preview == "playlist-import") { await ShowPlaylistImportAsync(owner); return; }
        if (preview.StartsWith("wordlyrics", StringComparison.Ordinal))
        {
            for (var n = 0; n < 20 && WordLyrics.WordLyricsFeature.Current is null; n++) await Task.Delay(50);
            if (WordLyrics.WordLyricsFeature.Current is not { } words) return;
            words.SetPreviewState(preview == "wordlyrics-error"
                ? new("Waiting for the lyric provider", "DNS_FAILED: Check your VPN or system proxy. Queued songs are saved and retry later.", Error: true, Finished: true)
                : new("Timing words", "Synthetic Artist · Night Drive", 3, 8, .45, Controllable: true));
            var track = preview == "wordlyrics-custom" ? new Models.Track { Title = "Night Drive", Artist = "Synthetic Artist", FilePath = Path.Combine(Path.GetTempPath(), "noctis-synthetic.wav") } : null;
            var wordDialog = new WordLyricsDialog(words, preview == "wordlyrics-library", track);
            if (preview == "wordlyrics-custom" && wordDialog.DataContext is WordLyricsViewModel edit)
            { edit.ActionIndex = 3; edit.Words = "[00:01.00]A synthetic line for the visual check\n[00:04.00]The next line keeps its timing guide"; }
            DialogHelper.SizeToOwner(wordDialog, owner); await wordDialog.ShowDialog(owner); return;
        }
        if (!preview.StartsWith("telegram-", StringComparison.Ordinal)) return;
        var dialog = new TelegramSignInDialog(new PreviewAccount(), _ => { });
        var vm = (TelegramSignInViewModel)dialog.DataContext!;
        vm.Step = preview switch
        {
            "telegram-code" => TelegramSignInViewModel.LoginStep.Code,
            "telegram-password" => TelegramSignInViewModel.LoginStep.Password,
            "telegram-connected" => TelegramSignInViewModel.LoginStep.SignedIn,
            _ => TelegramSignInViewModel.LoginStep.Phone,
        };
        if (preview == "telegram-error") vm.Error = TelegramSignInViewModel.Explain(new IOException());
        if (preview == "telegram-busy") vm.IsBusy = true;
        DialogHelper.SizeToOwner(dialog, owner);
        await dialog.ShowDialog(owner);
    }

    private static async Task ShowPlaylistImportAsync(Window owner)
    {
        var folder = Directory.CreateTempSubdirectory("NoctisPlus-visual-").FullName;
        var library = new PreviewLibrary();
        for (var n = 0; n < 2; n++)
        {
            var path = Path.Combine(folder, $"synthetic-{n}.txt");
            File.WriteAllText(path, "Synthetic visual fixture, not audio.");
            library.Songs.Add(new(Guid.NewGuid(), "Test artist", n == 0 ? "Night Drive" : "After Hours", path, TimeSpan.FromMinutes(3)));
        }
        var url = "https://open.spotify.com/playlist/0000000000000000000001";
        var entries = new[] { "Night Drive", "New Horizon", "After Hours" }.Select((title, n) => new CatalogEntry
        {
            Position = n + 1, Uri = "spotify:track:" + n.ToString("D22"),
            Request = new MusicRequest(title, "Test artist", 180),
        }).ToList();
        var job = new SpotifyPlaylist
        {
            Url = url, Created = true,
            Manifest = new() { Url = url, Name = "Night drive", Total = 3, CompleteIds = true, Entries = entries },
            Matches = new() { [1] = library.Songs[0].Id, [3] = library.Songs[1].Id },
            Applied = library.Songs.Select(s => s.Id).ToList(),
            Status = "2 of 3 songs matched locally · 1 missing",
        };
        var file = Path.Combine(folder, "imports.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new[] { job }));
        using var downloads = new Downloads(_ => { });
        using var imports = new SpotifyPlaylists(file, library, downloads,
            request => throw new InvalidOperationException("Visual preview never contacts download bots."));
        var dialog = new StreamingPlaylistImportDialog(imports, () => folder, () => false,
            async parent => await TelegramSignInDialog.ShowAsync(parent, new PreviewAccount(), _ => { }));
        DialogHelper.SizeToOwner(dialog, owner);
        await dialog.ShowDialog(owner);
        // The folder contains synthetic fixtures only. It stays in Temp for evidence/debugging.
    }

    private sealed class PreviewLibrary : IPlaylistLibrary
    {
        internal List<LocalSong> Songs { get; } = [];
        public Task<IReadOnlyList<LocalSong>> SnapshotAsync() => Task.FromResult<IReadOnlyList<LocalSong>>(Songs);
        public Task ApplyAsync(Guid id, string name, string url, IReadOnlyList<Guid> tracks, bool create, IReadOnlyList<Guid> expected, IReadOnlyList<Guid>? remappedExpected = null)
            => Task.CompletedTask;
    }

    private sealed class RecoveryQueue : Noctis.Services.Search.IDownloadQueue
    {
        public IReadOnlyList<Noctis.Services.Search.DownloadEntry> Snapshot() =>
            [new(new object(), "Synthetic saved song", "Test artist", Noctis.Services.Search.OnlineDownloadState.Saved, 1, "Downloaded", false, false, Path.Combine(Path.GetTempPath(), "synthetic-moved.wav")),
             new(new object(), "Waiting song", "Test artist", Noctis.Services.Search.OnlineDownloadState.Queued, 0, "TELEGRAM_CONNECTION_FAILED: Check your VPN or system proxy · retry at 23:00", false, false)];
        public bool IsPaused => false;
        public event EventHandler? Changed { add { } remove { } }
        public void SetPaused(bool value) { }
        public void RetryFailed() { }
        public void StartHeld() { }
        public void Clear() { }
        public void DownloadNext(Noctis.Services.Search.DownloadEntry entry) { }
        public void Remove(Noctis.Services.Search.DownloadEntry entry) { }
    }
    private sealed class PreviewAccount : ITelegramSignIn
    {
        public bool IsLoggedIn => false;
        public string SignedInAs => "Test account";
        public int ApiId => 12345;
        public string ApiHash => "0123456789abcdef0123456789abcdef";
        public bool HasCredentials => true;
        public string DefaultsError => "";
        public Task<string?> StartLoginAsync(int id, string hash, string phone) => Task.FromResult<string?>("verification_code");
        public Task<string?> ContinueLoginAsync(string answer) => Task.FromResult<string?>(null);
        public Task LogOutAsync() => Task.CompletedTask;
    }
}
#endif
