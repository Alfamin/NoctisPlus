using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class NowPlayingPage : UserControl
{
    public NowPlayingPage()
    {
        InitializeComponent();

        // The bar is a CapsuleSlider: it captures the finger itself and brackets the drag with
        // these, so a gesture taken away by the system (a notification, the shade) arrives as
        // ScrubCanceled. The ViewModel's seek watchdog stays the backstop for a gesture that
        // ends with no pointer event at all (device, 2026-09-22).
        SeekBar.ScrubStarted += (_, _) => (DataContext as ShellViewModel)?.Player.BeginSeek();
        SeekBar.ScrubCompleted += OnSeekCompleted;
        SeekBar.ScrubCanceled += (_, _) => (DataContext as ShellViewModel)?.Player.EndSeek();
        // The times follow the drag (PreviewSeek is a no-op unless a seek is held, so the
        // position pushes that move the bar between drags never come back through here).
        SeekBar.ValueChanged += OnSeekValueChanged;
        Menu.Anchor = NowPlayingMore;
    }

    private bool? _landscape;

    /// <summary>
    /// Rotation does not recreate the activity, so the page re-lays itself out on resize. In a
    /// landscape phone window (~412 dp tall) the portrait column leaves the cover's row almost
    /// nothing once the title, seek bar, transport, volume and bottom icons have their height;
    /// side by side, the cover gets the full height on the left and the controls fit on the right.
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyOrientation(e.NewSize.Width > e.NewSize.Height);
    }

    private void ApplyOrientation(bool landscape)
    {
        if (_landscape == landscape) return;
        _landscape = landscape;
        Layout.ColumnDefinitions = landscape ? new ColumnDefinitions("5*,6*") : new ColumnDefinitions("*");
        foreach (var child in Layout.Children)
            if (child != ArtworkBox) Grid.SetColumn(child, landscape ? 1 : 0);
        Grid.SetRow(ArtworkBox, landscape ? 0 : 1);
        Grid.SetRowSpan(ArtworkBox, landscape ? Layout.RowDefinitions.Count : 1);
        ArtworkBox.Margin = landscape ? new Thickness(0, 0, 24, 0) : new Thickness(0, 8);
    }

    // Seek on release only: seeking per pixel during a drag stutters the decoder. Held
    // between ScrubStarted and here, the player's 4 Hz position ticks leave the bar alone.
    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        vm.Player.EndSeek();
        vm.Player.Seek(TimeSpan.FromSeconds(SeekBar.Value));
    }

    private void OnSeekValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        => (DataContext as ShellViewModel)?.Player.PreviewSeek(TimeSpan.FromSeconds(e.NewValue));
}
