# Handoff — Claude cloud #261 (2026-11-05)

Branch `claude/android-plus-261` (pushed). Base 63b97047 → commits ff5abe1c, f82a4e31, d15c24d4, plus the test-fix/docs commit at the branch tip. Nothing was merged, tagged or released. Results are in CLOUD-261-STATUS.md.

**Standing rule for local work:** preserve the owner's local profiles, settings, API pairs and logins. The phone signs in with its own session; never copy one.

## Changed files
- `src/Noctis.Mobile/ViewModels/OnlineResultsViewModel.cs`, `tests/Noctis.Tests/PhoneFinderTests.cs`: P0.1 (Play/Download commands).
- `src/Noctis.TelegramSync/{TgLedger,TgSyncLog}.cs`, `tests/Noctis.TelegramSync.Tests/ConvergenceTests.cs`: P0.2 (M2). Checkpoint gains optional fields; it is backward-compatible and needs no migration step.
- `src/Noctis.Finder/ProgressiveAudioBuffer.cs` (timed waits), `src/Noctis.Finder/ProgressiveReadSession.cs` (new), `src/Noctis.Android/Services/ProgressiveDataSource.cs` (new), `src/Noctis.Android/Services/Media3AudioPlayer.cs` (factory wrap), `src/Noctis.Mobile/Services/Finder/PhoneFinderHost.cs` (`completeFilesOnly: false`), `tests/Noctis.Tests/ProgressiveReadSessionTests.cs`: P1.

## Local checks, priority order
1. **Android Debug compile, x64 then ARM64.**
   - `ProgressiveDataSource` is written against the Xamarin.AndroidX.Media3 1.11.0 metadata (IDataSource/IDataReader/DataSpec/C members inspected), but has never been compiled.
   - If the build rejects the `Read(byte[]?, …)` or `Uri` nullability, adjust the signatures to the binding's.
   - Command: `dotnet build src\Noctis.Android\Noctis.Android.csproj -c Debug -p:RuntimeIdentifier=android-x64 -p:JavaSdkDirectory=<JDK> -p:AndroidSdkDirectory=<SDK>`.
2. **Windows gates.** Ten-shard Noctis.Tests plus FreeMusicFinder.Tests; the shared buffer changed, so desktop VLC progressive tests matter. TelegramSync.Tests.
3. **Emulator, TEST bot, MP3 and FLAC each.**
   - First audio before the transfer completes: measure the time and the received bytes.
   - Throttle or drop the network mid-song: playback buffers and resumes without restarting or looping the prefix.
   - Pause/resume keeps position.
   - Seek within the received bytes; seek beyond them waits, and a skip during that wait does not hang.
   - Skip during an underflow releases the reader, while the download continues.
   - Cache-only Play stays out of Songs. Download joins Songs after completion. A completed cache song replays immediately.
   - Other formats (m4a/ogg) wait for the complete file.
4. **Phone Search.** Tap B while A is fetched: B plays and A never does. A→B→A. The buttons stay enabled while waiting.
5. **Two-device sync** (only once host wiring exists; still OPEN), including fast clocks.

## Still open
- Mobile list churn (keyed rows, coalesced progress).
- SAF playlist matching (inject an existence predicate into PlaylistMatcher).
- Sync controller, durable jobs and opt-in host wiring.
- Phone True Shuffle UI and mobile lyrics.
- Collision-renamed reopen edge (see status).
