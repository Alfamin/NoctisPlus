# Native Downloads song actions

Completed history entries become normal song rows after the library scan finds the saved file.
Double-click plays from the completed list; the cover toggles play/pause. The shared Noctis track
menu provides metadata, playlists, play next, artist/album navigation, lyrics and file tools.
The trailing remove button and menu's **Remove from this list** remove the download request only.
Neither is connected to library removal or file deletion.

## Matching and lifetime

- Finder publishes the actual completed output path and duration through `DownloadEntry`.
  Saved history reconstructs that path once on restore; progress snapshots perform no file probes.
- Downloads indexes the visible library by normalized absolute path. A known path waits for that
  exact file's scan, even when another copy has identical tags. Invalid paths are not guessed.
- Legacy entries without a path can match one unique `RecordingMatch` candidate. A lazy title
  index avoids comparing every history row against every library song. Versions and durations
  still go through Finder's existing recording rules; ambiguous copies stay non-actionable.
- Library updates replace row track references in place. Queue progress does not rebuild the
  library lookup. The first navigation also catches startup cache loading without an update event.
- Unresolved completed entries say **Adding to library…**. Pending, running and failed entries
  retain their existing queue controls. No download or rescan is started by opening this page.
- Disposal unsubscribes both sources and rejects posted callbacks. Removing a repeated history
  entry uses that row's queue handle, even if another entry points to the same library track.

## Verification

`DownloadsPageTests` exercises delayed scanning, exact-path preference, ambiguous/version/length
matching, metadata replacement, removed tracks, stable progress rows, native menu binding,
playback, duplicate history removal, byte-preserving removal and queued-callback disposal.
Finder's queue checks exercise completed paths, restart and history removal against scratch files.

Real-app visual checks used `artifacts/plus/d4-profile`, a library of existing scratch copies and
synthetic paused history. The page, covers, shared menu and correct native metadata dialog were
checked; double-click playback of the scratch `Free Smoke` copy lit the row and populated the
player. No Telegram login, bot requests, audio download, real-profile write or `D:\music` write.

For computer-use checks with another Noctis instance open, name a copy of the Debug apphost
`NoctisPlusVisual.exe` beside `Noctis.dll`. Always set `NOCTIS_PLUS_DATA_DIR` to the isolated profile
and select that exact executable in the helper. The helper otherwise confused same-named instances
after a restart. Stop only the test PID and reset the helper when finished.

Gate logs are kept locally under `artifacts/plus/codex-d4-*`; the master tracker records final counts.
