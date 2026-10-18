# Progressive playback — native integration, 2026-10-18

Status: DONE for the scope below after the final clock validation (see "Final clock validation" at the end). No release or installer. Live Telegram bots remain an unverified release gate.

## Behavior and ownership

- An explicit Play in native Search or an online album promotes/reuses the existing durable Downloads request. Browsing and ordinary Download/Add to downloads never autoplay.
- The same file, provider conversation, transport, proxy route and account are reused. No local HTTP server, fake network URL or media-server identity. A session-only reader registers the real final file path; incomplete files are never library imports or restorable buffer URLs.
- Four Telegram paths publish document size/actual MP3 or FLAC format before writing. The source manager publishes its captured format policy. Provisional MP3 awaiting a possible FLAC is never played early. Unknown duration, unsafe/unknown header or other codec retains complete-file fallback.
- Startup uses contiguous bytes, duration and observed throughput with headroom. It never treats filesystem Length or overall percentage as proof that earlier chunks exist.
- Producer and decoder hold separate leases. The final rename may happen while VLC is decoding the same open handle. Failure/reset wakes readers and invalidates the previous generation. Last lease closes the file; Stop/skip/shutdown cancels blocked reads before native Stop joins the decoder.
- Owned callback Media bypasses tail/header Parse and standby/crossfade staging. Native MP3/FLAC demuxers support growing input. Metadata duration wins over a growing MP3's partial length estimate. Buffering reflects a stalled playback clock rather than ordinary decoder read-ahead.
- Latest explicit Play wins across pages. Local play, pause, skip, clear or shutdown revokes pending autoplay without deleting/cancelling the queued download. The currently playing online row toggles Pause/Play. After import, the native library object is adopted without restarting or double-counting the play.
- Seeking is guarded during download; once validated/saved, seeking reopens the ordinary full local file. No unbuffered range-seeking is implemented. Changing the output engine during an incomplete nonseekable input currently restarts that input at the beginning; complete local playback resumes normally.

## Confirmed real-app findings

An isolated synthetic profile, Finder disabled, no Telegram session/credentials and generated audio only:

- Search source startup initially left an already-typed query empty. Fixed source attachment to rerun the query and disabling to clear old/in-flight online results; regression passes.
- Actual native Search Play clicked successfully through computer use. Generated MP3 began while the durable request was still transferring; native position advanced with seek disabled. Pause worked while the download continued. Full save enabled the seek slider even while paused.
- Partial MP3 duration estimate was wrong; corrected to the recording metadata duration. Rebuilt screenshot still required after this correction and later row/control polish.
- Rebuilt Search screenshot confirmed the duration correction, online Pause/highlight, secondary Download pill, and no spinner while the playback clock advances. Native mini player showed 0:16 elapsed + 0:07 remaining while the tail was withheld. Its seek controls and buffering ring are now aligned with the main bar (final rebuilt check pending).
- Complete-file import during active playback initially failed because TagLib's read/share-read open rejects a retained write handle. Completed transfers now exchange it for a read-only handle under the buffer lock, retaining the decoder and releasing write access before publishing FileSaved. An unreadable saved file now produces DOWNLOAD_IMPORT_FAILED instead of silent metadata rejection. New held-file metadata/import regression passes; actual FLAC album playback imported into the real synthetic library while paused, became a normal album row and enabled seeking (session.log 06:06:27).
- Long forced underruns exposed a backward playback-clock jump. The default splice engine's EngineFlush resets to the pending seek base even though this input cannot seek. Latest source delegates to FlushEngineInput, preserving sequential PCM and its audible position for progressive inputs; ordinary local seeks retain their flush behavior. Two new regression tests and Debug-only 500 ms CSV trace are saved, NOT BUILT/RUN at wrap. Recheck an actual long underrun before DONE.

## Checks

- Focused 37/37 passed in progressive-final-focused.log, including real LibVLC decoding generated MP3/FLAC before completion, owned input Stop/file-release, queue/part-file/quality pipeline, late request cancellation, saved-file adoption, source attachment, sparse/gap/reset and transfer watchdog behavior.
- Finder 1,132 / 0 wrong (progressive-final-finder.log).
- First integration full run: 6,562 total / 0 failed / 43 skipped, 467.688 s (progressive-full.log/xml), before read-only sealing and mini follow-ups.
- Expanded focused 38/38 after read-only sealing/import diagnostics/mini changes (progressive-verified-focused.log). Finder 1,132 / 0 wrong. Second full run is artifacts/plus/progressive-verified-suite.log and progressive-verified.xml, still running at wrap in exec session 23147; it does NOT include the latest unbuilt EngineFlush correction, tooltip labels or Debug trace/fixture metadata. Do not build over it. Read the actual result, then rebuild/test final sources and rerun full Noctis + Finder.
- Preview fixture setup: NativeFeaturePreview / ProgressivePlaybackPreview, Debug only. Requires explicit NOCTIS_PLUS_DATA_DIR and generated audio inside that profile. Uses real native Search/album/PlayerViewModel/Downloads/Downloader/LibraryService with synthetic source responses and delays; never logs in or opens provider/network requests. Excluded from Release.

Live Telegram settings and actual live transfer remain unverified release gates. No claims that the exact friend's 1.6.3.3 stall was reproduced, every network/codec works, or lyric accuracy changed.

Own preview PID16932 was closed after checking its full executable path. Computer-use kernel was reset. No active preview or authentication; only the full test process remains running.

## Final clock validation (Claude, 2026-10-18 morning)

The overnight flush correction (FlushEngineInput) was built and run in the real app on NEW synthetic profiles only
(`artifacts/plus/progressive-visual/profile-clock` to `profile-clock8`: settings.json + generated audio copied from
profile-album, nothing else; Finder disabled, volume 0/muted, no session or credentials). Play was pressed through
Windows UI Automation (the real Play buttons' Invoke), not autoplay. Screenshots: `progressive-visual/clock-shots`.

- FLAC album, two forced underruns: clock stopped at 14.304 s and 19.296 s with buffering shown (main bar and mini
  ring), resumed from exactly there, no backward jump, no restart/skip. Saved file byte-identical to the source,
  imported (library 0 → 1, row became the native local song), seek enabled, played to the end. No DOWNLOAD_IMPORT_FAILED,
  no VLC error.
- Pause while partial held the position (6.09 → 6.19 s), resume continued. Mini player: seek disabled while partial,
  enabled after the save.
- Seek (Local API on a synthetic profile; it uses the same player seek as the slider): while partial it is ignored
  (14.304 s kept, no error); after the save while paused it reopened the full local file at 6.000 s and stayed paused;
  Play continued from 6 s to the end.
- MP3 from native Search: one underrun (16.2 s), resumed, saved, imported.

**New bug found and fixed:** at the very end of a download-backed MP3 the bar jumped back about 1.5 s (23.5 → 21.98 s)
just before stopping. A temporary trace showed why: once the engine's segment drained, the position timer fell back to
VLC's own clock, which for an input that stalled runs ~2 s behind the audio actually played. `EngineTickTimeMs` now
gives no reading for a download-backed input when no engine segment is active (the end grace reports the real end);
local files keep VLC's clock exactly as before. Regression: `ProgressivePlaybackClockTests.
DrainedDownloadSegment_NeverFallsBackToVlcsLaggingClock`. The Debug preview now also records every backward position
step (`BACKWARD` rows), not only 500 ms samples. After the fix: FLAC (2 underruns) and MP3 twice, each on a fresh
profile, reached 24.000 s with no backward step other than the normal reset to 0 at the stop.

Gates on the final code: full Noctis suite 6,566 total / 0 failed / 43 skipped, 494.5 s
(`artifacts/plus/claude-clock-final-suite.log`); Finder 1,132 ok / 0 wrong (`claude-clock-final-finder.log`).
Focused progressive/importer/WordLyrics classes 38/38 and buffer/clock 8/8 before the end-of-track fix; progressive
classes 27/27 after it.
