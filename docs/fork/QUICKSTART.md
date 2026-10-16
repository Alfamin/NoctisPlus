# Noctis Plus

Noctis Plus 1.6.3.1 uses the stable Noctis 1.6.3 base. Finder 2.11.1, True Shuffle 1.5.0,
WordLyrics plugin 1.2.4 and Lyric Motion 1.4.3 are included and enabled. Keep using their
existing controls; individual switches and settings are in Settings > Plugins. The
community-plugin switch controls extra plugins, not the four included features.

## Install

Paste this into PowerShell (no administrator needed):

```powershell
irm https://raw.githubusercontent.com/Alfamin/NoctisPlus/main/scripts/install.ps1 | iex
```

It downloads the newest `NoctisPlus-v<version>-Setup.exe` through your VPN/system proxy,
continues an interrupted download when run again, checks it against the release's
`SHA256SUMS`, installs it for your user under `%LOCALAPPDATA%\Programs\Noctis Plus` and starts
it. You can also run the Setup yourself. Plus has its own installer identity, Start menu entry,
file-type registration and uninstaller; it never replaces or updates official Noctis.
Uninstalling removes the program and keeps `%APPDATA%\Noctis Plus`.

The portable `NoctisPlus-windows-x64.zip` still works: extract it to a new folder and run
`Noctis.exe`. The executable keeps its old assembly name for plugin compatibility; its
product/window/tray name is Noctis Plus. Both include the .NET runtime and VLC. Windows 10 x64
(1809 or later) is the minimum inherited from upstream.

## Your Noctis profile comes over by itself

The first time Noctis Plus starts with an empty profile, it copies your whole official Noctis
profile (`%APPDATA%\Noctis`, or `NOCTIS_DATA_DIR` if you set one) into `%APPDATA%\Noctis Plus`:
settings, library, stats (play counts, ratings, history, the yearly wrap), playlists and queue,
artwork, caches and models, plugin data, community plugins and sign-ins (Last.fm, Finder's
Telegram session and the others). A window shows the progress, then the player opens.

- Official Noctis is only read, never changed, moved or locked. If it is open, Plus waits and
  says so; the copy continues by itself once Noctis closes.
- Everything is copied into a staging folder first and moved into place only when complete.
  Closing the window or a crash simply resumes on the next start. The SQLite library database
  is copied as one consistent snapshot, including changes still in its write-ahead log.
- Adjusted on the way: official Noctis's default web-remote/local-API ports (9420/9421) become
  Plus's 9430/9431, the sync device id is renewed, and paths pointing into the old profile point
  into the new one. Official Noctis's crash logs, last session log and update bookkeeping go to
  `imported-from-noctis\`, so Plus never reports them or its update choices as its own.
- It runs once. A Plus profile that already has its own library is never imported over, and
  "Start fresh instead" (offered while waiting or after an error) is remembered.
  `%APPDATA%\Noctis Plus\.noctis-import\` keeps the state and a journal of what happened.
- Debug builds and a custom `NOCTIS_PLUS_DATA_DIR` never import on their own. For testing,
  `NOCTIS_PLUS_IMPORT_FROM=<folder>` imports from any folder.

Sign-ins are carried as they are, so avoid running official Noctis and Noctis Plus at the same
time: two apps using one Telegram session at once can make Telegram end that session for both,
and both would scrobble to Last.fm. Shared audio/sidecar files still belong to the same music
library: running lyric repair changes those shared files, so do not run jobs in both players
simultaneously.

Finder uses its existing proxy-aware network handling. Music downloads start only through
the existing download controls. WordLyrics's Python program and alignment models install
separately through the existing WordLyrics setup; they are not bundled or downloaded on
startup. Lyric Motion works with existing word-timed lyrics without Python.

WordLyrics setup launched by Plus inherits `WORDLYRICS_NO_NOCTIS=1` in the child process,
so the existing backend installer cannot offer/install official Noctis or write its plugins.
This is a process-local setting; it does not change the system environment or the behavior
of an installer you start separately from PowerShell.

## Build (PowerShell 5.1)

With .NET 10 SDK and the existing restored packages:

```powershell
.\scripts\Build-NoctisPlus.ps1 -DotNet 'C:\path\to\dotnet.exe'
```

Output: `release-artifacts\NoctisPlus-windows-x64.zip` and `SHA256SUMS`. Each build uses a
fresh publish directory. `-Installer -InnoCompiler 'C:\path\to\ISCC.exe'` also builds the
per-user Inno Setup installer `NoctisPlus-v<Version>-Setup.exe` (Inno Setup 6.5 or later),
with a separate AppId and installation folder, and lists it in `SHA256SUMS`. The Windows CI
workflow builds both on every push, then installs the Setup on the runner, starts it once over
a stand-in official profile to check the automatic import, reinstalls and uninstalls it, and
uploads the ZIP, Setup, `SHA256SUMS` and `install.ps1` as the build artifact.
The build command is offline (`--no-restore`); it explains missing cached dependencies
instead of downloading them. A fresh developer machine needs a normal .NET restore first.

## Updates and remaining release work

Plus checks only `Alfamin/NoctisPlus` releases. No releases/404 means no update; real network
and rate-limit failures still surface. Official Noctis update assets and temporary installers
cannot be installed or cleaned up by Plus. The developer version manager uses the same pin.
The four included module versions currently update with a Plus build. Independent module
updates and native integration are later work.

Releases are numbered on top of the Noctis base: 1.6.3.1, 1.6.3.2, ... Plugins still see the base
version (1.6.3). To publish one: bump `<Version>` in `src/Noctis/Noctis.csproj`, add
`docs/fork/releases/v<version>.md`, commit, then push the tag `v<version>`. The Windows workflow
builds, tests and install-checks that tag, and only then publishes the release with the Setup,
`SHA256SUMS` and the ZIP (the updater and `install.ps1` refuse a Setup without `SHA256SUMS`).
The Setup is not code-signed, so Windows SmartScreen may warn on first run ("More info" >
"Run anyway"). Licensing is in `PROVENANCE.md`. Recommendation engine changes remain deferred.
