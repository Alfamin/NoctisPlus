using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Services;

namespace Noctis.ViewModels;

public partial class SettingsViewModel
{
    [ObservableProperty] private bool _offlineMode;
    partial void OnOfflineModeChanged(bool value)
    {
        _settings.OfflineMode = value;
        OnlineAccess.Shared.SetOffline(value);
        _ = SaveAsync();
    }
    [RelayCommand] private void GoOnline() => OfflineMode = false;
}
