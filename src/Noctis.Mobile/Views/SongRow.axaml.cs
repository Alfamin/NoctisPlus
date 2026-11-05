using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Noctis.Mobile.Views;

public partial class SongRow : UserControl
{
    /// <summary>What the row's ⋯ opens, given the song: the list's own song menu (a playlist's
    /// page adds Remove from Playlist), or the shell's.</summary>
    public static readonly StyledProperty<ICommand?> MenuCommandProperty =
        AvaloniaProperty.Register<SongRow, ICommand?>(nameof(MenuCommand));

    public SongRow()
    {
        InitializeComponent();
    }

    public ICommand? MenuCommand { get => GetValue(MenuCommandProperty); set => SetValue(MenuCommandProperty, value); }

    private CancellationTokenSource? _artStop;
    private bool _attached;
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; LoadOnlineArtwork(); }
    protected override void OnDataContextChanged(EventArgs e)
    { base.OnDataContextChanged(e); if (_attached) LoadOnlineArtwork(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; _artStop?.Cancel(); base.OnDetachedFromVisualTree(e); }

    private async void LoadOnlineArtwork()
    {
        _artStop?.Cancel();
        if (DataContext is not Noctis.Models.Track { PendingStream: Noctis.ViewModels.PendingStream pending } track
            || pending.Song.ArtworkUrl.Length == 0 || File.Exists(track.AlbumArtworkPath)) return;
        var stop = _artStop = new();
        try
        {
            var path = pending.Source.CachedArtwork(pending.Song.ArtworkUrl)
                ?? await pending.Source.ArtworkAsync(pending.Song.ArtworkUrl, stop.Token);
            if (!stop.IsCancellationRequested && ReferenceEquals(DataContext, track) && path is not null) track.AlbumArtworkPath = path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
