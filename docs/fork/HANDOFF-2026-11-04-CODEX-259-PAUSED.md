# Noctis Plus — owner #259 PAUSED checkpoint (2026-11-04)

The owner said "stop and save the progress so far". Work is paused by request. Do not resume without a new instruction. This is a WIP checkpoint, NOT a validated build or finished Android app.

## Resume location
- Worktree: C:\Users\Alfamine\nplus-codex-257
- Branch: codex-recovery-257 (LOCAL ONLY).
- Last fully validated baseline: f678e92d. Read docs/fork/HANDOFF-2026-11-04-CODEX-RECOVERY-257.md for the combined Claude recovery, commits, build commands and cleanup evidence.
- The current checkpoint commit follows f678e92d; find it with git log -3 --oneline. Do not discard it or switch the shared main worktree.
- Main repo C:\Users\Alfamine\noctis-fork stays hotfix-271/b30fc8dd. Other worktrees and their source remain untouched.

## Owner intent / truthful completeness
#259 asks to FINISH the unfinished phone features: real native Telegram sign-in screens/service, shared Finder downloads and opt-in Telegram cross-device sync with desktop integration. Do not claim that unwired engines or mock tests are complete features.
The latest owner question asked whether Android matches Google Play and is complete. Answer given: NO; this is a development Plus fork based on upstream Android source, not compared against the Play-distributed binary. Local playback was checked; phone sign-in/Finder/sync are not complete.
Owner then explicitly paused all work. No push, tag, release, installs or additional tests were performed after that stop request.

## Changes saved in this WIP checkpoint
1. New UI-free src/Noctis.Finder project (net10.0, Core + WTelegramClient4.4.9), added to solution and desktop/mobile project references. Shared internals granted to desktop/mobile/Android/test hosts; Core grants Finder access.
2. git-moved almost all desktop Features/FreeMusicFinder backend .cs files into Noctis.Finder. Desktop retains FreeMusicPlugin, ArtistPicture, Shortcut and PlaylistLibrary host adapter; manifests/licenses remain where they were. Namespaces unchanged. Shared PlaylistMatcher/LocalSong/IPlaylistLibrary split into Finder/PlaylistLibrary.cs.
3. Moved UI-free ProgressiveAudioBuffer/ProgressiveTransfer into Finder. VLC-dependent implementations remain desktop; new ProgressiveLeaseExtensions restores CreateInput() as a desktop extension. This is not an Android progressive Media3 implementation.
4. Moved PendingStream record into Core (kept Noctis.ViewModels namespace for compatibility). Core OnlineStandIn.Create provides old stable queue identity. Desktop StandIn and shared FinderOnlineSearch use it.
5. CatalogPages default library no longer reads desktop App globals; desktop FreeMusicPlugin passes its own library explicitly.
6. New FinderSession owns one host's account, catalog, bot sources, downloads, pages and playlist service. First draft incorrectly called nonexistent TrackLinkLookup.ResolveAsync (failed build). Corrected to the existing FirstAsync pipeline with catalog + fresh Deezer + inline fallback like desktop. SetProxy now validates existing ProxyChoice and handles URI/IPv6 rather than splitting ':' naively. THESE LATEST FIXES HAVE NOT BEEN BUILT.
7. TelegramAccount now supports explicit packed .dat deployment defaults; added cancellable internal Start/Continue overloads retaining existing interface methods; cancel/timeout disposes pending client; ResetPendingLoginAsync preserves an authenticated account. No real account was used.
8. New mobile Services/Telegram/PhoneTelegramSignIn adapts FinderSession to IPhoneTelegramSignIn. Known delivery length unavailable => manual Continue. Missing app credentials gets explicit short error; logout pauses downloads. Service is NOT constructed in Android yet.
9. TelegramPaneViewModel now exposes IsPhoneStep/IsCodeStep/IsPasswordStep and short missing-setup/proxy errors. New native Views/Settings/TelegramPane.axaml + code-behind uses existing GroupedStyles for phone/code/password/sign-out/proxy/busy/cancel/error UI. Pane is NOT wired into Settings or Shell yet; no live screenshot of it.

## Validation boundary (important)
- Before FinderSession and latest phone changes: extracted Noctis.Finder built successfully (6 warnings/0 errors); desktop test project built (1071 warnings/0 errors). Log artifacts/plus/extract-desktop.log.
- First FinderSession build FAILED with missing ResolveAsync. See artifacts/plus/finder-session-build.log. Source was corrected afterward but no build run, so that log is historical and red.
- No tests/gates/emulator/UI checks have run against THIS WIP checkpoint. No promise it compiles; first resume action is build/fix.
- Previous f678e92d baseline: full gates codex-257b 9503 total/0failed/83skipped, Finder1760/0, TelegramSync56/0 fake only, Skia focused3/0, Android Debug x64 build0errors. Synthetic emulator local WAV play/pause/resume verified. These results do NOT validate current edits.
- No real Telegram bot request, channel or sync upload was made in this continuation. No sessions copied, no private credentials added to source, no D: writes.
- TEST remains on previous validated publish-codex-257b. Current WIP was NOT staged there. Installed app untouched and left running. Isolated emulator remains off. No computer-use session is open.

## Next implementation steps, when resumed
A. Build Finder + Mobile + desktop tests, then Finder checks. Adapt real architecture/source guards for moved files (do not weaken behavior tests). Review extraction for desktop globals, private defaults and publication paths; keep attribution/license files.
B. Wire phone host:
- ShellViewModel plus host property and TelegramPaneViewModel; SettingsPane.Telegram and boolean/title notifications; Telegram row + TelegramPane host in SettingsPage.axaml.
- Construct FinderSession in AndroidApp.axaml.cs using app NoBackupFilesDir for its OWN Telegram session. Own app-private music/cache paths. Never copy desktop/test sessions.
- Package app deployment defaults only through an explicit ignored AndroidAsset path/MSBuild property; load packed defaults via that path. No hardcoded API id/hash in public code. Current PhoneTelegramSignIn reports missing setup until packaging is wired.
- Native Search results/play/download action and download queue controls/progress; direct backend calls, no desktop window/reflection. Use existing local-first mobile search and latest-query cancellation. Prevent tap duplication.
- For playback initially ensure full transfer completion via ProgressiveTransfer.WaitForDownloadAsync before Media3 reads ordinary files; true progressive requires a Media3 DataSource, not just a growing path. Explain fallback if kept; do not claim streaming parity.
- Saved-file import through ILibraryService.ImportFilesAsync; private downloads need AndroidFileSystemSource non-SAF local path support and SafTrackFileAccess ordinary sidecar/audio support (currently SAF only). Preserve selected folders and scanner concurrency.
- Phone IPlaylistLibrary implementation over mobile library/persistence (serialized updates/expected-id safety), no desktop singleton.
C. Finish sync blockers BEFORE automatic real uploads (existing docs REVIEW-255-STAGE1.md):
- M2 receiver-relative future timestamp clamp can diverge depending on read time. Use canonical shared anchor/validated server timestamp, add adversarial read-time tests. An idea considered but NOT coded: ITelegramDrive server-clock anchor, log Record from it, receiver validation against stable message.DateUtc and canonical ledger application. Re-evaluate design carefully.
- M7 new installation needs fresh device id and initial pull/snapshot before per-device counter writes.
- Real WTelegram ITelegramDrive adapter using SAME FinderSession TelegramAccount connection; opt-in desktop/phone controller, durable upload queue and stable sendMedia random_id across ambiguous retries/restarts; channel/account guard. Existing per-instance upload dedup is not crash-proof exactly-once.
- Snapshot bootstrap/pruning UpTo, path sanitization, offline/storage/Wi-Fi policies, sidecar support, LAN+Telegram duplicate application, deletion policy. No auto-upload real library by default.
D. Phone TrueShuffle engine exists but not wired; native UI/modes needed. Online lyric source/timing phone integration remains open; no local WordLyrics model download. Android release signing needs owner's stable keystore; no temporary key release.
E. Real isolated checks after gates, ARM64 build for phones (previous APK is x64 emulator only). Phone Telegram sign-in requires its own account login; do not copy a TEST session to simulate it.

## Build / test / live setup
SDK C:\Users\Alfamine\dotnet-sdk\dotnet.exe
JDK C:\Users\Alfamine\android-tools\jdk-21.0.13.0.2+1
Android SDK C:\Users\Alfamine\android-tools\sdk
Use LOCAL copies artifacts/plus/tools/FastGates.ps1 and ShardedSuite.ps1, which point at this worktree. Original external FastGates hard-codes noctis-fork and would test wrong code.
PowerShell gates: powershell -NoProfile -ExecutionPolicy Bypass -File artifacts\plus\tools\FastGates.ps1 -Name <name> -Shards 10
Build Android Debug x64: dotnet build src\Noctis.Android\Noctis.Android.csproj -c Debug -p:RuntimeIdentifier=android-x64 -p:JavaSdkDirectory=<JDK> -p:AndroidSdkDirectory=<SDK> -v q
Isolated AVD NoctisPlusRecovery at ANDROID_AVD_HOME=C:\Users\Alfamine\android-tools\avd, port5580, no-window/no-audio/swiftshader. Only target emulator-5580. Synthetic WAV exists there under /sdcard/Music/NoctisRecovery. No owner data/login in emulator. Prior handoff has startup details. Shut emulator/helper down afterward.

## Standing rules for next session
Log every owner request in external LEAN-PLAN BEFORE work; DONE only after relevant real live check. D:\music read-only. Installed profile/session untouched. TEST sessions used only in place, one app owns them; preserve TEST settings/API/login. No push/tag/release/main merge without go; named-file staging only, no git add -A or force push. Native minimal English UI. No paid services/new API keys/model downloads. No CU/download/install while gaming; end computer-use immediately afterward. No subagents unless explicitly requested. Stop near requested usage boundary with honest checkpoint. Safe cleanup recovered about108GiB net; do not delete current artifacts/profiles or re-run blanket cleanup.