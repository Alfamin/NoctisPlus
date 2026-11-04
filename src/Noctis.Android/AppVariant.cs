namespace Noctis.Android;

/// <summary>
/// Which app this build is (#255). NOCTIS_PLUS comes from the csproj's NoctisPlus property
/// (default true in this fork; -p:NoctisPlus=false builds heartached's original Noctis).
/// The ApplicationId switches with it in the csproj, so the two apps install side by side.
/// </summary>
internal static class AppVariant
{
#if NOCTIS_PLUS
    public const string Label = "Noctis Plus";
#else
    public const string Label = "Noctis";
#endif
}
