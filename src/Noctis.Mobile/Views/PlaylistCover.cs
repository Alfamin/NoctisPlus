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
        SourcePath = (DataContext as PlaylistRow)?.ArtworkPath;
        if (SourcePath is not null || DataContext is not PlaylistRow { CoverSource: { } source, CoverUrl.Length: > 0 } row) return;
        var stop = _stop = new();
        try
        {
            var path = source.CachedArtwork(row.CoverUrl) ?? await source.ArtworkAsync(row.CoverUrl, stop.Token);
            if (!stop.IsCancellationRequested && ReferenceEquals(DataContext, row)) SourcePath = path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
