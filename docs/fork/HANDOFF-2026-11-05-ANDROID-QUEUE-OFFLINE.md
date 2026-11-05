# Android queue / offline checkpoint — 2026-11-05

Read LOCAL-CHECKPOINT.md first for the newest terminal gate/quota state. This supersedes the
old 265-269 handoff's red gate and quota hold. Worktree `C:\Users\Alfamine\nplus-codex-257`,
branch `codex-android-262`; original `noctis-fork` untouched. Inspect git/process state before
resuming; do not duplicate an active build.

## Owner rules

- Actual quota start/end each bounded block, <=5min apart. Start `docs/fork/tools/Start-QuotaWatch.ps1`;
  inspect `artifacts/plus/tools/quota-status.json`. **>=90% = WRAP-UP ONLY**, no new builds/features/research.
- #268: preserve last earned reset until Nov5 evening (~19:00 Tehran or later) window nearly
  exhausted. Save first, redeem via supported service only when eligible, verify actual quota.
  No purchases, subscription changes, UI automation or eligibility bypass. Earlier windows reset naturally.
- #264/#266: systematic portable PLUS parity first; upstream beta complaints later. #265 Home/ForYou high.
  Log owner requests BEFORE work in external LEAN-PLAN. DONE requires actual app evidence and full gates.
- Exact named-file commits/pushes on dedicated branch only; no main/native merge/tag/release.
  D:\music read-only; preserve all profiles/logins; no session copying. Phone owns its login.
  Desktop TEST account only in place. No paid services/model downloads. End computer use immediately.

## Saved work

- f14d1f61 disposes leaked SidebarGroupReorder test windows; production animation/assertions unchanged.
  Class24/0; full270a9562/0failed/83skipped, Finder1760/0. Old269c red gate resolved.
- d6db3262 native online queue integration, 477d758b Android wiring + isolated diagnostic source.
  Search and ForYou Play keep remaining list order, resolve typed pending stand-ins before Media3,
  latest click/current request wins; pause while preparing holds position and pause intent.
  Native Next/Previous/queue/shuffle/lockscreen share this path. Stale answers/errors ignored.
  Exact next4 QUEUE POSITIONS requested, next2 POSITIONS fetched, including local/cached positions;
  10s settle on replacement/manual queue edits/shuffle. Transient stale work trimmed/cache kept;
  explicit saves remain independent. Queue persists provider metadata only, restores paused without requests.
  Finder accepts only validated known-provider IDs on restore. Rejected ForYou song/artist pruned
  from queue/history/repeat/shuffle source. Full271b **9570/0failed/83skipped**, Finder1760/0.
- 85b9627e native Settings Offline mode (same existing AppSettings key), startup gate held until
  choice read, Android internet metadata/lyrics/scrobbling HttpClients wrapped in OnlineAccessHandler.
  Cached lyrics still available; deliberate offline miss silent; no login deletion. Pending playback
  retains selection/pause intent across offline interruption and quick off/on. Focused15/0.
  Initial x64/ARM64 builds0errors (38/68 warnings). Full272a predates final duplicate-ahead correction.
- 04143934 follow-up: request-start state in window fingerprint instead of forced
  invalidation prevents duplicate ahead asks when recovery and current-start callbacks race.
  Added a reordered-notification regression plus idempotent player disposal after the regression
  exposed a second-dispose exception. Finalfocused272c54/0 includes all MobileSettings tests.
  Old separator-count assertion16→17 updated for the new row, geometry checks unchanged.

## Real Android evidence (not real Telegram delivery)

AVD NoctisPlusRecovery/emulator-5580; separate `com.alfamin.noctisplus.auditqueue271` unsigned-in
profile, preserved. Debug + NoctisOnlineQueueAudit=true + exact package ID required; Release never
includes diagnostic transport. Seven synthetic40s MP3s, private distinct per-song paths copied
from synthetic probe.mp3; no bot messages/remote audio/session copy. Existing audit262 profile untouched.

- Rapid1→3: PREPARE1/PREPARE3/READY3 only, native loading bars and selected3. After settle FETCH4/5,
  ASK6/7. Native queue shows ordered remaining songs; Next changes current; Clear empties UpNext.
- Actual Media3 pause after Next4: position2889ms unchanged over2s. Distinct3/4 files confirmed.
- Force-stop/restart restored selected2 PAUSED, no PREPARE/FETCH/ASK, only current WINDOW.
- Offline ON during cached playback: position34374→37289ms continued. Restart retained toggle ON.
  Tap uncached1: WINDOW only, no request. OFF: PREPARE1/READY1 and PLAYING without second tap.
  That trace exposed duplicateASK4/5 on recovery. Final04143934 LIVE recheck: offline selected2,
  waited11s, switched online; exactly one PREPARE2, FETCH3/4, ASK5/6, READY2, native playing2.
  No duplicate asks/fetches. Evidence offline272c-recovery.log and offline272c-resumed.png.
- Exact final x64 binary (0errors/40warnings, both Debug audit flags) repeated REAL production
  ProgressiveDataSource + Media3 MP3/FLAC decode: first audio at40% received, pause held, starvation
  stable/no EOF, resume without restart (16201ms MP3/13978ms FLAC), completed seek and collision-renamed
  closed-before-completion reopen pass. Logs android-272c-final-{mp3,flac}.log. This closes the old
  final-guard-versus-APK evidence gap; still synthetic transfer, not Telegram delivery.
- Evidence: artifacts/plus/queue271*.png, queue271b-live.log, offline272*.png, offline272-live.log.
  Latest emulator CLOSED. No Windows computer-use runtime active. D:\music not written.

## Build/check commands

SDK `C:\Users\Alfamine\dotnet-sdk\dotnet.exe`; JDK `C:\Users\Alfamine\android-tools\jdk-21.0.13.0.2+1`;
AndroidSDK `C:\Users\Alfamine\android-tools\sdk`. Use --no-restore, -m:1; no concurrent MSBuilds sharing outputs.

Local ignored gate scripts target THIS checkout (external FastGates targets another worktree):
`powershell -NoProfile -ExecutionPolicy Bypass -File artifacts\plus\tools\FastGates.ps1 -Name <unique> -Shards 10 -NoPublish`.
Inspect ALL SHARDS + both frame groups + Finder counts; GATES DONE alone is insufficient.
Keep emulator closed during heavy frame gates. Theme sweep ~208s, full gate ~260s normally.

Android Debug build: `dotnet build src\Noctis.Android\Noctis.Android.csproj -c Debug --no-restore -m:1
-p:RuntimeIdentifier=android-x64 -p:JavaSdkDirectory=<above> -p:AndroidSdkDirectory=<above> -v q`.
For audit add ApplicationId=com.alfamin.noctisplus.auditqueue271 and NoctisOnlineQueueAudit=true.
ARM64 ordinary compile uses android-arm64 without audit flag. JNI duplicate-item warnings currently exist.

Launch AVD with ANDROID_AVD_HOME=C:\Users\Alfamine\android-tools\avd, emulator.exe -avd NoctisPlusRecovery
-port5580 (separate args), hostGPU, Vulkan off, no snapshot load/save, no audio/window, vsync120.
Wait for sys.boot_completed=1 before installing; wait-for-device alone is too early for package service.
Never override HOME. adb -s emulator-5580. Screenshots1080x2400; forward view_image with detail original
AND functions.image(...,"original") or coordinates will be scaled. Profile button center970/245.
Settings settles after1300ms; offline toggle center930/1970, close990/220 on this AVD.
End emulator after checks. Preserve package/profile. Do not copy any session to enable bot testing.

## Remaining

Duplicate-ahead regression/live recheck and final builds/gates are now complete; see terminal section below.
Continue parity map: native recovery audit (real own-phone Telegram required), rich
catalog/artist/album/playlist navigation/import, online timing and exact Plus Lyric Motion, opt-in sync
controller/jobs. Read LYRIC-MOTION-PHONE-255.md before rendering changes; desktop renderer unchanged.
Offline toggle covers internet Finder/metadata/lyrics/scrobbling, NOT a proven global block of LAN
account Media3 playback; native account sync/recovery still needs separate audit. No full-parity claim.
Physical120FPS/VRR, real phone bot delivery and paired-device sync remain unproven. No release.

## Terminal result — 16:24 Tehran (supersedes pending notes)

Source04143934: final **codex-272c GREEN: 9576 tests,0failed,83skipped**, both isolated frame groups
19/0 and50/0; Finder1760/0. Full run286s including build. Final x64 Debug0errors/40warnings;
ordinary ARM64 Debug0errors/68warnings. Focused54/0 and real native UI/recovery/MP3/FLAC evidence above.
Earlier272a separator-count failure is superseded. No production assertions weakened.

Owner#267 quota cutoff reached90%five-hour/45%weekly at16:22: WRAP-UP ONLY; no new work/build/research
started after that read. Only observed already-running272c and saved results. Natural reset19:09:36 Tehran.
One earned reset remains reserved; evening condition not reached. Emulator closed; no CUA active.
Named docs commit and dedicated branch push follow this save; inspect git/remote to verify success.
No native/main merge, tag, release, profile/session change or model download. Android Plus remains partial.
