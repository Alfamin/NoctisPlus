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
| Free Music Finder (catalog, downloads) | Alfamin/noctic-download-plugin, forked from Pr0nE/noctic-download-plugin (first commit 4d7ae1b) | The original code is by Pr0nE. Neither repository has a license file. Pr0nE gave permission to include and redistribute his code in Noctis Plus (told to us by the owner, 2026-10-16). It is shipped with that permission only: it is **not** MIT, and reusing it elsewhere needs its authors' permission. A license file in his repository would change that. |
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
