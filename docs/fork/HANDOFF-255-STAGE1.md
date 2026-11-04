# Handoff: #255 stage 1 (Telegram sync core), 2026-11-04 22:16

Branch `android-plus-stage1` (from `android-plus`), pushed. Built on the owner's Windows PC with the local
.NET 10 SDK in one ~15-minute window, so this is a solid first slice, not the whole task list.
Nothing here touches real Telegram; every call goes through `ITelegramDrive` and the tests use a fake.

## Built

`src/Noctis.TelegramSync` (net10.0, nullable, references only `Noctis.Core`; nothing references it yet):
- `ITelegramDrive.cs`: self user id, find-or-create channel by description marker
  (`noctis-plus:<library|sync>:v1:<guid>`, `TgMarkers`), post document + caption (stream), read history after
  an id (one call per 100), get message, get/pin, delete, download with offset/progress/cancellation, forward.
  Typed errors: `TelegramFloodWaitException` (RetryAfter, Premium flag) and `TelegramFileReferenceExpiredException`.
- `SyncBatch.cs`: wire format `{ v:1, device, seq, items:[{kind,id,payload,updatedUtc}] }` (+ `snapshot`, `upTo`),
  `BatchCodec` (JSON via `SyncJson.Options`, gzip above 3 KB, detected by magic; foreign/broken posts decode to null).
  Kinds: `track`, `playlist` (from `SyncKinds`), `song` (`SongSyncState`, id `tg:<documentId>`), plus two new ones (below).
- `TgLedger.cs`: in-memory merged state, the exact `SyncStore.Upsert` rule (LWW on stamp, tie by ordinal device id
  when payload differs, a stored stamp past now + `SyncStore.MaxClockSkew` loses) and the `ClampStamp` rule
  on incoming stamps (`LibrarySyncService.OldestStamp`, `SyncStore.MaxClockSkew`). `Fingerprint()` for convergence checks.
- `TgSyncLog.cs`: writer (`Record`/`RecordJson` -> `FlushAsync`, one post per `MinPostInterval` = 5 s, FLOOD_WAIT
  keeps the batch and blocks posting until retry-after), reader (`PullAsync` from the cursor, 100 per history call,
  idempotent by (device, seq), refreshes an expired reference by re-reading the message), persisted
  `TgSyncCursor` (last message id, per-device contiguous seq + seqs above a gap; atomic tmp+rename save),
  snapshot (`PostSnapshotAsync` posts + pins the whole ledger keeping each item's original writer, plus per-device
  seqs; `LoadSnapshotAsync` applies it and raises the cursor).
- `TgLibraryView.cs`: visible songs (tombstones out; same recording under different document ids shown once,
  others kept as `AlternateIds`; key = artist + title NFC, lower-case, brackets dropped, duration within 2 s),
  play totals, playlist membership.
- `TgTransferQueue.cs`: one download at a time into `<target>.part` (resumes from its length after cancel/drop),
  size check, atomic rename, policy predicate (Wi-Fi/charging) -> Paused, FLOOD_WAIT -> Waiting until retry-after
  (no calls meanwhile), expired reference -> re-read once, deleted message -> Missing (job dropped, no file touched).

`tests/Noctis.TelegramSync.Tests` (net10.0, xunit.v3 3.2.2, runner 3.1.5, Test.Sdk 18.10.1 = same as Noctis.Tests):
`FakeTelegramDrive` (in-memory account; injects flood waits on posts and downloads, duplicated history reads,
deleted messages, expired references, mid-transfer cancel via `OnChunk`), `SyncLogTests` (19), `TransferQueueTests` (4).
Both projects added to `Noctis.sln` (only additions in the sln diff). No new NuGet packages.

## Test results (exact)

`dotnet test tests/Noctis.TelegramSync.Tests`: **23 total, 23 passed, 0 failed**, 2.2 s.
Budget test (printed): decode + merge of 10,000 songs + 10,000 track states = **55-70 ms** (budget 200 ms;
asserted < 1 s), snapshot build + post = **52-60 ms** (budget 500 ms; asserted < 2.5 s), snapshot size **254 KB gzip**
(budget 2 MB). Reading 250 posts = 3 history calls. Measured on the owner's PC, three runs.
Not run: `tests/Noctis.Tests` (told not to: too slow) and `FreeMusicFinder.Tests`; no existing project was changed.

## Design changes (and why)

1. **Play counts**: new kind `plays`, id `<songId>@<device>`, payload `{count, lastPlayed}`. Only the owning device
   writes its own counter, so plain LWW never loses plays; total = sum (`TgLibraryView.PlayCount`). The `track`
   payload's `playCount` should not be used for totals on this log (stage 2 maps the desktop's number to the
   device's own counter).
2. **Playlists**: header `TgPlaylistHeader {name, color, order, deleted}` under kind `playlist`, membership as
   kind `playlistEntry`, id `<playlistGuidN>|<songId>`, payload `{present}`. Adds/removes merge per song;
   order = last writer's header order, then entries the header does not know by add time. This is NOT the
   desktop's `PlaylistSyncState` payload (that one replaces the whole list); stage 2 must convert.
3. **ClampStamp** is `internal` in Noctis.Core and Core's InternalsVisibleTo does not include this project. To avoid
   touching Core I repeated the two-line rule on Core's public constants. Option for later: add
   `<InternalsVisibleTo Include="Noctis.TelegramSync" />` to Noctis.Core and call it directly.
4. **Recording key**: `RecordingMatch.TitleKey` lives in the desktop app project (Finder), not UI-free Core, so
   `TgLibraryView.RecordingKey` is a simple stand-in. Stage 3 (Finder core extraction) should switch to the real one.
5. **Ledger is in memory** (fast merge); persisting it / writing through to `SyncStore` is stage 2 wiring.
6. **Snapshot items** carry the original writer as `<id>\u001f<device>` and per-device seqs as kind `_seq`,
   so a device that loads a snapshot breaks ties exactly as one that read every post.

## Left from the task list (CLOUD-PROMPT-255-STAGE1.md)

- Paced **uploader** (Library channel upload, one at a time, resumable) and **forward** pacing (1 per 2 s): only the
  interface exists; download queue is done.
- Local map **documentId <-> local track id** (persisted) with the recording-key fallback to an existing local file
  ("same file under another path: link it, don't download again").
- Account check: refuse to sync when `GetSelfUserIdAsync` differs from the id stored at setup (interface ready, no guard class yet).
- Channel deleted/left -> recreate from local state; session revoked -> stop sync (needs a typed error in the interface).
- Snapshot cadence (every N posts / weekly) + deleting old posts; `PostSnapshotAsync` should pull first so `upTo` is exact
  (today it uses the cursor's last read id; dedupe by seq keeps it correct, just reads a little more).
- Hash check of downloads when known; storage-full / SAF-lost pause; file name sanitising (other-OS invalid names,
  NFC, long names); LAN + Telegram double-apply guard; per-transfer progress reporting to the queue's caller.
- More tests asked for: playlist delete vs concurrent add on the other device, three-device play counts, deleted song
  message in the sync log itself (a `song` whose home message is gone -> "missing online").
- Then stages 2-5 per ANDROID-PLUS-PLAN.md.

## Commits

4b37e539 core, 7dbe1b84 fake + sync-log tests + sln, 04e2c32c transfer queue, then this handoff commit
(download flood-wait test + this file).
