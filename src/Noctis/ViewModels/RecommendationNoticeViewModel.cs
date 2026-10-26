using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Services.Search;

namespace Noctis.ViewModels;

/// <summary>The native artist-preference confirmation, shared by song, player, artist and Search menus.</summary>
public sealed partial class RecommendationNoticeViewModel : ObservableObject
{
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _expiry;
    private Action? _undo;
    [ObservableProperty] private bool _isShown;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _hasUndo;

    public RecommendationNoticeViewModel(Func<TimeSpan, Action, IDisposable>? schedule = null)
        => _schedule = schedule ?? ((delay, action) => DispatcherTimer.RunOnce(action, delay));

    internal void Show(IOnlineMusicSearch source, string artist, bool blocked)
    {
        _expiry?.Dispose();
        Text = (blocked ? "Won't recommend · " : "Recommending again · ") + artist;
        _undo = () =>
        {
            // Never apply an old Undo to a different feature instance or a newer preference.
            if (ReferenceEquals(OnlineMusicSearch.Current, source) && source.IsArtistBlocked(artist) == blocked)
                source.SetArtistBlocked(artist, !blocked);
        };
        HasUndo = true;
        IsShown = true;
        _expiry = _schedule(TimeSpan.FromSeconds(8), Hide);
    }

    [RelayCommand] private void Undo()
    {
        var undo = _undo;
        Hide();
        undo?.Invoke();
    }

    internal void Hide()
    {
        _expiry?.Dispose(); _expiry = null; _undo = null;
        HasUndo = false; IsShown = false;
    }
}
