# Download recovery checkpoint — 2026-10-18

Status: validated and pushed as a3e57f89; not a release and not proof of the friend's exact 1.6.3.3 failure.

## Confirmed recovery changes

- Saved audio is imported directly into the library even with folder watching off or a custom destination outside library roots. An unindexed saved row has an explicit Add to library action.
- The Downloads page distinguishes transfer, checking another provider for FLAC, saving, waiting and completed files. Only saved files reach 100%.
- Connection, metadata and quality negotiation waits are bounded. Unsupported quality keyboards cannot disable both providers; the actual returned format must still respect the user's preference. Both bot settings remain a live verification gate.

## Audio transfer watchdog

All four Telegram audio paths now use one transfer implementation: Hunters selected/cached and DeezLoad selected/cached. It reuses the app's existing account, proxy route and media DC connection. No new session is created.

The pinned WTelegram helper has no cancellation token and parallel tasks retain the output stream. Wrapping that helper in a timeout can leave a late writer running after fallback, removal or shutdown. Instead, four aligned 128 KiB Telegram requests return byte arrays. A single awaited writer writes those arrays at their offsets. Timed-out requests never receive the output stream, and pending wrappers are drained before returning. The media connection is retired after a stall or cancellation without clearing the saved login; a later request reconnects normally.

Three minutes without completed bytes triggers AUDIO_TRANSFER_STALLED. Continuous progress resets the deadline, so there is no arbitrary whole-file timeout on a slow link. A stalled provider receives a short cooldown and the backup is attempted. If neither can supply the recording, the durable request waits with its reason and retry time rather than disappearing. Declared size and exact chunk length are checked before final publication.

Primary contracts:

- [Pinned WTelegram transfer helper](https://github.com/wiz0u/WTelegramClient/blob/4.4.9/src/Client.Helpers.cs)
- [Pinned WTelegram client disposal](https://github.com/wiz0u/WTelegramClient/blob/4.4.9/src/Client.cs)
- [Telegram file transfer alignment](https://core.telegram.org/api/files)

## Validation

Full Noctis suite: 6,543 total / 0 failed / 43 skipped, 453.066 seconds (artifacts/plus/transfer-full.log and XML). Finder: 1,132 checks, 0 wrong. Nine focused tests cover out-of-order chunks, exact output, silent requests, cancellation, late completions, slow continuous progress, malformed chunks, real part-file backup and durable waiting/reopen. No live Telegram account or music download was used. The native Downloads recovery page was inspected in the real app on a synthetic profile; that preview was closed and computer-use ended.

Progressive playback is a separate subsequent change; see PROGRESSIVE-PLAYBACK-VALIDATION-2026-10-18.md for its current gate status.
