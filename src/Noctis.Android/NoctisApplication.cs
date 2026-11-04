using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Noctis.Android;

/// <summary>Android application object: owns the Avalonia app across activity restarts.</summary>
[Application(Label = AppVariant.Label)]
public class NoctisApplication : AvaloniaAndroidApplication<AndroidApp>
{
    protected NoctisApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder).WithInterFont();
}
