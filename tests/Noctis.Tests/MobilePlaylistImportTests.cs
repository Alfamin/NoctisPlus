using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services.Search;
using Xunit;

namespace Noctis.Tests;

public class MobilePlaylistImportTests
{
    private sealed class Imports : IOnlinePlaylistImports
    {
        public readonly Dictionary<Guid, OnlineImportedPlaylist> Pages = new();
        public Func<string, CancellationToken, Task>? Import;
        public int ImportsCalled, Downloads;
        public event Action? Changed;
        public void Publish() => Changed?.Invoke();
        public IReadOnlyList<OnlineImportJob> Snapshot() => Pages.Values.Select(p => p.Import).ToArray();
        public OnlineImportedPlaylist? Read(Guid id) => Pages.GetValueOrDefault(id);
        public Task ImportAsync(string links, CancellationToken ct) { ImportsCalled++; return Import?.Invoke(links, ct) ?? Task.CompletedTask; }
        public Task CheckLibraryAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<int> DownloadMissingAsync(Guid id, CancellationToken ct) { Downloads++; return Task.FromResult(2); }
        public Task RemoveAsync(Guid id, CancellationToken ct) { Pages.Remove(id); Publish(); return Task.CompletedTask; }
    }

    [AvaloniaFact]
    public async Task EmptyPlaylistsOfferNativeImport_MultilineEnterDoesNotSubmit_AndImportNeverStartsAudio()
    {
        var imports = new Imports(); var source = new MobileCatalogAlbumTests.Source { PlaylistImports = imports };
        using var rig = MobileFixtures.MakeRig(onlineSearch: source);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Playlists);
        var root = rig.Shell.PlaylistsRoot;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            MobileFixtures.Land(window); Assert.True(root.CanImport); Assert.True(root.HasTools); Assert.False(root.HasPlaylists);
            root.ImportPlaylistsCommand.Execute(null);
            MobileFixtures.Land(window);
            var prompt = Assert.IsType<PromptViewModel>(rig.Shell.Prompt);
            var field = MobileFixtures.Named<TextBox>(view, "PromptField");
            Assert.True(prompt.MultilineField); Assert.True(field.AcceptsReturn);
            prompt.FieldText = "https://open.spotify.com/playlist/1234567890123456789012\nhttps://www.deezer.com/playlist/123";
            field.RaiseEvent(new KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Equal(0, imports.ImportsCalled); Assert.Same(prompt, rig.Shell.Prompt);
            imports.Import = async (links, ct) =>
            {
                Assert.Contains('\n', links);
                var playlist = await rig.Shell.Library.CreatePlaylistAsync("Imported");
                var job = new OnlineImportJob(playlist.Id, playlist.Name, "https://www.deezer.com/playlist/123", 0, 3, "", true, false);
                imports.Pages[playlist.Id] = new(job, Enumerable.Range(1, 3).Select(i => new OnlinePlaylistEntry(i, new($"deezer:{i}", $"Song {i}", "Artist"), "")).ToArray()); imports.Publish();
            };
            await prompt.Actions[0].RunCommand.ExecuteAsync(null);
            Assert.Equal(1, imports.ImportsCalled); Assert.Null(rig.Shell.Prompt);
            Assert.Equal("3 songs", Assert.Single(root.Rows).Subtitle); Assert.Equal("Playlists imported", root.ImportMessage);
            Assert.Empty(rig.Library.TrackList); Assert.Empty(source.Saves); Assert.Equal(0, source.Prepares); Assert.Equal(0, imports.Downloads);
            rig.Shell.OpenPlaylistCommand.Execute(root.Playlists.Single());
            var page = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
            Assert.Equal(3, page.Songs.Count); Assert.All(page.Songs, t => Assert.True(t.IsPendingStream));
            Assert.Empty(root.Playlists.Single().TrackIds); Assert.Equal(0, source.Prepares); page.OnClosed();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ImportedPlaylistThumbnailArrivesWithoutReopening_AndCustomCoverWinsOnReuse()
    {
        var answer = new TaskCompletionSource<string?>(); var asks = 0;
        var source = new MobileCatalogAlbumTests.Source { Artwork = (_, _) => { asks++; return answer.Task; } };
        using var rig = MobileFixtures.MakeRig(onlineSearch: source);
        var row = new PlaylistRow(new Playlist { Name = "Remote" }, null, 251, source, "https://example.invalid/cover.jpg");
        var cover = new PlaylistCover { DataContext = row };
        var window = new Window { Content = cover, Width = 100, Height = 100 }; window.Show();
        try
        {
            MobileFixtures.Land(window); Assert.Equal(1, asks); Assert.Null(cover.SourcePath);
            answer.SetResult("import-cover.jpg"); await Task.Yield(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("import-cover.jpg", cover.SourcePath);
            cover.DataContext = row with { ArtworkPath = "custom.jpg" };
            Assert.Equal("custom.jpg", cover.SourcePath); Assert.Equal(1, asks);
            var late = new TaskCompletionSource<string?>(); source.Artwork = (_, _) => late.Task;
            cover.DataContext = row;
            cover.DataContext = row with { ArtworkPath = "custom2.jpg" };
            late.SetResult("wrong-cover.jpg"); await Task.Yield(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("custom2.jpg", cover.SourcePath);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ColdOnlineArtwork_NotifiesAndUpdatesTheMountedRowWithoutNavigating()
    {
        var answer = new TaskCompletionSource<string?>();
        var source = new MobileCatalogAlbumTests.Source { Artwork = (_, _) => answer.Task };
        using var rig = MobileFixtures.MakeRig(onlineSearch: source);
        var online = OnlineStandIn.Create(source, new("deezer:1", "Online", "Artist", ArtworkUrl: "https://example.invalid/cover.jpg"), false, "Test");
        rig.Shell.Navigate(new SongListPageViewModel(rig.Shell, "Test", () => [online]));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            MobileFixtures.Land(window);
            var row = MobileFixtures.Find<SongRow>(view);
            var cover = MobileFixtures.Find<Noctis.Controls.CachedImage>(row);
            Assert.Null(cover.SourcePath);
            answer.SetResult("arrived-cover.jpg");
            await Task.Yield(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("arrived-cover.jpg", online.AlbumArtworkPath);
            Assert.Equal("arrived-cover.jpg", cover.SourcePath);
        }
        finally { window.Close(); rig.Shell.CurrentPage?.OnClosed(); }
    }

    [AvaloniaFact]
    public async Task LateOnlineRowArtwork_CannotChangeARecycledLocalRow()
    {
        var answer = new TaskCompletionSource<string?>();
        var source = new MobileCatalogAlbumTests.Source { Artwork = (_, _) => answer.Task };
        using var rig = MobileFixtures.MakeRig(onlineSearch: source);
        var online = Noctis.Services.Search.OnlineStandIn.Create(source, new("deezer:1", "Online", "Artist", ArtworkUrl: "https://example.invalid/cover.jpg"), false, "Test");
        rig.Shell.Navigate(new SongListPageViewModel(rig.Shell, "Test", () => [online]));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            MobileFixtures.Land(window);
            var row = MobileFixtures.Find<SongRow>(view);
            var local = MobileFixtures.Song("Local"); row.DataContext = local;
            answer.SetResult("stale-cover.jpg"); await Task.Yield(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.NotEqual("stale-cover.jpg", local.AlbumArtworkPath); Assert.Same(local, row.DataContext);
        }
        finally { window.Close(); rig.Shell.CurrentPage?.OnClosed(); }
    }

    [AvaloniaFact]
    public async Task ImportedPlaylistShowsOwnedMissingDuplicateAndUnavailablePositions_AndPlaysFullContextWithoutPhantomFiles()
    {
        var owned = MobileFixtures.Song("Owned"); var playlist = new Playlist { Name = "Mix", TrackIds = [owned.Id] };
        var imports = new Imports();
        imports.Pages[playlist.Id] = new(new(playlist.Id, playlist.Name, "https://www.deezer.com/playlist/123", 1, 4, "", true, false), [
            new(1, new("deezer:1", "Owned", "Artist", LocalTrackId: owned.Id), ""),
            new(2, new("deezer:2", "Missing", "Artist"), ""), new(3, new("deezer:2", "Missing", "Artist"), ""), new(4, null, "Unavailable in this catalog")]);
        var source = new MobileCatalogAlbumTests.Source { PlaylistImports = imports };
        using var rig = MobileFixtures.MakeRig(tracks: [owned], onlineSearch: source, seed: p => p.SavePlaylistsAsync([playlist]));
        rig.Shell.OpenPlaylistCommand.Execute(playlist);
        var page = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        try
        {
            Assert.True(page.IsImported); Assert.True(page.HasMissingImported); Assert.Equal(4, page.Songs.Count);
            Assert.Same(owned, page.Songs[0]); Assert.True(page.Songs[1].IsPendingStream); Assert.Equal(page.Songs[1].Id, page.Songs[2].Id);
            Assert.True(page.Songs[3].IsFileMissing); Assert.Single(rig.Library.TrackList); Assert.Equal(0, source.Prepares);
            var stable = page.Songs[1]; imports.Publish(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Assert.Same(stable, page.Songs[1]);
            page.PlayAllCommand.Execute(null);
            Assert.Same(owned, rig.Shell.Player.CurrentTrack); Assert.Equal(2, rig.Shell.Player.UpNext.Count);
            Assert.All(rig.Shell.Player.UpNext, t => Assert.True(t.IsPendingStream));
            await page.DownloadMissingCommand.ExecuteAsync(null); Assert.Equal(1, imports.Downloads);
            page.OpenSongMenuCommand.Execute(page.Songs[3]); Assert.Equal("Unavailable song", rig.Shell.OptionsMenu?.Title);
            Assert.Equal(new[] { owned.Id }, playlist.TrackIds);
            rig.Shell.Player.ClearUpNextCommand.Execute(null);
            var sheet = ContextSheetViewModel.ForPlaylist(rig.Shell, playlist);
            Assert.Equal("4 songs", sheet.Subtitle);
            Assert.False(sheet.CanUseSavedSongs); Assert.False(sheet.CanFavourite);
            sheet.ShowPlaylistsCommand.Execute(null); Assert.False(sheet.IsPickingPlaylist);
            sheet.AddToQueueCommand.Execute(null);
            Assert.Equal(3, rig.Shell.Player.UpNext.Count); // full playable playlist, including both missing positions
        }
        finally { page.OnClosed(); }
    }
}
