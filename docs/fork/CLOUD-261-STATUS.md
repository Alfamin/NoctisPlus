# Cloud #261 status (Claude cloud, Linux, no device/account)

Base `cloud/android-plus-261` @ 63b97047 (contains 168a1266; ancestry verified). Task branch `claude/android-plus-261`.
Legend: IMPL / AUTO (cloud tests passed) / NOT-LIVE / OPEN / BLOCKED. **Nothing here is live-verified.**

| # | Task | State |
|---|---|---|
| P0.1 | Online Play: latest tap wins through the GENERATED PlayCommand (A->B->A, late/failed superseded wait, dispose) | IMPL + AUTO (ff5abe1c). With the old `[RelayCommand]` 2 tests fail |
| P0.2 | M2 remaining edge (fast writer reads another same-second post before its own read-back) | IMPL + AUTO (f82a4e31). Without the fix 2 tests fail |
| P1 | Android progressive playback: portable read session + Media3 DataSource, wired, phone host no longer waits for complete files | IMPL (d15c24d4) + AUTO for the portable layer; Android NOT compiled; NOT-LIVE |
| P2.1 | Mobile list churn (keyed rows, coalesced progress) | OPEN |
| P2.2 | SAF playlist matching | OPEN |
| P2.3 | Sync controller / durable jobs / host wiring; phone shuffle; lyrics | OPEN (sync stays off; no setting exposed) |
| Build | Android compile | BLOCKED in cloud (Android SDK host `dl.google.com` denied by network policy, #260); not retried |

## Exact results (.NET SDK 10.0.113, Linux x64)
- `Noctis.TelegramSync.Tests`: **71 total, 0 failed** (66 + 5 new same-second tests).
- `Noctis.Tests` focused: `ProgressiveReadSessionTests` + `PhoneFinderTests` **23 total, 0 failed**.
- Broad `Noctis.Tests` run (on the #260 build, 168a1266, before this pass; test DLLs were rebuilt during the run): **9,524 total, 60 failed, 98 skipped**.
  - None of the 60 are in the phone/Finder/sync classes. Biggest groups: NativeWordLyrics 17, AutoUpdateViewModel 15, AutoUpdateLaunchInstall 5, MiniPlayerFrost 3, EqualizerTogglePreset 3.
  - Four were re-run alone and reproduce. Each is Linux/root-specific:
    - `StreamedPlaylistAddTests` (×2): Windows `\Music\` path literals;
    - `OnlineTimingSidecarTests`: read-only attribute ignored by root;
    - `MissingQueueTrackTests`: Windows drive-not-present semantics.
  - The other 56 were not individually triaged. This is not a green suite; the Windows ten-shard gates decide.
- `FreeMusicFinder.Tests` was NOT re-run in this pass. The last run (1753 ok / 7 wrong, Windows-only semantics) predates the #260 Finder edits and this pass's buffer edits.

## Decisions
- **P0.1.** `PlayCommand`/`DownloadCommand` use `AllowConcurrentExecutions = true`. The toolkit's default disables the command while a wait runs (`CanExecute` false), so the row buttons could not start B to cancel A.
  - One current wait replaces the per-song in-flight set. A superseded wait never plays and never shows its error.
  - Cancelling a wait never cancels the shared download.
- **P0.2.** The ledger marks this device's own edits *provisional* until their post is read back. It holds the best remote item that lost only to such an edit.
  - Read-back, or a remote item beating the edit, makes the same comparison every reader makes.
  - A newer own edit keeps the mark and the held item.
  - Saved as optional checkpoint fields `Provisional`/`Shadowed`; older checkpoints load with none (previous behaviour).
- **P1.** Reads use timed waits (250 ms slices plus a Java interrupt check), because Media3 cancels loads with `Thread.interrupt()`, which does not wake a .NET monitor. No spin loop and no whole-file RAM.
  - Underflow keeps buffering. The end of input is reported only at the end of a complete transfer. A failed transfer surfaces as a Java `IOException`.
  - Seek works within received bytes. Beyond them it waits, because the Telegram transfer is sequential and there is no range fetch. Past the end of a complete file it is refused.
  - Only `file://` paths with a live transfer use the session. Finished files, content:// and authenticated LAN streams use the inner stock source.
  - MP3 and FLAC only (existing `ProgressiveTransfer` header/format rules). Other formats still wait for the complete file.
  - **Collision edge:** if a saved file was renamed on a name collision, a seek that reopens after completion reads the registered path.

## Not live-verified / local checks
See HANDOFF-2026-11-05-CLOUD-261.md.
