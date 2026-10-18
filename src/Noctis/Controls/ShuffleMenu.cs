using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Features.TrueShuffle;
using Noctis.ViewModels;

namespace Noctis.Controls;

/// <summary>Declared on each native shuffle button. No scanning/injection or attach delay.</summary>
public sealed class ShuffleMenu : MenuFlyout
{
    public static readonly StyledProperty<PlayerViewModel?> PlayerProperty =
        AvaloniaProperty.Register<ShuffleMenu, PlayerViewModel?>(nameof(Player));
    public PlayerViewModel? Player { get => GetValue(PlayerProperty); set => SetValue(PlayerProperty, value); }
    private readonly Dictionary<PlayerShuffleMode, MenuItem> _modes = [];
    private readonly Dictionary<ShuffleScope, MenuItem> _sources = [];
    private readonly MenuItem _status = new() { IsEnabled = false };
    // One spinner for the "Preparing shuffle…" row. A new ring on every refresh restarted its animation each time
    // the player raised a change (position ticks included), so it looked stuck (owner, 2026-10-18).
    private readonly ProgressRing _busyRing = new() { IsIndeterminate = true, Width = 16, Height = 16 };
    private readonly MenuItem _source = new() { Header = "Source" };
    private ShuffleScope? _scope;
    private PlayerViewModel? _listening;

    public ShuffleMenu()
    {
        foreach (var (mode, label) in new[]
        {
            (PlayerShuffleMode.Off, "Off"), (PlayerShuffleMode.Noctis, "Noctis shuffle"),
            (PlayerShuffleMode.EqualChance, "Equal chance"), (PlayerShuffleMode.NoRepeats, "No repeats"),
            (PlayerShuffleMode.Discovery, "Discovery"), (PlayerShuffleMode.NeverPlayed, "Never played"),
            (PlayerShuffleMode.ForYou, "For you"),
        })
        {
            if (mode == PlayerShuffleMode.EqualChance) Items.Add(new Separator());
            var item = new MenuItem { Header = label, StaysOpenOnClick = true,
                Icon = new LineIcon { Data = Geometry.Parse("M4,12 L9,17 L20,6"), Width = 16, Height = 16, Opacity = 0 } };
            item.Click += (_, _) =>
            {
                if (Player is not { } player) return;
                if (player.NativeShuffle is { } service) _ = service.SelectAsync(mode, _scope);
                else player.SelectShuffleModeCommand.Execute(mode);
            };
            _modes[mode] = item; Items.Add(item);
        }
        Items.Add(new Separator()); Items.Add(_source);
        foreach (var (scope, label) in new[] { (ShuffleScope.CurrentQueue, "Current queue"), (ShuffleScope.WholeLibrary, "Whole library"), (ShuffleScope.OpenPlaylist, "Open playlist") })
        {
            var item = new MenuItem { Header = label, StaysOpenOnClick = true,
                Icon = new LineIcon { Data = Geometry.Parse("M4,12 L9,17 L20,6"), Width = 16, Height = 16 } };
            item.Click += (_, _) => { _scope = scope; Refresh(); };
            _sources[scope] = item;
            _source.Items.Add(item);
        }
        Items.Add(_status);
        Opened += (_, _) =>
        {
            Unlisten(); _listening = Player;
            if (_listening is not null) _listening.PropertyChanged += Changed;
            Refresh();
            var chosen = Player?.ShuffleMode == PlayerShuffleMode.Off ? Player.NativeShuffle?.DefaultMode : Player?.ShuffleMode;
            if (chosen is { } mode) Avalonia.Threading.Dispatcher.UIThread.Post(() => _modes[mode].Focus());
        };
        Closed += (_, _) => Unlisten();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        // Only what the menu shows; the player also raises position and level changes many times a second.
        if (e.PropertyName is null or nameof(PlayerViewModel.ShuffleMode) or nameof(PlayerViewModel.IsShuffleBusy)
            or nameof(PlayerViewModel.ShuffleStatus) or nameof(PlayerViewModel.TrueShuffleAvailable)) Refresh();
    }
    private void Unlisten() { if (_listening is not null) _listening.PropertyChanged -= Changed; _listening = null; }
    private void Refresh()
    {
        var player = Player;
        foreach (var (mode, item) in _modes)
        {
            if (item.Icon is LineIcon icon) icon.Opacity = player?.ShuffleMode == mode ? 1 : 0;
            item.IsEnabled = player is not null && (mode is PlayerShuffleMode.Off or PlayerShuffleMode.Noctis || player.TrueShuffleAvailable);
        }
        var scope = _scope ?? player?.NativeShuffle?.ActiveScope ?? ShuffleScope.CurrentQueue;
        foreach (var (value, item) in _sources) if (item.Icon is LineIcon icon) icon.Opacity = value == scope ? 1 : 0;
        _source.Header = "Source · " + (scope == ShuffleScope.WholeLibrary ? "Whole library" : scope == ShuffleScope.OpenPlaylist ? "Open playlist" : "Current queue");
        _status.IsVisible = player?.IsShuffleBusy == true || !string.IsNullOrWhiteSpace(player?.ShuffleStatus);
        _status.Header = player?.IsShuffleBusy == true ? "Preparing shuffle…" : player?.ShuffleStatus;
        var busyIcon = player?.IsShuffleBusy == true ? _busyRing : null;
        if (!ReferenceEquals(_status.Icon, busyIcon)) _status.Icon = busyIcon;
    }
}
