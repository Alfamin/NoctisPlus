using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>All albums, two per row. Rows (not a wrap panel) so the list virtualises — the
/// same reason the desktop grid is rows of albums (AlbumRow).</summary>
public sealed partial class AlbumGridPageViewModel : MobilePage
{
    internal const int Columns = 2;

    public AlbumGridPageViewModel(ShellViewModel shell)
    {
        Shell = shell;
        Shell.Library.Refreshed += OnRefreshed;
        Refresh();
    }

    public ShellViewModel Shell { get; }

    public override string Title => Loc.T("Nav.Albums");

    public BulkObservableCollection<AlbumRow> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlbums))]
    private int _albumCount;

    public bool HasAlbums => AlbumCount > 0;

    public void Refresh()
    {
        var albums = Shell.Library.Service.Albums
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        AlbumCount = albums.Count;
        // A rescan republishes every 1.5 s with rebuilt Album objects: rebuilding unchanged rows
        // re-realised every tile and blanked the covers. A kept tile opens the current album
        // (ShellViewModel.OpenAlbum resolves it by id).
        if (SameTiles(Rows.SelectMany(r => r.Albums).ToList(), albums)) return;
        Rows.ReplaceAll(albums.Chunk(Columns).Select(chunk => new AlbumRow { Albums = chunk.ToList() }));
    }

    /// <summary>What a tile shows (AlbumTile: cover, name, artist), in the same order.</summary>
    private static bool SameTiles(IReadOnlyList<Album> shown, IReadOnlyList<Album> next)
    {
        if (shown.Count != next.Count) return false;
        for (var i = 0; i < next.Count; i++)
        {
            var (a, b) = (shown[i], next[i]);
            if (a.Id != b.Id || a.Name != b.Name || a.Artist != b.Artist || a.ArtworkPath != b.ArtworkPath) return false;
        }
        return true;
    }

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    public override void OnClosed() => Shell.Library.Refreshed -= OnRefreshed;
}
