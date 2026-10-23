namespace FreeMusicFinder.Tests;

/// <summary>
/// Explicit opt-in, offline: <see cref="DiscoverReplay.ReportAsync"/> on FMF_DISCOVER_REPLAY, a COPY of Noctis's data folder
/// (see docs/fork/discover-plan.md for what to copy). No network, no writes to the copy, nothing printed but counts and percentages.
/// </summary>
internal static class DiscoverReplayTest
{
    public static Task RunAsync(string noctisData) => DiscoverReplay.ReportAsync(noctisData, Console.Out);
}
