using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Noctis.Controls;

namespace Noctis.Mobile.Views;

/// <summary>
/// The round profile button of the tab pages' headers: a glass circle (it is the GlassPanel, so
/// its shadow is not cut at a wrapper's edges) showing <see cref="AvatarPath"/> (the desktop's
/// profile picture, <c>ShellViewModel.ProfileAvatarPath</c>) clipped to a circle, or the person
/// glyph when there is none, and running <see cref="Command"/> (Settings) on a tap:
/// <code>&lt;views:ProfileButton AvatarPath="{Binding ProfileAvatarPath}" Command="{Binding OpenSettingsCommand}" /&gt;</code>
/// </summary>
public partial class ProfileButton : GlassPanel
{
    public static readonly StyledProperty<string?> AvatarPathProperty =
        AvaloniaProperty.Register<ProfileButton, string?>(nameof(AvatarPath));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<ProfileButton, ICommand?>(nameof(Command));

    /// <summary>Styled as the GlassPanel it is (MobileStyles' header-glass).</summary>
    protected override Type StyleKeyOverride => typeof(GlassPanel);

    public ProfileButton()
    {
        InitializeComponent();
    }

    /// <summary>Local picture file; null or empty shows the person glyph.</summary>
    public string? AvatarPath
    {
        get => GetValue(AvatarPathProperty);
        set => SetValue(AvatarPathProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>A picture is shown (tests and callers that style around it).</summary>
    public bool HasAvatar => !string.IsNullOrEmpty(AvatarPath);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AvatarPathProperty)
        {
            var has = HasAvatar;
            AvatarImage.SourcePath = has ? AvatarPath : null;
            AvatarClip.IsVisible = has;
            AvatarGlyph.IsVisible = !has;
        }
        else if (change.Property == CommandProperty)
        {
            ProfileButtonRoot.Command = Command;
        }
    }
}
