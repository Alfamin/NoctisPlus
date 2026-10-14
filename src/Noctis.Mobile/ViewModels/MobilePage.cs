using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.Mobile.ViewModels;

/// <summary>The tabs of the phone UI, in the bar's order (Apple Music's: Home first, Search last).
/// Home is the start tab (Back from the others returns to it).</summary>
public enum MobileTab { Home, Favorites, Playlists, Library, Search }

/// <summary>
/// A page pushed over a tab's root (Songs, an album, an artist, Settings…). ShellView shows
/// the top one through its PageHost with a DataTemplate per concrete type, so a page's view
/// is re-created when it comes back to the top; <see cref="ScrollOffset"/> is how it keeps
/// its place (see ScrollMemory).
/// </summary>
public abstract partial class MobilePage : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Shown as a tab's root under the tab's own title (Playlists, Favorites) rather
    /// than pushed: the view hides its own back/title header.</summary>
    public bool IsEmbedded { get; init; }

    [ObservableProperty] private Vector _scrollOffset;

    /// <summary>The page left the stack for good (Back, a tab switch): drop
    /// event subscriptions so a closed page stops rebuilding on every library refresh.</summary>
    public virtual void OnClosed() { }

    /// <summary>Android Back, before the page itself is popped: a page with pages of its own
    /// (Settings' Music Folders, Appearance, Lyrics) returns to its top level and says so.</summary>
    public virtual bool TryGoBackWithin() => false;

    public override string ToString() => Title;
}
