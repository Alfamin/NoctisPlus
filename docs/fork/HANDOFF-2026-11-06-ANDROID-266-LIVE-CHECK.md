# Android Plus #266 — live cancellation check checkpoint

Date: 2026-11-06 16:35 Asia/Tehran
Repository: `C:\Users\Alfamine\nplus-codex-257`, branch `codex-android-262`

## State

- Source and test changes from `b5102a36` remain pushed; the current tree had no uncommitted source changes before this checkpoint.
- Usage at checkpoint: 47% used, no secondary window, no earned reset credits. The quota watcher is running. At 90% used, wrap up only.
- Shared `OnlineTimingDocument` extraction (`9a78d3e8`) is fully gated; Android still uses LRCLIB only.
- Track-change cancellation (`b5102a36`) has 11 focused tests and the full 9,592-test gate, both Android architectures built, and Finder checks green. This is still NOT DONE because a real-app cancellation check has not been observed.

## Live attempt and evidence

The isolated app package `com.alfamin.noctisplus.auditlyrics279` was signed out and contained only synthetic `Noctis Audit A/B` songs. On the lyrics page, the UI reached its final “No Lyrics” state quickly. I could not capture a visible active `Searching for Lyrics…` state while changing the selected track, so the cancellation path is still not proven live.

I tried an emulator-only network delay and temporary local delay proxy. The proxy log showed no request; that result is inconclusive and does not prove that the app bypasses the system proxy. The test app was restarted once without clearing app data. No Telegram account/session, personal music, audio download, or model was used. No source code changed during this attempt.

The temporary proxy was stopped and emulator state restored: system proxy `10.0.2.2:10808`, delay none, speed full, Wi-Fi/mobile data enabled. The AVD was closed after the check. The user's other audit packages/profiles remain installed.

## Next

1. Repeat the isolated app check with two longer synthetic tracks so playback cannot end during inspection.
2. Capture the `Searching for Lyrics…` state for track A, then switch to B before the request settles. Confirm B remains current and no false provider failure is shown for expected cancellation.
3. If the app cannot be made to hold a real request through the configured emulator network, report the limitation honestly; do not mark live verification DONE based on mocks.
4. Continue `PHONE-WORD-TIMING-NEXT-277.md` only after the cancellation check, keeping the LRCLIB fallback and current-track priority.

No release, merge, tag, build, install, or account action was performed in this checkpoint.
