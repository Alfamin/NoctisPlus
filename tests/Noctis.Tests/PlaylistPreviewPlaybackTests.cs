using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FreeMusicFinder;
using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #182: a found playlist plays as a playlist before it is saved, too.</summary>
public class PlaylistPreviewPlaybackTests
{
    private sealed class Preview : IDisposable
    {
        private readonly IOnlineMusicSearch? _previous = OnlineMusicSearch.Current;
        internal readonly QueueStreamingTests.Source Source = new() { Ahead = 0 };
        internal readonly TestPersistenceService Persistence = new();
        internal readonly FakeLibraryService Library = new();
        internal readonly Track Owned = Local("Owned");
        internal readonly PlayerViewModel Player;
        internal readonly SidebarViewModel Sidebar;
        internal readonly OnlinePlaylist Found = new("spotify:preview-182", "Found playlist", "Maker", "https://open.spotify.com/playlist/preview-182");
        internal readonly PlaylistViewModel Page;

        internal Preview()
        {
            OnlineMusicSearch.Current = Source;
            Library.TrackList.Add(Owned);
            Player = new(new FakeAudioPlayer(), Library, Persistence, new FakeAnimatedCoverService()) { QueueSettle = TimeSpan.FromMinutes(1) };
            Sidebar = new(Persistence, Library);
            Page = Reopen();
        }

        internal PlaylistViewModel Reopen() => PlaylistViewModel.ForPreview(Found, Player, Library, Persistence, Sidebar);

        internal async Task Load(bool onlyRemote = false)
        {
            CatalogSong a = new("spotify:a", "First", [new("", "Singer")], 180, null);
            CatalogSong b = new("spotify:b", "Last", [new("", "Singer")], 180, null);
            Page.ApplyPreview(onlyRemote
                ? [new(1, a, null, ""), new(2, b, null, "")]
                : [new(1, a, null, ""), new(2, null, Owned.Id, ""), new(3, b, null, ""),
                   new(4, b, null, ""), new(5, null, null, "Unavailable")]);
            await Until(() => onlyRemote || Page.Tracks.Count == 1);
        }

        public void Dispose()
        {
            Player.ClearQueue();
            Page.Dispose();
            OnlineMusicSearch.Current = _previous;
            Persistence.Dispose();
        }
    }

    private static Track Local(string title) => new() { Id = Guid.NewGuid(), Title = title, Artist = "Synthetic", FilePath = "C:/synthetic/" + title + ".flac" };
    private static string[] Ahead(PlayerViewModel player) => player.UpNext.Select(t => t.Title).ToArray();
    private static async Task Until(Func<bool> done)
    {
        var end = Environment.TickCount64 + 3000;
        do { Dispatcher.UIThread.RunJobs(); if (done()) return; await Task.Delay(5); } while (Environment.TickCount64 < end);
        Assert.True(done());
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RowPlay_ReplacesOldOrClearedQueue_FromSelectedRow(bool ownedRow, bool clearFirst)
    {
        using var f = new Preview(); await f.Load();
        f.Player.ReplaceQueueAndPlay([Local("Old"), Local("Old next")], 0);
        if (clearFirst) f.Player.ClearQueue();
        if (ownedRow) f.Page.PlayFromCommand.Execute(f.Owned);
        else await f.Page.PlayCatalogSongCommand.ExecuteAsync((ArtistCatalogSong)f.Page.Rows[0]);

        Assert.Equal(ownedRow ? new[] { "Last", "Last" } : new[] { "Owned", "Last", "Last" }, Ahead(f.Player));
        if (ownedRow) Assert.Same(f.Owned, f.Player.CurrentTrack);
        else
        {
            Assert.Equal([("spotify:a", false)], f.Source.Asked);
            var ready = Local("First"); f.Source.Pending["spotify:a"].SetResult(ready);
            await Until(() => ReferenceEquals(ready, f.Player.CurrentTrack));
        }
        Assert.Empty(f.Sidebar.Playlists); // playing never saves/imports the found playlist
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeaderPlay_UsesSourceOrder_IncludingMissingSongsAndDuplicates(bool onlyRemote)
    {
        using var f = new Preview(); await f.Load(onlyRemote);
        Assert.True(f.Page.ShowPlayActions);
        Assert.True(f.Page.ShowStreamToggle);
        f.Player.ReplaceQueueAndPlay([Local("Old"), Local("Old next")], 0);
        f.Page.PlayAllCommand.Execute(null);
        Assert.Equal([("spotify:a", false)], f.Source.Asked);
        Assert.Equal(onlyRemote ? new[] { "Last" } : new[] { "Owned", "Last", "Last" }, Ahead(f.Player));
    }

    [AvaloniaFact]
    public async Task LocalOnlyChoice_IsRememberedOnReopen_AndGlobalOffStillApplies()
    {
        using var f = new Preview(); await f.Load();
        f.Page.StreamMissing = false;
        Assert.Equal(new[] { f.Owned }, f.Page.PlaybackList());
        using var reopened = f.Reopen();
        Assert.NotEqual(f.Page.PlaylistId, reopened.PlaylistId);
        Assert.False(reopened.StreamMissing);
        reopened.StreamMissing = true;
        Assert.True(f.Page.StreamMissing);
        f.Player.StreamMissingSongs = false;
        Assert.Equal(new[] { f.Owned }, f.Page.PlaybackList());
        f.Page.PlayAllCommand.Execute(null);
        Assert.Same(f.Owned, f.Player.CurrentTrack);
        Assert.Empty(f.Source.Asked);
    }

    [AvaloniaFact]
    public async Task ExplicitAppendAndPlayNext_KeepExistingQueue_AndShuffleContainsWholePreview()
    {
        using var f = new Preview(); await f.Load();
        f.Player.ReplaceQueueAndPlay([Local("Old"), Local("Old next")], 0);
        f.Page.AddAllToQueueCommand.Execute(null);
        Assert.Equal(new[] { "Old next", "First", "Owned", "Last", "Last" }, Ahead(f.Player));
        f.Player.ReplaceQueueAndPlay([Local("Old"), Local("Old next")], 0);
        f.Page.PlayNextAllCommand.Execute(null);
        Assert.Equal(new[] { "First", "Owned", "Last", "Last", "Old next" }, Ahead(f.Player));
        f.Page.ShuffleAllCommand.Execute(null);
        // The selected first song is either playing locally or being prepared online.
        Assert.Equal(3, f.Player.UpNext.Count);
        Assert.DoesNotContain(f.Player.UpNext, t => t.Title.StartsWith("Old"));
        Assert.True(f.Player.IsShuffleEnabled);
    }
}
