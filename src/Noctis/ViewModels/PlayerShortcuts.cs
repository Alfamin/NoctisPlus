using System;
using Noctis.Models;

namespace Noctis.ViewModels;

/// <summary>
/// The rebindable shortcuts that only drive the player, run against a
/// <see cref="PlayerViewModel"/> so they can be tested without a window. Window-level
/// actions (fullscreen, settings, navigation, dialogs) stay in MainWindow.
/// </summary>
public static class PlayerShortcuts
{
    /// <summary>Volume step for the volume shortcuts, in percent.</summary>
    public const int VolumeStep = 5;

    /// <summary>
    /// Runs <paramref name="action"/> when it is a player action. Returns null when it is
    /// not one; false when it is but has nothing to act on (no track loaded), so the key
    /// falls through untouched; true when it ran.
    /// </summary>
    public static bool? Run(PlayerViewModel player, ShortcutAction action)
    {
        switch (action)
        {
            case ShortcutAction.PlayPause:
                player.PlayPauseCommand.Execute(null);
                return true;
            case ShortcutAction.NextTrack:
                player.NextCommand.Execute(null);
                return true;
            case ShortcutAction.PreviousTrack:
                player.PreviousCommand.Execute(null);
                return true;
            // Adjusting while muted means "let me hear it", as the bar's wheel/drag do: a
            // shortcut step used to change the level silently under the mute (10-08).
            case ShortcutAction.VolumeUp:
                player.UnmuteForAdjust();
                player.Volume = Math.Min(100, player.Volume + VolumeStep);
                return true;
            case ShortcutAction.VolumeDown:
                player.UnmuteForAdjust();
                player.Volume = Math.Max(0, player.Volume - VolumeStep);
                return true;
            case ShortcutAction.ToggleFavorite:
                if (player.CurrentTrack == null) return false;
                player.ToggleCurrentTrackFavoriteCommand.Execute(null);
                return true;
            case ShortcutAction.ToggleQueue:
                // GitHub #86: the island's Queue button is gone while nothing is loaded
                // (the bar unmounts), so this is the way in to an empty queue.
                player.ShowQueueCommand.Execute(null);
                return true;
            // GitHub #131: the same commands as the island's buttons.
            case ShortcutAction.ToggleShuffle:
                if (player.NativeShuffle?.FollowShortcut == true && player.RequestShuffleModes()) return true;
                player.ToggleShuffleCommand.Execute(null);
                return true;
            case ShortcutAction.CycleRepeat:
                player.CycleRepeatCommand.Execute(null);
                return true;
            case ShortcutAction.ToggleMute:
                player.ToggleMuteCommand.Execute(null);
                return true;
            // The island's skip buttons, so the step follows Settings' skip seconds.
            case ShortcutAction.SeekForward:
                if (player.CurrentTrack == null) return false;
                player.SkipForwardCommand.Execute(null);
                return true;
            case ShortcutAction.SeekBackward:
                if (player.CurrentTrack == null) return false;
                player.SkipBackCommand.Execute(null);
                return true;
            default:
                return null;
        }
    }
}
