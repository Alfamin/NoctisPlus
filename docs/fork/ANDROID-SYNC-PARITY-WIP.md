# Android cross-device sync parity — work plan

## Goal

Port the useful Noctis Plus personal-library sync to Android using the existing shared `Noctis.TelegramSync` protocol. This is separate from the existing LAN/account sync route; it must not replace or silently change that path.

## Safety boundaries

- Sync stays **off by default** and cannot run until the user explicitly enables it in the phone UI.
- Use only the phone's own Telegram sign-in through its existing Finder account. Never copy a desktop session, reuse a packed app API pair as the user's credentials, or expose secrets in settings/logs.
- No real Telegram/channel calls in automated checks. Use the existing fake drive. Do not run against the owner's library or TEST channel without a separate explicit live-test authorization.
- Keep data under the app's private, no-backup storage. Preserve local playback, existing LAN behavior, and existing sync data formats.
- Do not expose a setting that claims to sync before the complete opted-in route is wired.

## Initial audit (2026-11-15)

- Base is the released Android/Plus source at `1445a83d` in a new isolated worktree on `codex-android-sync-parity`; this branch is not merged into main/native or the release worktree.
- `Noctis.TelegramSync` already contains the ledger, conflict rules, snapshots, device identity, durable batch/transfer queues, and fake-drive tests.
- `Noctis.Finder/Sync/WTelegramDrive.cs` already wraps Telegram operations around a host-provided connection callback. `FinderSession` owns the phone's Telegram account, so an adapter can use that same client without a copied or second session.
- The missing vertical connection is the host/controller that maps the phone library, playlists, sidecars, play history, and preferences to the shared log, plus the native opt-in/settings/status UI. The older `CLOUD-260-STATUS.md` also marks this integration open.
- At the initial audit timestamp, no sync network operation, Android build, or emulator test was run; `dl.google.com` was unreachable then. Host follow-up on 2026-11-16 confirmed an installed Android SDK (`android-36`) and ADB 37.0.1; no device was attached. This does not establish that the app builds, and no Android package build or emulator run has been performed for this sync slice.

## Implementation order

1. Map existing sync kinds and host state to Android library/playlist/play-history APIs; keep local state read-only until an explicit opt-in controller exists.
2. Add a phone host/controller that reuses the existing signed-in `TelegramAccount`, enforces the shared account/channel guard, and persists device identity, cursor, and jobs in no-backup storage.
3. Wire changes idempotently: library/playlist state, play counters, and applicable sidecars; guard against LAN/Telegram double application and upload loops. Bootstrap from snapshots and preserve tombstones/deletion policy.
4. Add native settings only once the path is functional: explicit master opt-in, Wi-Fi-only automatic transfers by default, optional charging policy, concise status/recovery actions. Keep off by default.
5. Add fake-drive tests for opt-in gating, restart durability, account mismatch, bootstrap, duplicate apply, offline/retry, revoked session, and storage failure. Then run repository gates and x64/ARM64 builds. Real Telegram and two-device checks remain an owner-side step.

## Status

**In progress; not end-to-end.** The shared-drive adapter seam, default-off preferences store, and pure transfer-policy predicate are implemented and have focused headless coverage. No controller, UI, scheduler, or live account test is claimed. Automated integration tests, Android build evidence, and owner-side live verification are still required before this work can be called complete.

## Sync model audit — 2026-11-15

The release already has a separate LAN path: `Noctis.Core.Services.Sync.LibrarySyncService` carries favorites, ratings, play counts, and playlists through the PC's Noctis server, while Android's account client talks to that server. The Telegram protocol in `Noctis.TelegramSync` adds cloud media/library state and its own ledger. These must not echo each other's updates or overwrite the established LAN route. `IPlayHistoryService` also preserves the original play timestamp, which matters when adding offline phone plays. Before implementing the controller, map these existing application and protocol models together and retain idempotent apply/echo guards.

## Scope correction — Android LAN sync already has both directions

`NoctisAccountService.SyncNowAsync` pulls pages with `NoctisServerClient.GetSyncChangesAsync` and pushes local changes with `PushSyncChangesAsync`; that PC-hosted LAN/account path is already bidirectional in the release. The remaining gap tracked here is a separate Telegram-backed personal cloud transport, not a replacement for LAN sync and not evidence that the existing LAN route is one-way. Any bridge must preserve the current account client and avoid applying the same local change twice.

## Checkpoint — 2026-11-15 09:06 UTC

- Dedicated branch `codex-android-sync-parity` is clean and pushed through `a0366407`; the only changes are this planning/audit document. No sync source code, live Telegram operation, release integration, or profile change has occurred.
- Direct `get_usage_limits` at 09:06 UTC reported 71% used in the primary 30-day window, no secondary limit, and no reset credits. The local watcher status file instead reported 100% / `wrapUp: true` with `checkedUtc` 08:14 UTC (52 minutes old). Treat the watcher record as stale; obtain a fresh direct quota read and fresh watcher status before any further implementation. No reset is authorized or available.
- Android SDK/ADB remains absent; the official `dl.google.com` SDK host was unreachable. No build or emulator is active.
- Next: resolve quota-watcher freshness, then continue the bounded Android/Telegram sync host audit. Keep the feature opt-in-only and distinct from the already bidirectional PC-hosted LAN sync.

## Reused-account adapter seam — 2026-11-15

`FinderSession` now creates one `WTelegramDrive` over its already-owned `TelegramAccount`; `PhoneFinderHost.SyncDrive` exposes that stable adapter to a future controller. Constructing or reading the property makes no network call and creates no second Telegram session. Focused `PhoneSyncDriveTests`: 1 passed, 0 failed. The test assets were restored into this isolated worktree; only source compilation plus this one filtered test was run. This is an adapter seam, not a wired sync controller or enabled feature.

## Default-off phone policy store — 2026-11-15

Added an Android-only preferences file for future Telegram sync controls, separate from the LAN `SyncEnabled` account setting. Missing settings default to sync off, Wi-Fi-only transfers, and charging not required; malformed JSON reports corruption and returns the safe disabled defaults. Writes use a flushed temporary file followed by replacement. Focused `PhoneTelegramSyncPreferencesStoreTests`: 3 passed, 0 failed. No UI, scheduler, Telegram calls, or sync controller is wired yet; do not present this store as end-to-end sync.

## Transfer-policy predicate — 2026-11-15

`PhoneTelegramSyncPreferences.AllowsTransfer(onWifi, charging)` now centralizes the start rule: transfer requires explicit enablement and honors saved Wi-Fi-only / charging-only choices. The focused `PhoneTelegramSyncPreferencesStoreTests` passed 4/4 on the Windows test target (exit 0; existing analyzer warnings only). The existing default-off persistence and corruption behavior remain covered. This remains policy groundwork only: no UI, controller, scheduler, Telegram network call, Android package build, emulator validation, or live account check was performed. Do not describe it as end-to-end sync. Dedicated branch remains `codex-android-sync-parity`; next work requires mapping the controller seam and fake-drive coverage before any user-facing setting.

## Phone host/domain mapping audit — 2026-11-16

- `PhoneFinderHost.SyncDrive` forwards to `FinderSession.SyncDrive`; `FinderSession` creates one `WTelegramDrive` over its existing `TelegramAccount`. The constructor seam itself does not sign in or make a network call. Keep using this account owner; do not add another Telegram session.
- The Telegram log's domain split is explicit: `song` is library membership keyed by Telegram document id; `track` is user state (favorite/rating/dislike/snooze); `playlist` is header/tombstone; `playlistEntry` is per-song membership; `plays` is one cumulative counter per song and device. `TgLibraryView` sums the counters and resolves playlist membership/order. These are not interchangeable with the PC-hosted LAN `track`/`playlist` items.
- `TgTrackMap` is the boundary between Telegram document ids and phone track ids. Its existing-file match uses file size plus normalized recording key; do not assume title/artist alone uniquely identifies a recording or download into `D:\music`.
- The phone's `ILibraryService` owns the track catalog and `LibraryViewModel` owns playlist mutation. Playlist edits are already serialized through the phone playlist writer/UI dispatcher; a background sync worker must marshal mutations rather than editing observable models directly. `IPlayHistoryService.Events` stores timestamped play events, while the Telegram `plays` kind stores per-device totals only. A controller must define how counters and timestamped history interact before it imports either, or it can double-count or fabricate history.
- The existing phone LAN route remains `NoctisAccountService.SyncNowAsync`; this audit found no Telegram controller consuming `SyncDrive` or `PhoneTelegramSyncPreferences`. Keep those routes distinct and preserve LAN behavior.

No code, live Telegram request, library/profile data, or `D:\music` was touched in this mapping pass. The next implementation decision is the smallest fake-drive-backed controller boundary: durable device/cursor state and account guard first, then map one domain at a time with idempotency tests. Do not add a visible setting until the opted-in route is complete.

## Quota wrap-up checkpoint — 2026-11-16 02:20 Tehran

- Direct `get_usage_limits`: 90% used in the primary 30-day window, no secondary window, and no reset credits. The quota watcher reports `wrapUp: true`; stop implementation/build/research until a later safe ordinary window. The one reset authorized for #268 remains fulfilled; do not try another.
- Dedicated branch `codex-android-sync-parity` was clean at `5a76e062` before this documentation-only wrap-up. Its current saved scope is the existing-account drive adapter, default-off policy store, Wi-Fi/charging transfer predicate with focused test evidence (4/0), and the host/domain mapping. It is not end-to-end sync: no controller, UI, scheduler, Telegram request, Android build, APK, emulator, or live account test.
- `codex-android-262` remains clean at `8f243e9a`; no active test gate, Android build, or emulator was found. Android SDK/ADB binaries are present, but no device is attached; do not infer a successful build.
- No profile or login was changed, no Telegram session was copied, and `D:\music` was not accessed. Keep this work on its dedicated branch; no main/native merge, tag, or release.
- Resume only after an actual fresh quota read shows headroom. Next planned work is fake-drive controller/state integration, beginning with durable cursor/device identity and account-mismatch gating; continue to avoid user-facing controls until the opted-in path is complete and validated.

## Wrap-up continuation — 2026-11-16 03:15 Tehran

Direct usage is now 91% in the primary window, no secondary window, and zero reset credits. Watcher status refreshed at 23:45:57 UTC and agrees at 91% / `wrapUp: true`. The last saved code checkpoint is `d199309f`; the documentation-only wrap-up is `c2227ae4`. Dedicated branch is clean before this note. No Android build, emulator, or active test gate is running. This remains documentation-only until the service reports a later safe ordinary window; do not redeem the already-consumed #268 reset.

## Wrap-up check — 2026-11-16 09:20 Tehran

Direct usage is 92% in the primary window; no secondary limit or reset credits. Watcher record was stale at 91%, so the direct read governs. No implementation/build/research; branch was clean at 4c02ea72. #268 remains fulfilled. Continue wrap-up only.

