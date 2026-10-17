using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Features.TrueShuffle;
using Noctis.Models;

namespace Noctis.ViewModels;

public enum PlayerShuffleMode { Off, Noctis, EqualChance, NoRepeats, Discovery, NeverPlayed, ForYou }

public partial class PlayerViewModel
{
    internal TrueShuffleService? NativeShuffle { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsShuffleLit))]
    private PlayerShuffleMode _shuffleMode;
    [ObservableProperty] private bool _isShuffleBusy;
    [ObservableProperty] private string _shuffleStatus = "";
    [ObservableProperty] private bool _trueShuffleAvailable;

    public bool IsShuffleLit => ShuffleMode != PlayerShuffleMode.Off || IsShuffleEnabled;
    internal event Func<bool>? ShuffleMenuRequested;
    internal bool RequestShuffleModes()
    {
        if (ShuffleMenuRequested is not { } handlers) return false;
        foreach (Func<bool> show in handlers.GetInvocationList()) if (show()) return true;
        return false;
    }

    partial void OnIsShuffleEnabledChanged(bool value)
    {
        if (ShuffleMode is PlayerShuffleMode.Off or PlayerShuffleMode.Noctis)
            ShuffleMode = value ? PlayerShuffleMode.Noctis : PlayerShuffleMode.Off;
        OnPropertyChanged(nameof(IsShuffleLit));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SelectShuffleMode(PlayerShuffleMode mode)
    {
        if (NativeShuffle is { } shuffle) return shuffle.SelectAsync(mode);
        if (mode is PlayerShuffleMode.Off or PlayerShuffleMode.Noctis && IsShuffleEnabled != (mode == PlayerShuffleMode.Noctis))
            ToggleShuffle();
        return Task.CompletedTask;
    }

    // Explicit queue intent, not a collection watcher. The service can retain the user's own
    // requests or switch to the list they just played without guessing from collection resets.
    internal event Action<IReadOnlyList<Track>>? ShuffleListStarting;
    internal event Action<IReadOnlyList<Track>>? ShuffleListStarted;
    internal event Action? ShuffleQueueCleared;

    internal void ClearShuffleUpcoming() => ClearQueueCore(notifyShuffle: false);
    internal void PlayShuffleOrder(IList<Track> tracks) => ReplaceQueueAndPlay(tracks, 0, null, notifyShuffle: false);
}
