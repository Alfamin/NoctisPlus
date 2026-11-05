using Avalonia;
using Noctis.Controls;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>Fetch only attached playlist thumbnails; never materialize the playlist's tracks.</summary>
public sealed class PlaylistCover : CachedImage
{
    private CancellationTokenSource? _stop;
    private bool _attached;
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; LoadCover(); }
    protected override void OnDataContextChanged(EventArgs e)
    { base.OnDataContextChanged(e); if (_attached) LoadCover(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; _stop?.Cancel(); base.OnDetachedFromVisualTree(e); }

    private async void LoadCover()
    {
        _stop?.Cancel();
        var row = DataContext;
        var (local, source, url) = row switch
        {
            PlaylistRow playlist => (playlist.ArtworkPath, playlist.CoverSource, playlist.CoverUrl),
            RailItem rail => (rail.ArtworkPath, rail.CoverSource, rail.CoverUrl),
            _ => (null, null, ""),
        };
        SourcePath = local;
        if (local is not null || source is null || url.Length == 0) return;
        var stop = _stop = new();
        try
        {
            var path = source.CachedArtwork(url) ?? await source.ArtworkAsync(url, stop.Token);
            if (!stop.IsCancellationRequested && ReferenceEquals(DataContext, row)) SourcePath = path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
