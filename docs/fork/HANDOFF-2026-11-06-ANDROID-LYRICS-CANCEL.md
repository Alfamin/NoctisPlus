# Android lyrics lookup cancellation — 2026-11-06

## Start here

Repository: `C:\Users\Alfamine\nplus-codex-257`  
Branch: `codex-android-262`  
Read `LOCAL-CHECKPOINT.md`, then `PHONE-WORD-TIMING-NEXT-277.md` and `ANDROID-PLUS-PARITY-266.md`.

## What changed

The native Android lyrics page cancels the old provider lookup when the current track changes or the page is disposed. It passes the token through `IOnlineLyricsSource.FindAsync`, treats expected cancellation as neutral, and retains the generation guard so late provider answers cannot replace the currently selected song. This makes skipping away from a slow lyrics search stop unnecessary network work.

Files: `src/Noctis.Mobile/ViewModels/LyricsPageViewModel.cs`, `tests/Noctis.Tests/MobileLyricsOnlineTests.cs`.

## Verification

- `MobileLyricsOnlineTests`: 11 passed, 0 failed.
- Full `codex-online-cancel-278` gate: 9,592 total, 0 failed, 83 skipped.
- Isolated frame groups: 19 passed / 0 failed; 50 passed / 0 failed.
- Finder: 1,796 OK, 0 wrong.
- Android Debug x64: 0 errors. Android Debug ARM64: 0 errors.
- No emulator was running for this gate; the modified behavior has not yet been checked in the real app. No profile, Telegram session, music library, or credentials were changed.

This is validated code, not a completed user-facing parity item. Do not mark DONE until the isolated real-app check passes.

## Next

1. Run the live isolated Android check: begin an online lyric lookup on one track, change tracks, confirm the first request cancels, the new track owns the lyrics view, and cancellation does not display a network-failure state. Preserve the audit profile; do not copy any session.
2. Continue the ordered work in `PHONE-WORD-TIMING-NEXT-277.md`: portable word-timing document/coverage/attribution, bounded ordered lrc.red → Unison → Kugou lookup with LRCLIB fallback, cache/offline/transient-miss semantics, then audio-fit seam and current/next-two priority. Do not treat catalog metadata as audio-fit proof, download a local model, or replace the desktop renderer.
3. Keep full gates, Android x64/ARM64 builds, and live isolated-app evidence before marking each integrated slice done.

## Rules

Log any new owner request in the external LEAN-PLAN before work. Preserve profiles/logins; `D:\music` is read-only; never copy Telegram sessions. Commit exact file names on `codex-android-262`; no main/native merge, tag, or release. No reset redemption or paid/model downloads.
