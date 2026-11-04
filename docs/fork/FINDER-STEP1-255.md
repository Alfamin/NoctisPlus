# #255 step 1 done: online search contract types now in Noctis.Core

2026-11-04 22:15, branch `android-plus-finder1`. Step 1 of the move order in FINDER-MOVE-MAP-255.md.

## What moved

- `src/Noctis/Services/Search/OnlineMusicSearch.cs` -> `src/Noctis.Core/Services/Search/OnlineMusicSearch.cs` (`git mv`, file unchanged, 233 lines).
  It has no Avalonia (its only outside type is `Noctis.Models.Track`, already Core), so the whole file moved, the static hub included:
  `OnlineArtist`, `OnlineAlbum`, `OnlineSong`, `OnlinePlaylist`, `OnlineSearchResult`, `OnlineDownloadState`, `OnlineDownloadStep`,
  `OnlineDownloadStatus`, `IOnlineMusicSearch`, `StreamedAdd`, `DownloadEntry`, `IDownloadQueue`, static `OnlineMusicSearch`
  (`FileSaved`/`PublishSavedFile`, `FilesInPlayer`, `Current`/`CurrentChanged`, `OpenRequested`/`RequestOpen`).
- Namespace stays `Noctis.Services.Search`: no other file changed, no type forwarding needed (nobody loads these by assembly-qualified name).
- `src/Noctis.Core/Noctis.Core.csproj`: added `InternalsVisibleTo FreeMusicFinder.Tests`, because `OnlineMusicSearch.FileSaved`/`PublishSavedFile`
  are `internal` and StreamCacheTests subscribes to `FileSaved`. `Noctis` and `Noctis.Tests` already had Core's internals.
- No behaviour change; Core still has no Avalonia reference.

## Results

| Check | Result |
|---|---|
| `dotnet build src/Noctis/Noctis.csproj -c Release -v q` | 0 errors (188 warnings, as before), 26 s |
| `dotnet build tests/FreeMusicFinder.Tests` (Release) | 0 errors, 37 s |
| `dotnet FreeMusicFinder.Tests.dll -noLogo` | **1760 ok, 0 wrong**, exit 0, 32 s |
| `dotnet build tests/Noctis.Tests` (Release, build only, suite not run) | 0 errors, 24 s |

Not run: FastGates, the full Noctis.Tests suite, the Android/server heads (they only gain a Core file).

## What is left (map steps 2-6)

2. The injections inside desktop: `CatalogPages` library required; `Downloads`/`FinderOnlineSearch` file-saved callback (could now stay a direct
   `OnlineMusicSearch.PublishSavedFile` call, since the hub is Core; then only `PlayerViewModel.StandIn` needs a factory); `IPlaylistLibrary`;
   `Downloader` default root; `StreamCache` without AppData fallback; `Telegram` packed-defaults path.
3. Empty `src/Noctis.Finder` (net10.0, WTelegramClient 4.4.9, refs Core, InternalsVisibleTo Noctis/FreeMusicFinder.Tests/Noctis.Android).
   Note: `Noctis.Finder` will need Core's internals too (`PublishSavedFile`), so add `InternalsVisibleTo Noctis.Finder` to Core then.
4. `git mv` the A files in batches (a)-(e). 5. `FinderSession`. 6. Mobile host.

## Step 2 (first slice): Downloader default root injectable

2026-11-04 22:24, branch `android-plus-finder2`. Smallest piece of map step 2 only.

- `src/Noctis/Features/FreeMusicFinder/Downloader.cs`: new `internal static Func<string?>? DefaultRootOverride`.
  `DefaultFolder` order is now: first library folder -> `DefaultRootOverride` (if set and non-empty) -> MyMusic -> UserProfile.
  Nobody sets it on desktop, so desktop behaviour is identical. The Android host will set it (no MyMusic there).

| Check | Result |
|---|---|
| `dotnet build src/Noctis/Noctis.csproj -c Release -v q` | 0 errors (188 warnings, as before) |
| `dotnet build tests/FreeMusicFinder.Tests` (Release) | 0 errors |
| `dotnet FreeMusicFinder.Tests.dll -noLogo` | **1760 ok, 0 wrong**, exit 0 |

Still left in step 2: `CatalogPages` library required; `Downloads`/`FinderOnlineSearch` file-saved callback (`PlayerViewModel.StandIn`
factory); `IPlaylistLibrary`; `StreamCache` without AppData fallback; `Telegram` packed-defaults path.
