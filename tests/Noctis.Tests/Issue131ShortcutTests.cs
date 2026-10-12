using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #131 "Add more shortcuts": Open Settings, ReplayGain for the selection, and the
/// player / navigation keys that had none. Defaults and persistence live in
/// ShortcutDefaultsTests / ShortcutServiceTests; this covers what the keys do.
/// </summary>
public class Issue131ShortcutTests
{
    private static PlayerViewModel Player() =>
        new(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());

    private static Track Song(string title, string path) => new() { Title = title, FilePath = path };

    // ── Player actions ──

    [AvaloniaFact]
    public void ShuffleRepeatMute_RunTheIslandCommands()
    {
        var p = Player();
        var shuffle = p.IsShuffleEnabled;
        Assert.True(PlayerShortcuts.Run(p, ShortcutAction.ToggleShuffle));
        Assert.Equal(!shuffle, p.IsShuffleEnabled);

        p.RepeatMode = RepeatMode.Off;
        Assert.True(PlayerShortcuts.Run(p, ShortcutAction.CycleRepeat));
        Assert.Equal(RepeatMode.All, p.RepeatMode);
        PlayerShortcuts.Run(p, ShortcutAction.CycleRepeat);
        Assert.Equal(RepeatMode.One, p.RepeatMode);
        PlayerShortcuts.Run(p, ShortcutAction.CycleRepeat);
        Assert.Equal(RepeatMode.Off, p.RepeatMode);

        Assert.False(p.IsMuted);
        Assert.True(PlayerShortcuts.Run(p, ShortcutAction.ToggleMute));
        Assert.True(p.IsMuted);
        Assert.True(PlayerShortcuts.Run(p, ShortcutAction.ToggleMute));
        Assert.False(p.IsMuted);
    }

    [AvaloniaFact]
    public void VolumeStep_UnmutesFirst()
    {
        var p = Player();
        p.Volume = 50;
        p.IsMuted = true;
        Assert.True(PlayerShortcuts.Run(p, ShortcutAction.VolumeUp));
        Assert.False(p.IsMuted);
        Assert.Equal(50 + PlayerShortcuts.VolumeStep, p.Volume);
    }

    /// <summary>With nothing loaded the seek and favorite keys have nothing to act on: they
    /// report false so the key falls through instead of being swallowed.</summary>
    [AvaloniaFact]
    public void SeekAndFavorite_WithNoTrack_FallThrough()
    {
        var p = Player();
        Assert.Null(p.CurrentTrack);
        Assert.False(PlayerShortcuts.Run(p, ShortcutAction.SeekForward));
        Assert.False(PlayerShortcuts.Run(p, ShortcutAction.SeekBackward));
        Assert.False(PlayerShortcuts.Run(p, ShortcutAction.ToggleFavorite));
    }

    [AvaloniaFact]
    public void WindowActions_AreNotPlayerActions()
    {
        var p = Player();
        foreach (var a in new[] { ShortcutAction.OpenSettings, ShortcutAction.ScanReplayGain, ShortcutAction.ToggleMiniPlayer,
                     ShortcutAction.GoBack, ShortcutAction.GoForward, ShortcutAction.GoHome, ShortcutAction.GoSongs,
                     ShortcutAction.GoAlbums, ShortcutAction.GoArtists, ShortcutAction.ToggleFullscreen,
                     ShortcutAction.SearchLibrary, ShortcutAction.CommandPalette, ShortcutAction.NewPlaylist, ShortcutAction.ToggleLyrics })
            Assert.Null(PlayerShortcuts.Run(p, a));
    }

    // ── ReplayGain target ──

    [Fact]
    public void ReplayGain_QueueSelection_ThenPage_ThenPlaying_ElseNothing()
    {
        var q = new[] { Song("q", @"C:\m\q.flac") };
        var page = new[] { Song("a", @"C:\m\a.flac"), Song("b", @"C:\m\b.flac") };
        var playing = Song("p", @"C:\m\p.flac");

        Assert.Same(q, TrackSelection.ForReplayGain(q, page, playing));
        Assert.Same(page, TrackSelection.ForReplayGain(Array.Empty<Track>(), page, playing));
        Assert.Same(page, TrackSelection.ForReplayGain(null, page, playing));
        Assert.Equal(new[] { playing }, TrackSelection.ForReplayGain(null, Array.Empty<Track>(), playing));
        Assert.Empty(TrackSelection.ForReplayGain(null, null, null));
    }

    [Theory]
    [InlineData("http://server/stream/42")]
    [InlineData("cdda:///D:/track03")]
    [InlineData("")]
    public void ReplayGain_PlayingFallback_SkipsStreamsAndDiscs(string path)
        => Assert.Empty(TrackSelection.ForReplayGain(null, null, Song("x", path)));

    [Fact]
    public void ReplayGain_UncPlayingTrack_IsAFile()
        => Assert.Single(TrackSelection.ForReplayGain(null, null, Song("x", @"\\nas\music\x.flac")));

    [Fact]
    public void QueueIndices_MapToRows_InOrder_SkippingStaleOnes()
    {
        var rows = new[] { Song("0", "0"), Song("1", "1"), Song("2", "2") };
        Assert.Equal(new[] { rows[0], rows[2] }, TrackSelection.FromIndices(rows, new[] { 2, 7, 0, -1 }));
    }

    [Fact]
    public void AlbumSelection_ExpandsToTracks()
    {
        var a = new Album { Tracks = new List<Track> { Song("1", "1"), Song("2", "2") } };
        var b = new Album { Tracks = new List<Track> { Song("3", "3") } };
        Assert.Equal(a.Tracks.Concat(b.Tracks), TrackSelection.FromAlbums(new[] { a, b }));
    }

    // ── Real key presses ──

    /// <summary>
    /// A real Ctrl+, / Ctrl+1 / Alt+← press (headless qwerty) produces the Key the defaults
    /// name (OemComma, D1, Left), and the window tunnel rule from MainWindow keeps
    /// Shift+→ / Alt+← in a focused TextBox while Ctrl+, / Ctrl+S still fire from it.
    /// </summary>
    [AvaloniaFact]
    public void RealPresses_MatchDefaults_AndTextBoxKeepsCaretKeys()
    {
        var shortcuts = new ShortcutService(isMac: false);
        var button = new Button { Content = "x" };
        var box = new TextBox { Text = "hello world" };
        var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { button, box } } };
        var fired = new List<ShortcutAction>();
        window.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (shortcuts.TryMatch(e) is not { } action) return;
                if (e.Source is TextBox && ShortcutDefaults.IsTextBoxKey(e.Key, e.KeyModifiers)) return;
                fired.Add(action);
                e.Handled = true;
            },
            RoutingStrategies.Tunnel);

        void Press(PhysicalKey key, RawInputModifiers mods)
        {
            window.KeyPressQwerty(key, mods);
            window.KeyReleaseQwerty(key, mods);
        }

        window.Show();
        button.Focus();
        Press(PhysicalKey.Comma, RawInputModifiers.Control);
        Press(PhysicalKey.Digit1, RawInputModifiers.Control);
        Press(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        Press(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        Press(PhysicalKey.R, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(new[] { ShortcutAction.OpenSettings, ShortcutAction.GoHome, ShortcutAction.GoBack,
                             ShortcutAction.SeekForward, ShortcutAction.ScanReplayGain }, fired);

        fired.Clear();
        box.Focus();
        box.CaretIndex = 0;
        Press(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        Press(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        Assert.Empty(fired);

        Press(PhysicalKey.Comma, RawInputModifiers.Control);
        Press(PhysicalKey.S, RawInputModifiers.Control);
        Assert.Equal(new[] { ShortcutAction.OpenSettings, ShortcutAction.ToggleShuffle }, fired);
        window.Close();
    }

    // ── Settings › Shortcuts labels ──

    /// <summary>The tab's labels, group titles and "Not set" were hard-coded English; they
    /// now come from Strings.resx and follow a live language switch.</summary>
    [AvaloniaFact]
    public void ShortcutsTab_IsLocalized_AndRelabelsOnLanguageSwitch()
    {
        var service = new ShortcutService(isMac: false);
        var vm = new ShortcutsSettingsViewModel(service, isMac: false);
        var row = vm.Rows.Single(r => r.Action == ShortcutAction.OpenSettings);
        var group = vm.Groups.Single(g => g.Id == ShortcutDefaults.GroupWindow);
        service.Set(ShortcutAction.ScanReplayGain, null);
        var unbound = vm.Rows.Single(r => r.Action == ShortcutAction.ScanReplayGain);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        try
        {
            Loc.Instance.SetCulture("en");
            Assert.Equal("Open Settings", row.Label);
            Assert.Equal("Window", group.Name);
            Assert.Equal(new[] { "Not set" }, unbound.GestureParts);

            Loc.Instance.SetCulture("ru");
            Assert.Equal("Открыть настройки", row.Label);
            Assert.Equal("Окно", group.Name);
            Assert.Equal(new[] { "Не задано" }, unbound.GestureParts);
            Assert.Contains(nameof(ShortcutRowViewModel.Label), changed);
            Assert.All(vm.Rows, r => Assert.DoesNotContain("Shortcuts.", r.Label));
        }
        finally
        {
            Loc.Instance.SetCulture("en");
        }
    }
}
