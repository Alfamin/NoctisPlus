# Review #255 stage 1 - Noctis.TelegramSync

Read-only review of `src/Noctis.TelegramSync` against `docs/fork/ANDROID-PLUS-PLAN.md`. No source edits.
Ranked by severity. Line numbers are for branch `android-plus-stage1` at efcd8803.

## HIGH

### H1. Items recorded during an in-flight flush are dropped - TgSyncLog.cs:108-121
`FlushAsync` copies `_pending.ToList()` (108), awaits the post (112), then calls `_pending.Clear()` (121).
Any `Record` made while the post is awaiting (UI thread, play counter tick) is cleared without being posted.
It is still in the local ledger, so this device looks fine but no other device ever gets it.
Fix: `_pending.RemoveRange(0, batch.Items.Count)` instead of `Clear()`, plus a single-flight guard (see H4).

### H2. Seq reuse after a crash, reinstall or ambiguous post failure - TgSyncLog.cs:108, 119-123
`OwnSeq` is only bumped and saved after the post returns.
- Crash between the post landing and `Save()` (123): the restart posts different items under the same seq.
  Other devices see `Seen(device, seq)` (141) and skip it, so those changes are lost.
- A timeout where the server did accept the post: `_pending` is kept, more `Record`s arrive, and the next flush
  posts a superset under the same seq. Devices that read the first copy drop the new items.
- Reinstall or cursor wipe with the same device id: `OwnSeq` restarts at 0, and every new post is below the other
  devices' `Contiguous[device]`, so it is silently ignored.

Fix: reserve the seq before posting. Set and save `OwnSeq = seq` first; on failure post the next seq and never reuse.
At startup, after the snapshot and pull, set `OwnSeq = max(OwnSeq, Contiguous[Device], max(Above[Device]))`.
Better still, give every install a new device id.

### H3. Cursor moves past a batch whose download failed - TgSyncLog.cs:138-140, 200-212
`LastMessageId` advances (138) before `ReadBatchAsync` (140). A network error, a flood wait, a second expired
reference, or a cancel throws out of `PullAsync`. The in-memory cursor is already past that message, so the next
`PullAsync` on the same instance never re-reads it, and the next successful `Save()` writes the skip to disk.
The seq then waits in `Above` forever, because nothing ever goes back for it.
Fix: advance `LastMessageId` only after the batch is applied, or skipped as foreign or seen. On any exception,
roll it back to the last fully handled id. Handle `TelegramFloodWaitException` inside the pull by stopping cleanly
with a retry-after, the same way `FlushAsync` does.

### H4. No thread safety on shared state - TgSyncLog.cs (all), TgLedger.cs:18, TgTransferQueue.cs:16
`_pending`, `Cursor.Contiguous` / `Above` (plain `Dictionary` / `SortedSet`), the ledger `_items`, and the
`LinkedList` queue are mutated with no lock. A timer `FlushAsync` running beside a `PullAsync` (both call `Mark`
and `Save`), or a `Record` from the UI thread, can corrupt the dictionaries or throw
"collection was modified" inside `Save()` serialization.
Fix: one `SemaphoreSlim` per `TgSyncLog` around Record / Flush / Pull / Snapshot (or a documented single owner
thread, enforced by a check). The same applies to `RunNextAsync`, which a second caller can re-enter on the same
head job and `.part` (`FileShare.None` throws an IOException).

### H5. An existing target file blocks the download queue forever - TgTransferQueue.cs:56
`File.Move(part, target, overwrite:false)` throws an IOException when the target exists (already downloaded, or a
duplicate recording queued twice). The exception is not caught, the job stays at the head, and every later call
fails the same way, so no other song downloads.
Fix: if the target exists with the expected size, delete the `.part` and return Done. Otherwise move to a
unique name. Catch IOException and return a failed result that dequeues the job or moves it to the back.

## MEDIUM

### M1. A same-device, same-stamp edit is lost - TgLedger.cs:50-52
On a tie the incoming item wins only when `CompareOrdinal(device, stored.Device) > 0`. Two `Record`s from one
device with the same stamp (a coarse clock, an injected clock, or a fast double toggle of a playlist entry)
compare equal, so the second is ignored locally, yet it is still posted. Every device keeps the first value.
Fix: on a tie from the same device the later apply wins. Or make `Record` stamps strictly increase per device
(`max(now, lastStamp + 1 tick)`).

### M2. Receiver-side clamping breaks convergence under clock skew - TgLedger.cs:44, 49-50
Each receiver clamps an incoming future stamp to its own `now + MaxClockSkew`, then stores the clamped value.
Devices that apply the same item at different times store different stamps, so `Fingerprint()` differs, and
later LWW comparisons against that item can come out differently on different devices. The `stale` check (49)
can then only fire when the local clock goes backwards.
Fix: store the writer's raw stamp and clamp only for the comparison. Or clamp once at the writer (in `Record`) and
reject far-future items at the receiver.

### M3. No size limits on downloads or decompression - TgSyncLog.cs:200-211, SyncBatch.cs:81-86
`ReadBatchAsync` reads the whole document into memory whatever its `Document.Size`, and `Decode` decompresses
gzip with no output cap. A large or corrupt post under the batch caption (any member of the channel, or another
client bug) can use up memory on the phone.
Fix: skip documents over about 8 MB (the plan's 10,000-song snapshot budget is under 2 MB) and stop decompression
at about 32 MB, returning null.

### M4. Malformed items crash the pull - SyncBatch.cs:90, TgSyncLog.cs:142, TgLibraryView.cs:20,47,58
`Decode` accepts `items: [null]` and items with `payload: null`. `Ledger.Apply(null)` throws a
NullReferenceException. A null payload is stored and later breaks `SyncJson.Deserialize` in the library view.
The plan says foreign or broken posts should be "skipped, never thrown on".
Fix: in `Decode`, drop null items and items with null or empty kind, id or payload. Return null if `Device`
contains the snapshot separator U+001F.

### M5. Resume offset may not be valid for Telegram - TgTransferQueue.cs:41-42
The resume starts at `file.Length`, which can be any byte count after a drop. `upload.getFile` needs an offset
divisible by 4 KB (and parts of 1 MB for fast downloads). The fake accepts any offset, so the tests do not show
this.
Fix: before resuming, truncate the `.part` down to a multiple of 1 MB (or 4 KB), or do the alignment inside the
real `ITelegramDrive`, and add a test for it.

### M6. A size mismatch re-downloads forever - TgTransferQueue.cs:51-54
A wrong size deletes the `.part` and returns Paused, and the job stays at the head. If `Document.Size` is wrong,
or the file is changed on the server, every run downloads the whole song again: wasted data on mobile, and the
rest of the queue is blocked.
Fix: count failures per job. After 2 failures, move it to the back or mark it failed. Also compare against the
refreshed `msg.Document.Size` instead of `job.Message`.

### M7. A play counter can go backwards after a reinstall with the same device id - TgLibraryView.cs:43-47
Only the owning device writes `<song>@<device>`, so the newest write wins. A fresh install with the same id that
plays a song before the first pull writes `count=1` with a newer stamp, which overwrites its older count of, say,
40 on every device.
Fix: new device id per install (as in H2). Or have the writer always write `max(ledger count) + 1`, and block
`Record(Plays)` until the first pull or snapshot load is done.

### M8. The cursor is saved before the ledger is persisted - TgSyncLog.cs:148, 166, 123
`PullAsync` and `LoadSnapshotAsync` save the cursor, but the ledger lives only in memory (the caller persists it).
A crash after `Save()` and before the caller writes the ledger leaves a cursor that says "applied", so on restart
those batches are skipped and their state is lost.
Fix: persist the ledger and cursor together (one file, or ledger first then cursor). Or let the caller own both
saves, and document the order.

## LOW

- L1. TgSyncLog.cs:146: a full last page (exactly 100 posts) costs one extra empty history call. That is within
  the budget, but the metric overcounts.
- L2. TgSyncLog.cs:177: the snapshot `_seq` items use `_now()` as the stamp, so two snapshots of the same state
  have different bytes. This is harmless, but it does not help dedupe.
- L3. Nothing detects "my cursor is older than the oldest post still in the channel" (the plan: offline for months,
  old posts deleted). Without that check the device never calls `LoadSnapshotAsync` and stays partial. Suggest
  this: if the first page's lowest id is greater than `LastMessageId + 1` and a pinned snapshot covers it, load
  the snapshot first.
- L4. TgSyncLog.cs:192: a device id that contains U+001F splits wrongly in snapshots. Validate device ids.
- L5. TgSyncCursor.Save (TgSyncLog.cs:42-47): there is no flush to disk before the rename. On Android, power loss
  can leave an empty file, and `Load` then silently resets to a new cursor (which makes H2 worse). Fix: write
  with a `FileStream` and `Flush(true)`, and keep a `.bak` copy.

## Checked and fine
- An idempotent re-read of the same (device, seq), and gap folding in `Mark`.
- Snapshot+tail: `Raise` drops `Above` entries at or below the snapshot seq, and `UpToMessageId` moves the cursor.
- The flood wait in `FlushAsync` sets `_nextPostUtc`, and the flood wait in the queue keeps the job.
- The gzip magic check and the threshold at 3 KB.
- Playlist entries: per-song LWW keeps an add on A and a remove on B both, and the header order comes from the
  last writer with new songs appended.
