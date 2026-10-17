# Noctis Plus: source provenance

Noctis Plus is a fork of [Noctis](https://github.com/heartached/Noctis) by heartached, MIT licensed. The upstream
copyright notice stays in `LICENSE`; the Noctis Plus changes are MIT licensed too (second copyright line, 2026-10-16).
Our own changes are listed by file in Git history.

## Base

| Item | Value |
|---|---|
| Fork base | upstream tag `v1.6.3`, commit `ad41b97c7c00fa1fb9ea377fb091f4059e7c0021` (the version installed on the development PC) |
| Upstream main at fork time | `29a7269ca9ffd25d14a2c70d52df8e0744662dc8`, 15 commits / 6 files after v1.6.3, not merged (review at the next integration) |
| Remote names | `upstream` = heartached/Noctis; `origin` = the Noctis Plus repository once it is created by the owner |

## Code that comes into the fork from other repositories

| Feature | Source repository | Author / rights |
|---|---|---|
| Free Music Finder (catalog, downloads) | Alfamin/noctic-download-plugin, forked from Pr0nE/noctic-download-plugin (first commit 4d7ae1b) | Original code by Pr0nE, MIT. The owner, a maintainer, added the MIT LICENSE upstream in 1503a6c on 2026-10-17 (afternoon handoff). Alfamin's fork changes are MIT as well. This supersedes the initial permission-only status. |
| True Shuffle | Alfamin/TrueShuffle | Alfamin (owner), MIT (LICENSE added 2026-10-16) |
| Lyric Motion | Alfamin/LyricMotion | Alfamin (owner), MIT (LICENSE added 2026-10-16) |
| WordLyrics (plugin and program) | Alfamin/WordLyrics | Alfamin (owner), MIT (LICENSE added 2026-10-16) |

The owner chose MIT for their own modules and for the Noctis Plus changes ("fine for anyone to use", 2026-10-16).
The bundled module binaries are the pinned builds listed in `bundled-plugins/manifest.json`; the license files were
added afterwards without changing their code. Finder's bundled `WTelegramClient.dll` is WTelegramClient by Wizou (MIT).

## Third-party dependencies to review before distribution

From the v1.6.3 project files. Licenses are recorded at the time each one is reviewed; nothing new is bundled yet.
Avalonia 12.1.3, Avalonia.Labs.Lottie 12.0.2, CommunityToolkit.Mvvm 8.4.2, DiscordRichPresence 1.6.1.70,
LibVLCSharp 3.10.1 + VideoLAN.LibVLC.Windows 3.0.23.1, Microsoft.Data.Sqlite 8.0.31 + SQLitePCLRaw 2.1.13,
Microsoft.Extensions.DependencyInjection 8.0.1, NAudio 2.3.0, Net.Codecrete.QrCodeGenerator 3.2.1, SkiaSharp 3.119.4,
System.Security.Cryptography.ProtectedData 8.0.0, TagLibSharp 2.3.0, Tmds.DBus.Protocol 0.95.1, Whisper.net 1.9.1,
YamlDotNet 15.3.0; the CI build also bundles ffmpeg. Feature modules may add WTelegramClient 4.4.9 (Finder) and the
WordLyrics program's Python runtime and models, which are installed separately.

## Native Lyric Motion (2026-10-17)

The native word renderer is owned by Noctis's lyrics templates and its existing word clock.
It retains the settings id/keys from Alfamin/LyricMotion 1.4.3 and its MIT-licensed LetterLayer, Spring/Follower, FillReader, tuning and easing. Native word controls own the text layers and register directly; the old Director/Surface/Rig discovery and reflection are not used. LetterLayer retains its geometric ancestor transforms for pixel alignment. The source licence is retained under
`src/Noctis/Features/LyricMotion/LICENSE` and delivered as `features/dev.moshi.lyricmotion/LICENSE`.
The old pinned DLL remains in this repository for provenance, but is excluded from new packages.

## Native True Shuffle (2026-10-17)

The app now owns shuffle sessions, rolling queue refill and player menus. The MIT domain logic
from Alfamin/TrueShuffle (57114ad) remains in `src/Noctis/Features/TrueShuffle`; the original
licence is delivered under `features/dev.moshi.trueshuffle/LICENSE`. The bundled DLL/manifest
entry was removed. Existing settings and `plugin-data/dev.moshi.trueshuffle` history keep their
identity. The native service uses typed player/library calls and explicit queue intent; the
plugin's reflection bridge, injected menus, visual-tree scans and attach timers are not used.

## Native WordLyrics (2026-10-17)

Noctis owns the song menu, pill dialogs, settings migration and typed library/download events.
The MIT queue/backend protocol from Alfamin/WordLyrics (local 1.6.4 source) is retained under
`src/Noctis/Features/WordLyrics`, with the owner's copyright/MIT notice delivered under
`features/dev.moshi.wordlyrics/LICENSE`. The original plugin DLL/manifest were removed.
Existing settings and `plugin-data/dev.moshi.wordlyrics` checkpoints keep their identity.
Python and AI models remain separately installed; they are not embedded in Noctis. The native
background entry forbids package/model downloads. Explicit setup asks for consent first.
The MMS model's separate CC-BY-NC 4.0 terms remain in the standalone backend's model notices;
the MIT licence on the native integration does not change those model terms.
