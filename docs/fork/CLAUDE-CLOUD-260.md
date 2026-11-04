# Claude cloud assignment — owner #260, 2026-11-05

## Assignment and authorization

Implement the cloud-suitable parts of Noctis Plus Android and shared desktop integration. This is engineering work, not another research report. The owner has about US$20 of cloud credit and wants useful, maintainable implementation with minimal wasted usage. Do not sacrifice correctness. Choose safe defaults without routine questions, and keep working on independent tasks when a particular task is blocked.

Start from GitHub Alfamin/NoctisPlus, branch `cloud/android-plus-260`. It includes saved local source checkpoint `1d1a6f3b28c0b2acbcf0e6a3bf25dbe52dde6825` and this assignment. Verify ancestry before coding; do not start from the older main/native code. Create your own task branch from it. The owner authorizes implementation commits and pushing your dedicated task branch for handoff. NEVER push main/native, merge into them, force-push, tag, release, publish packages or upload to a store.

This assignment resumes the CLOUD portion of owner #259; the pause mentioned in the saved handoff does not block this task. Local Windows work stays paused until the owner resumes it. No source patch upload is needed when this branch is available.

## Read once

1. `docs/fork/HANDOFF-2026-11-04-CODEX-259-PAUSED.md`: authoritative WIP state and validation boundary.
2. `docs/fork/HANDOFF-2026-11-04-CODEX-RECOVERY-257.md`: earlier integrated recovery; its extraction-pending status is superseded by the newer checkpoint.
3. `docs/fork/ANDROID-PLUS-PLAN.md`, `PHONE-UI-255.md`, `FINDER-MOVE-MAP-255.md`: intent and relevant boundaries. Historical status/paths may be stale. Do NOT reintroduce automatic submission of a guessed five-digit login code.
4. `docs/fork/REVIEW-255-STAGE1.md`: remaining sync risks. Most findings were already fixed; inspect current code before doing anything again.

Create `docs/fork/CLOUD-260-STATUS.md` with a compact task list BEFORE implementation. Log new requirements there because the external Windows blueprint is unavailable. Distinguish implemented, automated-tested and LIVE-VERIFIED. Mock/headless/build success never establishes real-account or device success.

## Starting state

- Desktop Plus works; preserve its native lyric renderer, search ranking, bot-specific selection/fallback/quality, current-song priority, offline handling, settings and data formats.
- Android has the upstream Avalonia/Media3 shell. Synthetic local import/play/pause/resume was checked in an isolated emulator. It is NOT a complete phone Plus app or verified against the Play-distributed binary.
- WIP extracted `src/Noctis.Finder` (UI-free), shared playlist matcher, Core OnlineStandIn/PendingStream, and progressive buffer/transfer separated from desktop VLC adapters. FinderSession exists. PhoneTelegramSignIn and native TelegramPane are written but NOT connected to Android/Shell/Settings.
- Latest WIP was stopped before compilation/tests. First FinderSession build failed on nonexistent ResolveAsync; source was corrected to existing FirstAsync but not rebuilt. Begin with compilation, not more architecture research.
- Sync has 56 fake-Telegram tests. Shared shuffle engine and phone policy exist; actual sync adapter/host integration and phone controls remain unfinished.
- Historical pre-WIP baseline: Noctis9503 total/0failed/83skipped; Finder1760 correct/0wrong; sync56/0. These do NOT validate your new changes.

## Priority 1: stabilize shared extraction

Build Finder, Mobile and relevant test projects. Fix dependency/host-boundary issues; preserve desktop behavior, identities, license attribution and settings migration. No desktop App globals, reflection or window hooks in shared code. Keep desktop ProgressiveLeaseExtensions working after moving UI-free buffer/transfer code. Run Finder checks and focused extraction/desktop regressions. Update architecture/source-path checks for actual moved files without weakening behavior checks.

## Priority 2: complete phone sign-in and Finder

Use existing mobile controls, GroupedStyles, brushes/fonts, navigation and subtle motion. Minimal English labels, at most one short explanation. No copied desktop Finder window or extra navigation clutter.

- Construct ONE host-owned FinderSession in AndroidApp.axaml.cs, with its OWN Telegram session under NoBackupFilesDir and private music/cache paths. Never copy a PC/TEST session; share that host's connection with future sync.
- Wire TelegramPaneViewModel/PhoneTelegramSignIn into Shell and Settings (row, pane enum, visibility/title notifications and view). Phone -> code -> optional password -> signed in, cancel/retry, proxy, unsupported signup/email, logout and missing-deployment setup all need usable states. Unknown code length requires Continue. Cancellation must abort pending work and reject late results; reset must retain an authenticated session. No sensitive logging.
- Add an explicit ignored build-time AndroidAsset/property for existing packed app deployment defaults; load privately. Cloud has NO actual credentials. A build without defaults must compile and explain missing setup. Never fabricate credentials, commit API secrets or require paid/new user API keys. Preserve a user's existing API pair over defaults.
- Add asynchronous online results/play/download to existing Search, keeping local results immediate, debounced latest-query-wins online work, cancellation, recording-aware deduplication and cached artwork. Repeated taps must not spam bots. Native download controls need progress/pause/resume/retry/remove/clear and honest errors.
- Saved files join the actual library and survive restart/rescan. Add private regular-file support where AndroidFileSystemSource/SafTrackFileAccess currently only handle SAF URIs. Preserve external folder grants. Implement phone IPlaylistLibrary with serialized persistence and expected-track safety.
- Until a genuine Media3 progressive DataSource is ready, wait for full transfer completion before playing an ordinary local file. Do not call this streaming parity. A growing path alone is not a progressive implementation.

## Priority 3: sync correctness, real adapter and host wiring

Fix remaining blockers BEFORE enabling uploads:

- M2: receiver-relative future timestamp clamping can diverge when devices read the same edit at different times. Choose deterministic ordering/time validation and prove convergence with adversarial tests. Preserve ties, concurrent edits and snapshot/replay semantics. The speculative server-clock idea in the handoff is NOT an instruction to implement it blindly.
- M7: fresh install gets a fresh durable device identity; initial snapshot/pull precedes per-device counter writes. Reinstall/restart must not replace accumulated plays with count1.

Implement a real WTelegram ITelegramDrive adapter using the SAME host account connection, plus opt-in desktop/phone controller/settings. Check the PINNED WTelegram API rather than guessing signatures. Use injectable protocol seams for tests without cloud credentials.

Cover ownership/account markers including rebuilt channels, durable queued jobs and stable request id across ambiguous acceptance/crash, paced single-file work, FLOOD_WAIT, expired references, bounded memory, cancel/retry, aligned partial download resume, collision/Unicode-safe names, storage errors and offline recovery. Explain limitations: per-instance dedup is not crash-proof exactly-once upload; resumable download is not resumable multipart upload.

Wire library membership/state/playlists/counters and sidecars without upload loops or duplicate LAN/Telegram application. Bootstrap from snapshots, prune only covered log history, preserve tombstones/user-selected deletion policy and existing LAN protocol. Sync OFF until explicit opt-in; no silent whole-library upload. Wi-Fi-only automatic phone transfers by default, optional charging policy. Offline local playback remains usable. Storage/SAF problems pause affected work with a concise action.

If budget prevents a whole safe vertical slice, finish the highest priority coherent part and document remaining connections. Do not expose an enabled setting that cannot work.

## Priority 4: phone shuffle and lyrics

Connect the existing shared/phone True Shuffle engine to native mode controls, selection indicators and persistence. Cover restored startup, latest click wins, manually queued songs ahead of generated shuffle, offline behavior and valid empty-mode results. No second shuffle engine.

Connect supported online word-timed lyrics and synced PC sidecars to mobile lyrics. Preserve Plus motion/rendering stability. NO local WordLyrics model/Whisper download, ffmpeg or Reels port on phone. Add meaningful state/geometry regressions; exact visual parity remains a local check.

## Priority 5: progressive playback and build readiness

After higher priorities, implement a real Media3 DataSource over shared transfer/buffer behavior: blocking reads until bytes arrive, temporary underflow versus true EOF/error, cancellation/unblocking, buffering/recovery, stable pause/resume position and seeking only where supported. Test via a portable seam and compile Android code. Device/network behavior remains unverified until local testing. Playing cache-only songs must not add them to Songs automatically.

Check Plus CI/build variant `com.alfamin.noctisplus` while preserving stock builds. Android Debug ARM64 compilation is valuable if toolchain is available. No official release signing/key creation; owner keystore/signing stays local. Never present a temporary/debug-signed APK as the official update.

## Tests and cloud boundaries

Use .NET10 and repository pins. Core/Finder/Mobile/TelegramSync are net10.0. Noctis.Tests/Finder.Tests select net10.0 on Linux. Start by building touched projects directly; read runner options instead of guessing. Initial commands:

```
dotnet build src/Noctis.Finder/Noctis.Finder.csproj
dotnet build src/Noctis.Mobile/Noctis.Mobile.csproj
dotnet run --project tests/Noctis.TelegramSync.Tests/Noctis.TelegramSync.Tests.csproj
dotnet run --project tests/FreeMusicFinder.Tests/FreeMusicFinder.Tests.csproj
```

Run focused Noctis.Tests for changed behavior, then one broad portable pass once stable if environment/budget permits. Use --no-restore after successful restore. Install justified Linux test prerequisites once; classify unsupported native/OS checks separately, do not silently ignore failures. PC FastGates scripts contain hard-coded Windows paths and cannot validate cloud code.

For Android compile, follow `.github/workflows/dotnet.yml` Android job; bounded SDK/JDK/workload setup is fine. Do not burn the budget downloading an emulator or fighting unavailable toolchains. No real Telegram calls, bot stress tests, credentials or music downloads in cloud. Fake tests must use synthetic files.

Important regressions: edits during flush; crash after server acceptance; same seq/different payload; snapshots/gaps; different reader clocks; reinstall counters; network loss mid-transfer; expired reference; long Persian/Unicode title; name collision; disk full; revoked SAF grant; unknown code length/cancel; duplicate taps; stale search reply; local/online duplicate; playback priority; shuffle after restore.

## Spend efficiently

Default ONE worker, no agent swarm. Read handoffs once; use rg and bounded relevant reads; reuse fixtures/tests; batch independent checks; summarize logs. No repeated whole-repo audits, speculative rewrites, dependency upgrades or filler documentation. Spend reasoning on protocol/concurrency correctness and clean integration.

Use actual platform spending telemetry/cap if exposed. Do not infer dollars/usage percentage from elapsed time or context length. Leave a reserve to save changes and deliver the handoff. Commit coherent slices with exact named files; never git add -A. Continue until cloud-suitable work is complete, actual budget is near exhaustion, or a concrete blocker leaves no useful independent task. Preserve quality; do not promise everything will fit US$20.

## Required delivery

1. Implementation commits on your dedicated task branch; push that branch only. Record original task base/final hashes. No main/native merge, release or tag. If push is unavailable, export a binary/full-index follow-up patch including all new tracked files.
2. `docs/fork/CLOUD-260-STATUS.md`: implemented/automated-tested/not-live-checked/open/blocked, exact results, important design decisions, migrations and reliability/security limits.
3. `docs/fork/HANDOFF-2026-11-05-CLOUD-260.md`: commits/files, integration instructions and precise remaining Windows/Android/real-account checks in priority order. Record that local profiles/settings/API/login must be preserved.
4. Short owner summary. Commands for the OWNER are PowerShell; cloud-internal commands may use the actual shell.

Local Codex will integrate safely and run full Windows ten-shard gates + Finder checks, TEST-app visual checks, isolated Android emulator interactions, own-device Telegram login, real TEST bot/file/channel/forward/reference checks, two-device sync and interrupted-network playback, then phone build/install/signing only when authorized. Cloud cannot establish these results from simulations. Never request a copied session or invent live evidence.
