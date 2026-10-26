using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeMusicFinder;
using Noctis.Helpers;
using Noctis.Services.Search;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

public class RecommendMenuTests
{
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(3)]
    public void FirstOpen_AndLateRecycledDataContext_WireTheActualCardMenu(int artists)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        using var vm = new ForYouShelfViewModel();
        ForYouPick Pick(int count) => new(new(new(new CatalogSong("deezer:1", "Song",
            Enumerable.Range(1, count).Select(i => new CatalogArtist("deezer:" + i, "Artist " + i)).ToList(), 180, true), null), 1, "Test"),
            new SearchSongItem(new OnlineSong("deezer:1", "Song", "Artist 1"))) { Shelf = vm };
        vm.Picks.Add(Pick(artists));
        var view = new ForYouShelfView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 700 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var tile = view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("foryou-tile"));
            var menu = tile.ContextMenu!;
            menu.Open(tile); // Direct Open doesn't raise Opening: the owner's dots button uses this exact path.
            Dispatcher.UIThread.RunJobs();
            var block = menu.Items.OfType<MenuItem>().Single(i => RecommendMenu.GetEntry(i) == "pick");
            Check(artists);
            Assert.All(menu.Items.OfType<MenuItem>().Where(i => RecommendMenu.GetEntry(i) is null), i => Assert.NotNull(i.Command));
            menu.Close(); Dispatcher.UIThread.RunJobs();
            // First attach may precede the popup data binding; later recycling must not keep the previous song's artists.
            tile.DataContext = null;
            menu.Open(tile); Dispatcher.UIThread.RunJobs();
            tile.DataContext = Pick(artists == 1 ? 3 : 1); Dispatcher.UIThread.RunJobs();
            Check(artists == 1 ? 3 : 1);
            menu.Close();
            void Check(int count)
            {
                Assert.True(block.IsVisible);
                if (count == 1)
                {
                    Assert.Same(vm.BlockArtistCommand, block.Command);
                    Assert.Equal("Artist 1", block.CommandParameter);
                    Assert.Empty(block.Items);
                }
                else
                {
                    Assert.Null(block.Command);
                    var choices = block.Items.OfType<MenuItem>().ToArray();
                    Assert.Equal(count, choices.Length);
                    for (int i = 0; i < count; i++)
                    {
                        Assert.Equal("Artist " + (i + 1), choices[i].Header);
                        Assert.Same(vm.BlockArtistCommand, choices[i].Command);
                        Assert.Equal(choices[i].Header, choices[i].CommandParameter);
                    }
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SharedNotice_UndoRestoresOnlyTheChosenArtist_ExpiresAndCannotAffectAnotherSource()
    {
        var previous = OnlineMusicSearch.Current;
        var source = new Source(); OnlineMusicSearch.Current = source;
        Action? expire = null;
        var notice = new RecommendationNoticeViewModel((_, action) => { expire = action; return new Cancel(); });
        try
        {
            source.SetArtistBlocked("Guest", true);
            source.SetArtistBlocked("Main", true);
            notice.Show(source, "Main", true);
            Assert.True(notice.IsShown && notice.HasUndo);
            Assert.Equal("Won't recommend · Main", notice.Text);
            notice.UndoCommand.Execute(null);
            Assert.False(source.IsArtistBlocked("Main"));
            Assert.True(source.IsArtistBlocked("Guest"));
            Assert.False(notice.IsShown);
            source.SetArtistBlocked("Main", true);
            notice.Show(source, "Main", true);
            expire!(); notice.UndoCommand.Execute(null);
            Assert.True(source.IsArtistBlocked("Main"));
            notice.Show(source, "Main", true);
            OnlineMusicSearch.Current = new Source();
            notice.UndoCommand.Execute(null);
            Assert.True(source.IsArtistBlocked("Main"));
        }
        finally { notice.Hide(); OnlineMusicSearch.Current = previous; }
    }

    private sealed class Cancel : IDisposable { public void Dispose() { } }
    private sealed class Source : IOnlineMusicSearch
    {
        private readonly HashSet<string> _blocked = [];
        public bool CanBlockArtists => true;
        public bool IsArtistBlocked(string artist) => _blocked.Contains(artist);
        public bool SetArtistBlocked(string artist, bool blocked) => blocked ? _blocked.Add(artist) : _blocked.Remove(artist);
        public async IAsyncEnumerable<OnlineSearchResult> SearchAsync(string query, [EnumeratorCancellation] CancellationToken ct) { await Task.Yield(); yield break; }
        public Task<string?> ArtworkAsync(string url, CancellationToken ct) => Task.FromResult<string?>(null);
        public string? CachedArtwork(string url) => null;
        public void WarmUp() { }
        public void Retry() { }
        public string Route => "synthetic";
        public bool CanDownload => false;
        public void RequestSignIn() { }
        public OnlineDownloadStatus StatusOf(OnlineSong song) => OnlineDownloadStatus.None;
        public event EventHandler? DownloadsChanged { add { } remove { } }
        public Task<string> DownloadAsync(IReadOnlyList<OnlineSong> songs, bool now, CancellationToken ct) => throw new NotSupportedException();
        public IDownloadQueue Downloads => throw new NotSupportedException();
        public void OpenArtist(OnlineArtist artist) { }
        public Task OpenAlbumAsync(OnlineAlbum album, CancellationToken ct) => Task.CompletedTask;
    }
}
