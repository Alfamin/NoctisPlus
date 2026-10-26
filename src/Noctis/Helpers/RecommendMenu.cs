using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services.Search;
using Noctis.ViewModels;

namespace Noctis.Helpers;

/// <summary>What a "Don't recommend artist" entry does when clicked: block or unblock one artist.</summary>
public sealed record RecommendToggle(string Artist, bool Block);

/// <summary>
/// Owner #166: "Don't recommend artist" in every artist and song menu, like <see cref="ScrobbleMenu"/>: one static command,
/// and the entry binds itself each time its menu opens. One artist: the entry itself, flipping to "Recommend again" once
/// blocked. Several (a song with features): one ticked or unticked entry per artist; the listener picks which, never all
/// at once. Hidden while there is no For you (Free Music Finder off).
/// </summary>
public static class RecommendMenu
{
    /// <summary>For menus declared in XAML: "artist" (the artist page) or "song" (an online song row in Search).</summary>
    public static readonly AttachedProperty<string?> EntryProperty =
        AvaloniaProperty.RegisterAttached<MenuItem, string?>("Entry", typeof(RecommendMenu));

    public static string? GetEntry(MenuItem item) => item.GetValue(EntryProperty);
    public static void SetEntry(MenuItem item, string? value) => item.SetValue(EntryProperty, value);

    public static ICommand ToggleCommand { get; } = new RelayCommand<object?>(p =>
    {
        if (p is RecommendToggle t) OnlineMusicSearch.Current?.SetArtistBlocked(t.Artist, t.Block);
    });

    static RecommendMenu()
    {
        EntryProperty.Changed.AddClassHandler<MenuItem>((item, e) =>
        {
            item.AttachedToVisualTree -= OnEntryAttached;
            item.DataContextChanged -= OnEntryDataContextChanged;
            if (e.NewValue is string)
            {
                item.IsVisible = false;
                item.AttachedToVisualTree += OnEntryAttached;
                item.DataContextChanged += OnEntryDataContextChanged;
            }
        });
    }

    // As ScrobbleMenu: the DataContext can arrive just before or just after the entry attaches.
    private static void OnEntryAttached(object? sender, VisualTreeAttachmentEventArgs e) => Rebind(sender);
    private static void OnEntryDataContextChanged(object? sender, EventArgs e) => Rebind(sender);

    private static void Rebind(object? sender)
    {
        if (sender is not MenuItem item) return;
        switch (GetEntry(item), item.DataContext)
        {
            case ("artist", ArtistDetailViewModel vm) when !string.IsNullOrWhiteSpace(vm.ArtistName): Bind(item, [vm.ArtistName]); break;
            case ("song", SearchSongItem { Online: { } song }): Bind(item, Track.ParseArtistTokens(song.ArtistDisplay)); break;
            default: item.IsVisible = false; break;
        }
    }

    public static void BindTrack(MenuItem item, Track track) => Bind(item, TrackContextMenuBuilder.CreditedArtists(track));

    public static void Bind(MenuItem item, IReadOnlyList<string> names)
    {
        var source = OnlineMusicSearch.Current;
        item.Items.Clear();
        names = [.. names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase)];
        item.IsVisible = source?.CanBlockArtists == true && names.Count > 0;
        if (!item.IsVisible) return;
        if (names.Count == 1)
        {
            var blocked = source!.IsArtistBlocked(names[0]);
            item.Header = blocked ? "Recommend again" : "Don't recommend artist";
            item.Command = ToggleCommand;
            item.CommandParameter = new RecommendToggle(names[0], !blocked);
            return;
        }
        item.Header = "Don't recommend artist";
        item.Command = null; item.CommandParameter = null;
        foreach (var name in names)
        {
            var blocked = source!.IsArtistBlocked(name);
            item.Items.Add(new MenuItem
            {
                Header = name, ToggleType = MenuItemToggleType.CheckBox, IsChecked = blocked,
                Command = ToggleCommand, CommandParameter = new RecommendToggle(name, !blocked),
            });
        }
    }
}
