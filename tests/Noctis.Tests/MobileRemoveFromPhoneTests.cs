using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>#306: Remove from Phone, in a song's menu and in Select mode.</summary>
public class MobileRemoveFromPhoneTests
{
    private sealed class Remover : ISongFileRemover
    {
        public readonly HashSet<string> Refuse = new();
        public readonly List<string> Tried = new();
        public bool TryDelete(string path)
        {
            Tried.Add(path);
            return !Refuse.Contains(path) && new PlainSongFileRemover().TryDelete(path);
        }
    }

    private static Track OnDisk(string dir, string title)
    {
        var path = Path.Combine(dir, title + ".mp3");
        File.WriteAllBytes(path, [1, 2, 3]);
        return new Track { Id = Guid.NewGuid(), Title = title, Artist = "Artist", AlbumArtist = "Artist", Album = "Album", FilePath = path, Duration = TimeSpan.FromSeconds(90) };
    }

    [Fact]
    public async Task RemoveSongs_DeletesTheFiles_TakesThemOutOfTheLibraryAndThePlaylists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = OnDisk(dir, "A"); var b = OnDisk(dir, "B"); var c = OnDisk(dir, "C");
            using var rig = MobileFixtures.MakeRig([a, b, c], songFiles: new Remover());
            var playlist = await rig.Shell.Library.CreatePlaylistAsync("Mix");
            await rig.Shell.Library.AddToPlaylistAsync(playlist, [a, b, c]);

            var (deleted, kept) = await rig.Shell.RemoveSongsAsync([a, b]);

            Assert.Equal((2, 0), (deleted, kept));
            Assert.False(File.Exists(a.FilePath)); Assert.False(File.Exists(b.FilePath)); Assert.True(File.Exists(c.FilePath));
            Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), rig.Library.RemovedBatchIds.OrderBy(x => x));
            Assert.Empty(rig.Library.HiddenList);
            Assert.Equal(new[] { c.Id }, (await rig.Persistence.LoadPlaylistsAsync()).Single(p => p.Id == playlist.Id).TrackIds);
            Assert.Contains("2 songs removed", rig.Shell.Player.Notice);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task AFileThePhoneMayNotDelete_Stays_AndItsSongIsHiddenInstead()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = OnDisk(dir, "A"); var b = OnDisk(dir, "B");
            var remover = new Remover();
            remover.Refuse.Add(b.FilePath); // a folder shared read-only
            using var rig = MobileFixtures.MakeRig([a, b], songFiles: remover);

            var (deleted, kept) = await rig.Shell.RemoveSongsAsync([a, b]);

            Assert.Equal((1, 1), (deleted, kept));
            Assert.False(File.Exists(a.FilePath));
            Assert.True(File.Exists(b.FilePath));
            Assert.Equal(new[] { a.Id }, rig.Library.RemovedBatchIds);
            Assert.Equal(new[] { b.FilePath }, rig.Library.HiddenList);
            Assert.Contains("1 removed", rig.Shell.Player.Notice);
            Assert.Contains("hidden", rig.Shell.Player.Notice);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task TheMenuAndSelectMode_AskFirst_ThenRemove()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = OnDisk(dir, "A"); var b = OnDisk(dir, "B");
            var stream = new Track { Id = Guid.NewGuid(), Title = "Online", PendingStream = new object() };
            using var rig = MobileFixtures.MakeRig([a, b], songFiles: new Remover());

            // A song's menu: the row is there for a song on the phone, not for one that is only waiting to be fetched.
            var menu = ContextSheetViewModel.ForTrack(rig.Shell, a);
            Assert.True(menu.CanRemoveFromPhone);
            Assert.False(ContextSheetViewModel.ForTrack(rig.Shell, stream).CanRemoveFromPhone);
            menu.RemoveFromPhoneCommand.Execute(null);
            var prompt = Assert.IsType<PromptViewModel>(rig.Shell.Prompt);
            Assert.Contains("\"A\"", prompt.Title);
            Assert.True(File.Exists(a.FilePath)); // nothing is deleted before the owner agrees
            var remove = Assert.Single(prompt.Actions);
            Assert.True(remove.IsDestructive);

            // Select mode: the bar's button asks about the ticked songs and ends the mode afterwards.
            var selection = new TrackSelection(rig.Shell, () => [a, b], () => "Songs");
            selection.Begin(a); selection.Toggle(b);
            Assert.True(selection.CanRemoveFromPhone);
            selection.RemoveFromPhoneCommand.Execute(null);
            var second = Assert.IsType<PromptViewModel>(rig.Shell.Prompt);
            Assert.Contains("2 songs", second.Title);
            await second.Actions[0].RunCommand.ExecuteAsync(null);
            Assert.False(File.Exists(a.FilePath)); Assert.False(File.Exists(b.FilePath));
            Assert.False(selection.IsActive);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}