using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.Helpers;
using Noctis.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace Noctis.Views;

public partial class DownloadsView : UserControl
{
    private TrackContextMenuBuilder? _menu;
    private string? _menuCulture;
    private Control? _menuOwner;
    public DownloadsView()
    {
        InitializeComponent();
    }

    private void OnFinishedDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: DownloadRow { IsActionable: true } row } && DataContext is DownloadsViewModel vm)
        {
            vm.PlayTrackCommand.Execute(row.Local);
            e.Handled = true;
        }
    }

    private void OnFinishedContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control owner && OpenTrackMenu(owner, PlacementMode.Pointer)) e.Handled = true;
    }

    private void OnFinishedMoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control owner && OpenTrackMenu(owner, PlacementMode.BottomEdgeAlignedRight)) e.Handled = true;
    }

    internal bool OpenTrackMenu(Control owner, PlacementMode placement)
    {
        if (owner.DataContext is not DownloadRow { IsActionable: true, Local: { } track } row
            || DataContext is not DownloadsViewModel { LibraryActions: { } actions } vm) return false;
        // This is the shared Noctis menu, including community track actions. Its remove action
        // only removes history; it is deliberately never wired to RemoveFromLibrary.
        actions.CtrlSelectedTracks.Clear();
        if (_menu is null || _menuCulture != Noctis.Localization.Loc.Instance.Culture.Name)
        {
            _menuCulture = Noctis.Localization.Loc.Instance.Culture.Name;
            _menu = new TrackContextMenuBuilder();
            _menu.Build("Remove from this list", null, this, v2: true, removeIsDanger: false);
        }
        _menu.Bind(track,
            playCommand: vm.PlayTrackCommand, shuffleCommand: vm.ShuffleFinishedCommand,
            playNextCommand: actions.PlayNextCommand, addToQueueCommand: actions.AddToQueueCommand,
            addToPlaylistCommand: actions.AddToNewPlaylistCommand, toggleFavoriteCommand: actions.ToggleFavoriteCommand,
            openMetadataCommand: actions.OpenMetadataCommand, searchLyricsCommand: actions.SearchLyricsCommand,
            showInExplorerCommand: actions.ShowInExplorerCommand,
            removeCommand: new RelayCommand(() => vm.RemoveCommand.Execute(row)),
            convertCommand: actions.ConvertTracksCommand, scanReplayGainCommand: actions.ScanReplayGainCommand,
            startRadioCommand: actions.StartRadioCommand, snoozeCommand: actions.SnoozeForMonthCommand,
            rateCommand: actions.RateTrackCommand, fetchLyricsCommand: actions.FetchLyricsCommand,
            lyricsStudioCommand: actions.OpenLyricsStudioCommand, removeLyricsCommand: actions.RemoveLyricsCommand,
            sendToFolderCommand: actions.SendToFolderCommand,
            viewAlbumCommand: vm.ViewAlbumCommand, viewArtistCommand: vm.ViewArtistCommand);
        ContextMenuCoordinator.NotifyOpening(_menu.Menu);
        MenuOpenAnimation.CloseNow(_menu.Menu);
        if (_menuOwner is not null) _menuOwner.ContextMenu = null;
        if (_menu.Menu.Parent is Control previous) previous.ContextMenu = null;
        _menuOwner = owner;
        owner.ContextMenu = _menu.Menu;
        _menu.Menu.Placement = placement;
        _menu.Menu.Open(owner);
        return true;
    }
}
