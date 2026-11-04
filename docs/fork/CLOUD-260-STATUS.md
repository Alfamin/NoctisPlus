# Cloud #260 status (Claude cloud, Linux, no device/account)

Base: `cloud/android-plus-260` @ efcca6d0 (contains WIP checkpoint 1d1a6f3b; ancestry verified).
Task branch: `claude/android-plus-260`. Commits: 322cf445, 38917dce, 8ffefe4d, then the docs commit.

Legend: **IMPL** = code written and compiled; **AUTO** = covered by automated tests that passed in cloud;
**NOT-LIVE** = never run on Windows, emulator, phone or real Telegram; **OPEN** = not done; **BLOCKED** = not possible in cloud.
**Nothing here is LIVE-VERIFIED.** Builds and fake/headless tests do not show real-account or device success.

## Task list (assignment order)

| # | Task | State |
|---|---|---|
| P1.1 | Build Noctis.Finder / Noctis.Mobile / Noctis.Tests (FinderSession compile fix) | AUTO (0 errors each) |
| P1.2 | Finder checks, TelegramSync tests | AUTO (see results; 7 Linux-only "wrong") |
| P1.3 | No desktop App globals/window hooks/reflection in shared Finder; source-path guards | Checked: none found; no guard referenced the moved paths, nothing to update |
| P2.1 | One host-owned FinderSession in AndroidApp; own session under NoBackupFilesDir | IMPL, NOT-LIVE (Android not compiled, see P5) |
| P2.2 | Telegram pane wired into Shell + Settings (row, pane enum, title, value) | IMPL + AUTO |
| P2.3 | Ignored build-time AndroidAsset for packed defaults; builds without it explain missing setup | IMPL, NOT-LIVE |
| P2.4 | Online results / play / download in phone Search; downloads list controls | IMPL + AUTO (fakes) |
| P2.5 | Private regular-file support; phone IPlaylistLibrary | IMPL + AUTO |
| P2.6 | Full-transfer wait before playing (no streaming claim) | IMPL (`RequireCompleteFile`), not unit-tested end to end |
| P3.1 | M2 deterministic stamps + adversarial convergence tests | IMPL + AUTO (5/9 new tests fail on the old rule) |
| P3.2 | M7 per-install device id; counters not written before first pull/snapshot | IMPL + AUTO |
| P3.3 | Real WTelegram ITelegramDrive adapter (same host account) | IMPL + AUTO (pure helpers only), NOT-LIVE |
| P3.4 | Durable sync job storage, controllers/settings, host wiring of library/playlists/counters/sidecars | OPEN (no setting exposed; sync remains off) |
| P4 | Phone shuffle UI, mobile online/sidecar word lyrics | OPEN |
| P5 | Media3 progressive DataSource | OPEN |
| P5 | Android Debug compile | BLOCKED: workload installed, but the environment's network policy denies `dl.google.com` (Android SDK) |

## New requirements logged (no external blueprint available)
- Phone sign-in must never save the build's packed API pair as the user's own (desktop owner #185 rule). Found violated in the WIP `PhoneTelegramSignIn`; fixed.
- Cancelling a sign-in step returns to the number step and resets the pending attempt. Before this, the account had dropped the client but the UI stayed on the code step, so the next attempt failed.

## Exact results (cloud, .NET SDK 10.0.113, Linux x64)
- `dotnet build src/Noctis.Finder`: 0 errors (6 warnings). `dotnet build src/Noctis.Mobile`: 0 errors. `dotnet build tests/Noctis.Tests`: 0 errors.
- `Noctis.TelegramSync.Tests`: **66 total, 0 failed** (56 existing + 9 convergence/M7 + 1 request-id). Fake Telegram only.
- `FreeMusicFinder.Tests`: **1753 ok, 7 wrong**, run on the pre-change WIP (start of session). All 7 depend on Windows: `FileShare.None` lock semantics (queue-save failure, locked removal/clear) and `C:\` path literals (stream-cache folder tests). These tests predate this work and passed 1760/0 on Windows. They are classified as not portable to Linux, not as passing. Not re-run after the later Finder edits (`RequireCompleteFile`, FinderSession param, WTelegramDrive); local Windows Finder checks must re-run.
- Focused `Noctis.Tests` (xUnit, net10.0 Linux): PhoneFinderTests 13/0, WTelegramDriveTests 8/0, MobileTelegramSignInTests + MobileSearchTests + MobileSettingsTests (with PhoneFinderTests) 73 total, failing only on my own test bug, which I then fixed (rerun 13/0).
- Broad portable `Noctis.Tests` pass: see section below.
- Counterfactual: with remote stamps bounded by the reader's clock again (the old M2 behaviour), 5 of 9 convergence tests fail.

## Design decisions
- **M2.** A remote item's stamp is `clamp(raw, postDate + MaxClockSkew)`, where `postDate` is the Telegram server date of the message that carried it. Every reader sees the same date, so every reader stores the same stamp. The reader-relative "stale" rule is removed.
  - The writer stores exactly what it posts. When it reads its own post back, it adopts the bounded stamp, matched on exact raw stamp. Only the first copy in message order matches, the same copy readers apply, so an ambiguous retry copy is a no-op.
  - Own successive edits are forced monotonic. Only the newest *unposted* edit of an item is sent, so two edits cannot collapse onto one bounded stamp.
  - The speculative "server-clock anchor at the writer" idea was NOT implemented.
  - Remaining edge: devices with clocks fast by more than 2 min that edit one item in the same server second tie-break by device id. A writer that read the other device's post before its own may then differ from the readers. Very unlikely; documented.
  - Between a post and the writer's next pull, the writer's own copy can briefly differ from the readers' (fast clock only).
- **M7.** `TgDeviceIdentity.LoadOrCreate(path, kind)` makes a per-install id. The host must keep it in its private, not-backed-up folder, so a reinstall gets a new id.
  - `TgSyncLog.RecordPlay` buffers plays as durable increments until the first pull or snapshot. It then writes `own count + n`, never `count = 1` over an existing count.
  - Checkpoint gains `Bootstrapped`/`EarlyPlays` (backward-compatible). An old checkpoint that had read the log counts as bootstrapped.
- **Stable request ids.** `ITelegramDrive.PostDocumentAsync(..., long requestId, ...)` (default method keeps the old fakes working). Sync batches use `TgRequestIds.For("batch:"+channel, device#seq)`; uploads use the job identity. This is NOT crash-proof exactly-once: Telegram remembers random ids only for a while. Interrupted uploads restart from byte 0; only downloads resume.
- **Phone playback of online songs.** It waits for the complete file (`FinderOnlineSearch.RequireCompleteFile`); a growing path is never handed to Media3. Played-only songs stay in the stream cache, not in Songs. Explicit downloads go to `NoBackupFilesDir/finder-music` and are imported as individually added files, which rescans keep.
- **Phone playlist matcher limitation.** It sees only plain-path songs (Finder downloads). SAF-folder songs have content:// paths the shared matcher cannot check, so a playlist import may re-download a song a selected folder already has.
- **Packed defaults.** `-p:NoctisTelegramApp=<path>`, or `src/Noctis.Android/private/telegram-app.dat` (git-ignored), is packed as an AndroidAsset. At start it is copied privately to `NoBackupFilesDir/finder/telegram-app.dat`; a build without it deletes an older copy. A user's own API pair in the session store still wins. No credentials exist in this branch.

## Reliability / security limits
- No sensitive logging added. Finder log lines go through DebugLog, which the Android host already redacts before logcat.
- The adapter's history window, pin, forward, `RANDOM_ID_DUPLICATE` recovery and DC migration follow the pinned API docs but are unverified against Telegram.
- No sync setting is exposed. Nothing uploads.
