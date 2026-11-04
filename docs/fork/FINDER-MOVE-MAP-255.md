# #255 Finder move map: `src/Noctis/Features/FreeMusicFinder` -> shared `src/Noctis.Finder`

2026-11-04, read-only mapping, no code changed. This is the detail for ANDROID-PLUS-PLAN.md section 6 / stage 3.
Source: branch `android-plus` (57 files, 13,072 lines in 56 `.cs` files plus `plugin.json`/`LICENSE`, WTelegramClient 4.4.9).

## Target shape

- `src/Noctis.Finder` (net10.0, **no Avalonia**): references `Noctis.Core` and `WTelegramClient 4.4.9`. Namespace stays `FreeMusicFinder`, so every type keeps its name and tests do not change.
- `InternalsVisibleTo`: `Noctis`, `FreeMusicFinder.Tests` and later `Noctis.Mobile` (most Finder types are `internal`).
- Desktop `src/Noctis` keeps `FreeMusicPlugin` (the plugin shell), the Avalonia parts and the `plugin.json`/`LICENSE` items in `Noctis.csproj`, and references `Noctis.Finder`.
- Already in `Noctis.Core` (no work needed): `DebugLog`, `DebugLogger`, `OnlineAccess`, `OfflineModeException`, `LibraryExclusions`, `ILibraryService`, `Track`, `Album`.
- **Desktop-only blockers** used by core files: `src/Noctis/Services/Search/OnlineMusicSearch.cs` (`OnlineDownloadStep`, `OnlineDownloadState`, `IOnlineMusicSearch`, `IDownloadQueue`, static hub `OnlineMusicSearch` with `PublishSavedFile`/`Current`/`RequestOpen`/`FilesInPlayer`), `Noctis.App.Services`, `PlayerViewModel.StandIn`, `MainWindowViewModel`/`SidebarViewModel`.

## Per-file map

Legend: **A** = move as-is. **B** = move after a small change (said exactly). **C** = stays desktop-only.

| File | Lines | Verdict | What |
|---|---:|---|---|
| ArtistCatalog.cs, ArtistCatalog.Browsing.cs | 648 | A | pure logic (+ TL types) |
| ArtistIdentity.cs | 18 | A | |
| ArtistPicture.cs | 48 | C | Avalonia controls, `Bitmap`, `Dispatcher` (UI). Phone writes its own |
| ArtistPopularity.cs, ArtistPortraits.cs | 209 | A | |
| AudioFileTransfer.cs | 193 | B | uses `OnlineDownloadStep.Receiving` -> needs the contract types moved first (step 1); `OnlineAccess`/`DebugLog` already Core |
| BotJoin.cs, BotPacing.cs, BotSelections.cs, BotText.cs, BotTimings.cs | 456 | A | |
| BotLibrary.cs | 394 | B | `OnlineDownloadStep` (step 1) |
| BotQuality.cs | 276 | A | `DebugLog`/`DebugLogger` are Core |
| CatalogArtwork.cs, CatalogEditions.cs, CatalogLocks.cs, CatalogRecommend.cs, CatalogSearch.cs | 721 | A | |
| CatalogPages.cs | 179 | B | line 45 default `Noctis.App.Services?.GetService(...)` -> make the `library` argument required (the plugin passes it); `ILibraryService`/`Album` are Core |
| CheckThese.cs | 76 | A | static `Load(path)`; path comes from host |
| DiscoverBaseline/Evaluation/Feedback/Identity/Replay.cs | 1,030 | A | |
| DiscoveryActions.cs | 98 | A | `DebugLog` only |
| Downloader.cs | 200 | B | `OnlineDownloadStep` (step 1); default folder uses `SpecialFolder.MyMusic`/`UserProfile`: phone must pass its own folder (Android `MyMusic` is empty) -> add a `defaultRoot` parameter, desktop passes today's value |
| Downloads.cs | 667 | B | `OnlineDownloadStep` + `OnlineMusicSearch.PublishSavedFile` (desktop static hub) -> inject an `Action<string>` "file saved" callback; desktop passes `OnlineMusicSearch.PublishSavedFile` |
| FinderDownloadQueue.cs | 53 | B | implements `IDownloadQueue` (step 1) |
| FinderOnlineSearch.cs | 573 | B | implements `IOnlineMusicSearch` (step 1); `PlayerViewModel.StandIn` (desktop VM) -> inject a `Func<..., Track>` stand-in factory; `PublishSavedFile` -> same callback as Downloads |
| ForYou.cs, ForYouCache.cs | 1,253 | A | `OfflineModeException`/`DebugLog` Core. (ForYou *view* code lives outside this folder) |
| FreeMusicPlugin.cs | 422 | C | the plugin shell: `IPluginHost`, Avalonia window/Dispatcher (9), `TelegramSignInDialog`, `OnlineMusicSearch.Current/RequestOpen`, `LibraryExclusions`. Later split: a UI-free `FinderSession` (builds the objects in `Initialize`, lines 82-160) can move as B |
| ITelegramSignIn.cs | 21 | A | interface; phone implements it with its own page |
| LastFmSimilar.cs, LyricSearch.cs | 309 | A | |
| MusicSources.cs | 517 | B | `OnlineDownloadStep.Asking/Switching` (step 1) |
| PartialAudio.cs | 103 | A | static `Folder`, set by host |
| PlaylistCatalog*.cs (4 files) | 746 | A | `DebugLog` only |
| PlaylistLibrary.cs | 98 | C | `Application.Current` lifetime, `MainWindowViewModel`, `SidebarViewModel`, `Dispatcher`. Core uses it through `new PlaylistLibrary()` in SpotifyPlaylists/ForYou ctor args -> extract an `IPlaylistLibrary` interface (B for callers) |
| PlaylistTransfers.cs | 148 | A | |
| Proxy.cs | 590 | B | Windows system proxy via `Microsoft.Win32.Registry` (line 166). It already falls back to `HttpClient.DefaultProxy` off Windows (line 164), so it compiles on net10.0 only if the Registry call is guarded with `[SupportedOSPlatform]`/`OperatingSystem.IsWindows()` (already true) and `Microsoft.Win32.Registry` resolves; on net10.0 it does (in-box, throws PlatformNotSupported off Windows). Phone: system/VPN proxy = `DefaultProxy`; MTProto proxy string field as today |
| QualityDefault.cs, ResultsLock.cs | 65 | A | |
| Shortcut.cs | 37 | C | `Avalonia.Input` keys (desktop shortcut) |
| SimilarArtists.cs | 154 | A | |
| SpotifyPlaylists.cs | 476 | B | takes `PlaylistLibrary` -> `IPlaylistLibrary` |
| StreamCache.cs | 175 | B | `SpecialFolder.ApplicationData/LocalApplicationData` fallback -> phone passes `host.DataDirectory` (`StreamCache.Folder(dir)` already exists; drop/guard the AppData fallback) |
| Telegram.cs | 1,159 | B | `OnlineAccess.WaitAsync`/`DebugLog` Core; packed defaults read from `AppContext.BaseDirectory` (line 31) -> on Android that is not a readable folder: pass the packed file stream/path from the host (Android asset) |
| TelegramDefaults.cs | 57 | A | |
| TelegramMusic.cs | 672 | B | `OnlineDownloadStep` (step 1) |
| TelegramStore.cs | 207 | A | DPAPI only when `IsWindows()`, else plain file with `chmod 600` (line 117). On Android the app-private dir is the protection; optional later: Android Keystore wrap |
| TrackLinkLookup.cs | 24 | A | |
| plugin.json, LICENSE | - | C | embedded/linked by `Noctis.csproj` (lines 220, 231) |

Count: A = 36 files, B = 15, C = 5 (+2 non-code). After step 1 most B items are one-line using changes.

## What the Finder needs from the host (`IPluginHost`, `src/Noctis.Plugins.Abstractions/NoctisPlugin.cs`)

| Used in FreeMusicPlugin | Phone host supplies |
|---|---|
| `host.DataDirectory` (all state files) | `Android Context.FilesDir/finder` (app-private, survives updates, removed on uninstall) |
| `host.Settings.GetString/GetBool`, `Settings.Changed` | a small settings store over the mobile app's settings (same keys: proxy, allowMp3, preferredQuality, firstBot, lastFm key, download folder, artistCatalogOnPages) |
| `host.Log` | `DebugLog.Write` |
| `host.RegisterTrackCommand(...)` | phone track menu entry (or no-op at first) |
| desktop statics: `OnlineMusicSearch.Current/RequestOpen/FilesInPlayer`, `LibraryExclusions`, `TelegramSignInDialog` | phone search page + its own sign-in page implementing `ITelegramSignIn` |

Simplest: the phone does **not** load the plugin; it builds a `FinderSession` (see FreeMusicPlugin row) with a tiny `IFinderHost { DataDirectory; Settings; Log }`.

## State files (all under `host.DataDirectory`) and Android

`telegram.dat` (session, TelegramStore), `telegram-defaults.private.json`, `bot-quality.json`, `bot-timings.json`, `hunters-search-until.json`, `source-limits.json`, `bot-songs.json`, `download-queue.json`, `playlist-queue.json`, `spotify-playlists.json`, `check-these.json`, `for-you-hidden.json`, `artist-catalog/artist-choices.json`, `stream-partial/`, stream cache folder. Desktop: the plugin's data folder under the Noctis profile.
Android: all of these go in the app-private dir; **never copy the PC's `telegram.dat`** (own login, phone + code). Downloads go to the app's music folder (or MediaStore later), not `MyMusic`. Packed Telegram app defaults ship as an Android asset.

## Tests: `tests/FreeMusicFinder.Tests`

Console test runner (`Program.cs`), references `src/Noctis/Noctis.csproj` plus `Avalonia.Headless` and `Avalonia.Skia` 12.1.3; uses `InternalsVisibleTo` from Noctis. UI-touching tests (Avalonia): ArtistPortraitTests, ForYouDiscoverTests, ForYouLayoutTests, NativeForYouTests, PlaylistWindowTests, SearchPreview, TelegramSignInTests, UnifiedSearchTests, LegacyUi/*. `Live*` tests need a real login (env-gated). Keep the project referencing `Noctis` (which will reference `Noctis.Finder`) so nothing in the tests changes; add `InternalsVisibleTo("FreeMusicFinder.Tests")` to `Noctis.Finder`. A later split into a non-UI `Noctis.Finder.Tests` is optional.

## Dependency order (each step: build desktop, run the Finder suite + FastGates, behaviour identical)

1. Move the contract types out of `src/Noctis/Services/Search/OnlineMusicSearch.cs` (`OnlineDownloadState`, `OnlineDownloadStep`, `IOnlineMusicSearch`, `IDownloadQueue`, and the static hub if it has no Avalonia, else leave the hub) into `Noctis.Core`, same namespace `Noctis.Services.Search`. Zero behaviour change.
2. Add the three injections inside the desktop project: `CatalogPages` library required; `Downloads`/`FinderOnlineSearch` "file saved" callback + stand-in factory; `IPlaylistLibrary`; `Downloader` default root; `StreamCache` without AppData fallback; `Telegram` packed-defaults path parameter. Desktop passes today's values.
3. Create empty `src/Noctis.Finder` (net10.0, WTelegramClient 4.4.9, refs Noctis.Core, InternalsVisibleTo), add to the solution, `Noctis` references it. Move WTelegramClient package ref there.
4. `git mv` the A files in leaf-first batches: (a) text/time helpers: BotText, BotTimings, BotPacing, BotSelections, BotJoin, QualityDefault, ResultsLock, TrackLinkLookup, TelegramDefaults, TelegramStore, ITelegramSignIn; (b) Proxy, Telegram, AudioFileTransfer, PartialAudio, BotQuality, TelegramMusic, MusicSources, BotLibrary; (c) catalog: Artist*, Catalog*, CheckThese, LastFmSimilar, SimilarArtists, LyricSearch, Playlist* (not PlaylistLibrary), SpotifyPlaylists, PlaylistTransfers; (d) Downloads, Downloader, FinderDownloadQueue, StreamCache, FinderOnlineSearch; (e) Discover*, DiscoveryActions, ForYou, ForYouCache. Build + suite after each batch.
5. Extract `FinderSession` from `FreeMusicPlugin.Initialize` (object wiring only) into `Noctis.Finder`; the plugin calls it.
6. Only then: `Noctis.Mobile` references `Noctis.Finder` and supplies `IFinderHost` (app-private dir) + its own sign-in/search pages.

Check for each move: the Android build must not pull Avalonia.Desktop/Windows TFM; `Noctis.Finder` stays plain `net10.0` (desktop is `net10.0-windows10.0.19041.0` on Windows, which can reference net10.0).

## Forwarded / re-sent document ids

Question: does a forwarded audio message keep the same `Document.id` (and `access_hash`)? Does a bot's cached re-send keep the id?

- **`Document.id`: very likely yes, not proven by a live test here.** Evidence: the Bot API says `file_unique_id` is "supposed to be the same over time and for different bots" (core.telegram.org/bots/api#file), and Pyrogram's `file_id.py` shows a document's `file_unique_id` is just the type plus `media_id` (= `Document.id`). So the same stored file has the same `Document.id` for every account and over time; forwarding and re-sending a cached file do not upload a new file, so they carry the same id. No Telegram page says "forward keeps the id" in those words.
- **`access_hash`: not confirmed.** The Bot API `file_id` (which packs `id` + `access_hash` + `file_reference`) differs between bots, and mtcute's guide says file ids are per-user. So `access_hash` may differ per account. Within one account it is probably the same, but nothing official says so.
- **`file_reference`: changes with context** (core.telegram.org/api/file-references): it is tied to the message where the media was seen, expires, and must be refetched from that message.
- Rule for #255 sync: key songs by `Document.id` only; always download using the `access_hash` + `file_reference` from the message you just fetched in *your* chat/channel (as BotLibrary already does, lines 208-219, 352). Before relying on it, do the VERIFY step of the plan: one real forward, compare `id`/`access_hash` on both sides (LiveForwardTest.cs already has the forward path; it needs an explicit yes because it sends).

Sources: https://core.telegram.org/bots/api#file , https://core.telegram.org/api/file-references , https://github.com/pyrogram/pyrogram/blob/master/pyrogram/file_id.py , https://mtcute.dev/guide/topics/files

## Top risks

1. `OnlineMusicSearch` static hub and contract types sit in desktop `src/Noctis`; core files depend on them everywhere. Step 1 must move them without dragging Avalonia.
2. Internal visibility + one namespace across two assemblies: partial classes (ArtistCatalog, PlaylistCatalog) must move together, and tests using reflection on private fields (LiveForwardTest `_defaults`) must still find them.
3. Android paths/assets: `AppContext.BaseDirectory` packed defaults, `SpecialFolder.MyMusic`, AppData stream cache all silently point to wrong/empty places on Android; must be host-injected before the phone uses the library. `telegram.dat` is plain (no DPAPI) off Windows.
