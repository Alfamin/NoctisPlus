using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace FreeMusicFinder;

/// <summary>A small, optional portrait. Identity and navigation never depend on its availability.</summary>
internal sealed class ArtistPicture : UserControl, IDisposable
{
    private readonly Image _image;
    private Bitmap? _bitmap;
    private CancellationTokenSource? _stop;
    private bool _started, _disposed;
    private readonly string? _artwork;
    private readonly int _decodeWidth;
    public ArtistPicture(CatalogArtist artist, double size = 64, string? artwork = null)
    {
        _artwork = artwork;
        _decodeWidth = (int)Math.Clamp(size * 2, 96, 640);
        Width = Height = size;
        var initials = string.Concat(artist.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => System.Globalization.StringInfo.GetNextTextElement(w))).ToUpperInvariant();
        var grid = new Grid();
        grid.Children.Add(new Border { CornerRadius = new(artwork is null ? size / 2 : 6), Background = new SolidColorBrush(Color.Parse("#303440")), Child = new TextBlock { Text = artwork is null ? initials : "♫", Foreground = Brushes.White, FontSize = size * .3, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        _image = new Image { Width = size, Height = size, Stretch = Stretch.UniformToFill, Clip = artwork is null ? new EllipseGeometry(new Rect(0, 0, size, size)) : new RectangleGeometry(new Rect(0, 0, size, size)), IsVisible = false };
        grid.Children.Add(_image); Content = grid;
    }
    public async void Load(ArtistCatalog catalog, CatalogArtist artist, CancellationToken ct)
    {
        if (_started || _disposed || ct.IsCancellationRequested) return; _started = true;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct); ct = _stop.Token;
        try
        {
            async Task Show(string path) => await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || ct.IsCancellationRequested) return;
                using var input = File.OpenRead(path); var next = Bitmap.DecodeToWidth(input, _decodeWidth);
                var previous = _bitmap; _bitmap = next; _image.Source = next; _image.IsVisible = true; previous?.Dispose();
            });
            if (_artwork is null) await catalog.PortraitAsync(artist, ct, Show);
            else if (_artwork.Length > 0) await catalog.ProgressiveArtworkAsync(_artwork, Show, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TimeoutException or SourcesWaitingException or ArgumentException or OperationCanceledException or System.Text.RegularExpressions.RegexMatchTimeoutException) { }
    }
    public void Dispose() { _disposed = true; _stop?.Cancel(); _stop?.Dispose(); _stop = null; _image.Source = null; _bitmap?.Dispose(); _bitmap = null; }
}
