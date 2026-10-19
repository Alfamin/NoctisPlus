using CommunityToolkit.Mvvm.Input;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Now Playing's quality badge tapped: the file's class under the desktop's waveform, the technical line, one
/// plain line on what it means, and OK - on the glass pop menu (PopMenu), out of the badge. No
/// settings row: the phone has no audio-quality setting to open.
/// </summary>
public sealed partial class QualityCardViewModel
{
    private readonly ShellViewModel _shell;

    public QualityCardViewModel(ShellViewModel shell, TrackQuality quality)
    {
        _shell = shell;
        Quality = quality;
    }

    public TrackQuality Quality { get; }

    public string Label => Quality.Label;
    public string Detail => Quality.Detail;
    public string Meaning => Quality.Meaning;
    public bool HasDetail => Quality.Detail.Length > 0;

    [RelayCommand]
    private void Done() => _shell.CloseSheet();
}
