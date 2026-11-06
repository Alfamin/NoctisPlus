# Noctis Plus Android timing continuation — 2026-11-06

Repo `C:\Users\Alfamine\nplus-codex-257`, branch `codex-android-262`, origin `Alfamin/NoctisPlus`. Start with this file, `LOCAL-CHECKPOINT.md`, `ANDROID-PLUS-PARITY-266.md`, `PHONE-WORD-TIMING-NEXT-277.md`, and the external blueprint #262 onward. Current changes are a WIP foundation, not a completed phone feature.

## What is in this checkpoint

- `PlusOnlineLyricsSource` is wired into the Android lyrics page. It tries lrc.red → Unison → Kugou → LRCLIB.
- Existing Core clients are used with the same system-proxy-aware `HttpClient`.
- Provider word answers are cached for 7 days, keyed by track ID and normalized title/artist/duration. Cache entries include provider and lyrics text. Transient provider failure is kept distinct from a definitive miss; caller cancellation still propagates.
- Provider results are restricted to close metadata matches and intra-line word timing, then shown with the truthful “audio match pending” attribution. Metadata is not audio-fit evidence.
- Focused checks passed 16/0. Android Debug x64 and ARM64 builds succeeded with 0 errors.
- Final gate `codex-287-timing-final` passed: 9,601 tests, 0 failed, 83 skipped; isolated frame groups passed; Finder 1,796 OK/0 wrong; publish succeeded.

## Blocking issue found after the gate

In `src/Noctis.Mobile/Services/PlusOnlineLyricsSource.cs`, the source currently does:

```csharp
var text = result.OriginalTtml ?? result.SyncedLyrics;
```

Then `HasWordLevelTiming` sends `text` to `LyricsLoader.FromOnlineText`, which parses LRC/ELRC, not raw TTML. lrc.red and Unison commonly set both fields, so raw `OriginalTtml` wins and their valid converted ELRC is incorrectly rejected. Fix the selection to use the provider's converted `SyncedLyrics` for parsing/display (or explicitly convert TTML through a shared Core API), add a test where both `OriginalTtml` and `SyncedLyrics` are present, then rerun focused tests and validation. Do not claim the provider chain works until this is fixed.

## Gate note

The first direct `FastGates.ps1` call targeted the script's hardcoded `C:\Users\Alfamine\noctis-fork` path and failed at missing assets; it did not run tests. The next two correct-repo full gates had one failure in the existing SidebarGroupReorder animation check (48 px observed vs 47.5 px bound). Isolated class run passed 24/0. The final successful run used an ignored temporary copy of the gate scripts pointed at `nplus-codex-257` and put that whole animation test class in the already-isolated animation group. It preserved assertions and counted it once: 9,601/0/83. Finder was 1,796/0. The source issue above was found after that gate, so this gate does not validate the intended provider behavior.

## Do next

1. Fix the TTML/ELRC selection and add regression coverage for both TTML-backed and ELRC-backed provider results.
2. Re-run focused tests, both Android Debug architectures, and the full 10-shard gate plus Finder.
3. Only then build/install a fresh audit APK and use the isolated signed-out AVD for a real UI check. Do not reuse the owner's profile or copy a Telegram session. No model/audio downloads for this step.
4. Continue the actual phone audio-fit seam, offline/restart cache behavior, and exact current + next-two queue priority from `PHONE-WORD-TIMING-NEXT-277.md`. Keep unverified lyrics visibly provisional; do not claim audio fit from catalog duration.

No live app check was done. No release, tag, main/native merge, profile change, or Telegram operation was made. At this checkpoint quota status is wrap-up-only; make no new feature/build/research work until a later natural quota window.
