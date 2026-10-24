using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using FreeMusicFinder.Tests;

if (Environment.GetEnvironmentVariable("FMF_UNIFIED_LIVE") is { Length: > 0 } searchQueries)
{
    await LiveUnifiedSearchTest.RunAsync(searchQueries);
    return 0;
}
if (Environment.GetEnvironmentVariable("FMF_LYRICS_LIVE") is { Length: > 0 } lyricQueries)
{
    try { await LiveLyricSearchTest.RunAsync(lyricQueries, Environment.GetEnvironmentVariable("FMF_LIVE_PROXY")); }
    finally { Console.WriteLine($"{Check.Passed} ok, {Check.Failed} wrong"); Check.RemoveFolders(); }
    return Check.Failed;
}
if (Environment.GetEnvironmentVariable("FMF_PLAYLIST_SEARCH_LIVE") is { Length: > 0 } playlistQueries)
{
    try { await LivePlaylistSearchTest.RunAsync(playlistQueries, Environment.GetEnvironmentVariable("FMF_LIVE_PROXY")); }
    finally { Console.WriteLine($"{Check.Passed} ok, {Check.Failed} wrong"); }
    return Check.Failed;
}
if (Environment.GetEnvironmentVariable("FMF_SEARCH_PREVIEW_QUERY") is { Length: > 0 } previewQueries)
{
    AppBuilder.Configure<TestApp>().UseSkia().UseHarfBuzz().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
    try { SearchPreview.Run(previewQueries); } finally { Check.RemoveFolders(); }
    return Check.Failed;
}

// Explicit opt-in: For you on the real (read-only) listening history and library, against the public catalog.
if (Environment.GetEnvironmentVariable("FMF_FORYOU_LIVE") is { Length: > 0 } forYouData)
{
    try { await LiveForYouTest.RunAsync(forYouData); } finally { Console.WriteLine($"{Check.Passed} ok, {Check.Failed} wrong"); }
    return Check.Failed;
}

// Explicit opt-in, offline: chronological replay of For you's taste step on a copy of the listener's own data.
if (Environment.GetEnvironmentVariable("FMF_DISCOVER_REPLAY") is { Length: > 0 } replayData)
{
    await DiscoverReplayTest.RunAsync(replayData);
    return 0;
}

// Explicit public catalog metadata probe. No Telegram connection, audio download or live library write.
if (Environment.GetEnvironmentVariable("FMF_DISCOVERY_LIVE") is { Length: > 0 } artists)
{
    await LiveDiscoveryTest.RunAsync(artists);
    return 0;
}

// Runs every test and prints one line each. The exit code is the number of wrong ones.
// With FMF_PICTURES set to a folder, the window tests also save pictures of the search window there.
// With FMF_LIVE_ROUTE set to public Spotify or Deezer links, only LiveRouteTest runs: it reads their
// metadata from the real services and says which way (proxy or direct) this computer reached them.
if (Environment.GetEnvironmentVariable("FMF_LIVE_ROUTE") is { Length: > 0 } links)
{
    await LiveRouteTest.RunAsync(links, Environment.GetEnvironmentVariable("FMF_LIVE_PROXY"));
    return 0;
}
// With FMF_LIVE_BOT set to a track link and FMF_TELEGRAM_STORE to the plugin's login file, only
// LiveBotTest runs: it asks DeezLoad for that track with the real account (one message is sent) and
// prints what the bot answers, without downloading the file. Noctis must be closed for it.
if (Environment.GetEnvironmentVariable("FMF_LIVE_BOT") is { Length: > 0 } track)
{
    await LiveBotTest.RunAsync(track, Environment.GetEnvironmentVariable("FMF_TELEGRAM_STORE"), Environment.GetEnvironmentVariable("FMF_LIVE_PROXY"));
    return 0;
}
// With FMF_REPLAY_IMPORTS set to a copy of the plugin's saved imports (spotify-playlists.json) and
// FMF_LIBRARY_SNAPSHOT to a library list, only the replay runs: the saved playlists are matched
// against that library again, offline, and what changed is printed. Both files are only read.
if (Environment.GetEnvironmentVariable("FMF_REPLAY_IMPORTS") is { Length: > 0 } replay)
{
    try { await LivePlaylistTest.ReplayAsync(replay, Environment.GetEnvironmentVariable("FMF_LIBRARY_SNAPSHOT")); }
    finally { Check.RemoveFolders(); }
    Console.WriteLine($"\n{Check.Passed} ok, {Check.Failed} wrong");
    return Check.Failed;
}
try
{
    await LogicTests.RunAsync();
    await TelegramLoginDeadlineTests.RunAsync();
    await CatalogRetryTests.RunAsync();
    await CatalogPagesTests.RunAsync();
    BotTimingTests.Run();
    await TransferTests.RunAsync();
    await BotQualityTests.RunAsync();
    await QueueManagementTests.RunAsync();
    await StreamCacheTests.RunAsync();
    await BotSelectionTests.RunAsync();
    await HunterLinkTests.RunAsync();
    await BotPreferenceTests.RunAsync();
    await CheckTheseTests.RunAsync();
    await SpotifyPlaylistTests.RunAsync();
    await PlaylistSearchTests.RunAsync();
    await LyricSearchTests.RunAsync();
    await HunterLinkTests.ResultsLockAsync();
    await BotLibraryTests.RunAsync();
    await DiscoveryTests.RunAsync();
    await UnifiedSearchTests.RunAsync();
    await CatalogPerformanceTests.RunAsync();
    await ArtistIdentityTests.RunAsync();
    await ArtistPortraitTests.RunAsync();
    await ArtworkQualityTests.RunAsync();
    await DiscoveryNetworkTests.RunAsync();
    await ForYouDiscoverTests.RunAsync();
    await LastFmDiscoverTests.RunAsync();
    await LivePlaylistTest.RunAsync(Environment.GetEnvironmentVariable("FMF_SPOTIFY_PLAYLIST"), Environment.GetEnvironmentVariable("FMF_LIBRARY_SNAPSHOT"), Environment.GetEnvironmentVariable("FMF_LIVE_REPORT"));

    var pictures = Environment.GetEnvironmentVariable("FMF_PICTURES");
    var app = AppBuilder.Configure<TestApp>();
    if (string.IsNullOrEmpty(pictures)) app.UseHeadless(new AvaloniaHeadlessPlatformOptions());
    else app.UseSkia().UseHarfBuzz().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    app.SetupWithoutStarting();
    TelegramSignInTests.Run();
    StreamingPlaylistImportTests.Run();
    NativeForYouTests.Run();
    NativeForYouTests.RunPager();
    DiscoverReplayChecks.Run();
    WindowTests.Run(string.IsNullOrEmpty(pictures) ? null : pictures);
    PlaylistWindowTests.Run(pictures);
    NativePlaylistTests.Run();
    ArtistPortraitTests.RunUi(pictures);
    UnifiedSearchTests.RunUi(pictures);
    ForYouDiscoverTests.RunUi();
    ForYouLayoutTests.Run(pictures);
}
catch (Exception ex)
{
    Check.True(false, "the tests stopped: " + ex);
}
finally
{
    Check.RemoveFolders();
}

Console.WriteLine($"\n{Check.Passed} ok, {Check.Failed} wrong");
return Check.Failed;

internal sealed class TestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }
}

