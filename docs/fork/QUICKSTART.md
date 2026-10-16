# Noctis Plus: first local preview

This first version uses the stable Noctis 1.6.3 base. Finder 2.11.1, True Shuffle 1.5.0,
WordLyrics plugin 1.2.4 and Lyric Motion 1.4.3 are included and enabled. Keep using their
existing controls; individual switches and settings are in Settings > Plugins. The
community-plugin switch controls extra plugins, not the four included features.

## Run locally

Extract `NoctisPlus-windows-x64.zip` to a new folder and run `Noctis.exe`. The executable
keeps its old assembly name for plugin compatibility; its product/window/tray name is
Noctis Plus. The package includes the .NET runtime and VLC. Windows 10 x64 (1809 or later)
is the minimum inherited from upstream; this preview is tested on the development PC,
not yet across a Windows 10/11 VM matrix.

Plus starts with an empty profile at `%APPDATA%\Noctis Plus`. Debug builds use
`%APPDATA%\Noctis Plus-dev`. No profile or login is automatically copied. Add music folders
in Settings to use your existing audio without duplicating it. Shared audio/sidecar files
still belong to the same music library: running lyric repair changes those shared files,
so do not run jobs in both players simultaneously.

Finder uses its existing proxy-aware network handling. Telegram sign-in and any personal
Last.fm key must be configured in this separate profile. Music downloads start only through
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
fresh publish directory. `-Installer -InnoCompiler 'C:\path\to\ISCC.exe'` also builds an
optional per-user Inno Setup installer, with a separate AppId and installation folder.
The build command is offline (`--no-restore`); it explains missing cached dependencies
instead of downloading them. A fresh developer machine needs a normal .NET restore first.

## Updates and remaining release work

Plus checks only `Alfamin/NoctisPlus` releases. No releases/404 means no update; real network
and rate-limit failures still surface. Official Noctis update assets and temporary installers
cannot be installed or cleaned up by Plus. The developer version manager uses the same pin.
The four included module versions currently update with a Plus build. Independent module
updates and native integration are later work.

This is a local preview, not a public release. Before publishing: choose licenses for the
included modules, create the Plus repository, compile/test the installer, and validate on a
clean Windows machine. Profile migration will be an explicit copy with a journal; it is not
implemented here. Recommendation engine changes remain deferred.
