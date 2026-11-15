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
- No sync network operation, Android build, or emulator test was run during this audit. Android SDK download remains blocked at `dl.google.com`.

## Implementation order

1. Map existing sync kinds and host state to Android library/playlist/play-history APIs; keep local state read-only until an explicit opt-in controller exists.
2. Add a phone host/controller that reuses the existing signed-in `TelegramAccount`, enforces the shared account/channel guard, and persists device identity, cursor, and jobs in no-backup storage.
3. Wire changes idempotently: library/playlist state, play counters, and applicable sidecars; guard against LAN/Telegram double application and upload loops. Bootstrap from snapshots and preserve tombstones/deletion policy.
4. Add native settings only once the path is functional: explicit master opt-in, Wi-Fi-only automatic transfers by default, optional charging policy, concise status/recovery actions. Keep off by default.
5. Add fake-drive tests for opt-in gating, restart durability, account mismatch, bootstrap, duplicate apply, offline/retry, revoked session, and storage failure. Then run repository gates and x64/ARM64 builds. Real Telegram and two-device checks remain an owner-side step.

## Status

**Planning/audit only.** No source implementation or live account test is claimed. This work remains incomplete until automated tests, build evidence, and owner-side live verification are recorded separately.

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
