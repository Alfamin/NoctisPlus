# Native True Shuffle validation

The app owns the service, queue intent and three shuffle flyouts. The existing MIT engine,
cycle history and listening/taste logic are retained. Settings and data stay under
`dev.moshi.trueshuffle`. The old bundled DLL was removed; installed same-id copies are
ignored by the built-in feature host.

## Behaviour verified

- Restored current song with no upcoming rows → Play → select a custom mode uses the whole
  library and retains the current audio instance. No second song selection is needed.
- Every mode has a checkmark and lights the player/queue/mini button; Off clears it.
- Latest click wins, including Off while preparation is in progress. Preparation has an
  indeterminate ring and status; failures remain visible in the menu.
- Play-count/tag saves do not reject a click. Real membership/current-song changes retry.
- A 200-song rolling window continues across cycles, filtering missing/newly ineligible
  tracks. For you uses 25 songs. User additions stay ahead of generated songs, in order.
- Selecting another list switches context; selecting a single song retains continuation.
  Clearing the queue ends continuation. Explicit repeat/autoplay/radio changes release it.

Automated coverage is in NativeTrueShuffleTests, TrueShuffleQueueTests and
TrueShuffleDomainTests. Real-app screenshots were inspected with the computer-use skill on
`artifacts/plus/native-night-visual/profile`, an empty synthetic profile with 2,134 generated
WAV files, zero volume, no Telegram session and WordLyrics disabled. Main bar, mini player,
and the Queue page all opened their native menu and applied a custom mode. Queue rows filled
while the restored song stayed loaded. The preview was closed and computer use ended.

## First-activation measurements and limits

Before the change, an instrumented copy of the old plugin ran against a synthetic host:
first No repeats 54.25 ms; warm Discovery 30 ms; For you 62 ms. It logged each await in
ApplyCoreAsync. This **did not reproduce** the owner's long delay. Its inputs were remote
synthetic tracks and empty history, not the owner's library/session.

The native service was measured on the actual PlayerViewModel and 2,134 temporary local
files: first No repeats 127.59 ms, Discovery 108 ms, For you 108 ms. These are different
fixtures, so they are **not** a before/after speedup claim. Raw logs are under the ignored
`artifacts/plus/shuffle-before*` and `shuffle-native-timing.txt` paths.

The native architecture removes the old 1–5 second menu-attachment scan and does not wait
for listening-history import outside For you. Identity reading was already ruled out by
Claude's read-only 28–32 ms measurement of all 2,134 owner files. Real hardware, a large
listening log, cold disks and network libraries can still change first-use latency; no
unmeasured improvement is claimed.

## Related fixes in this checkpoint

Search uses the provider's song rank and up to three parallel, seven-day-cached artist-reach
lookups for ambiguous strong title matches. First results appear before that refinement.
The native top-result selector respects the catalog's chosen kind rather than always
promoting an exact-name artist. Public metadata checks passed 13/13 (Runaway, Drake,
Flaws and Sins and Take Care) through the existing system proxy. The real Search page
showed Kanye West's Runaway first. No bot/audio request was made.

Download quality is a FLAC/MP3 preference, sent to both providers before transfer. Synthetic
protocol checks cover settings menus, actual callback bytes, confirmation, ambiguity,
cooldowns, fallback, cancellation and mid-download preference changes. Cached opposite-format
audio cannot bypass a fresh preferred-format request. DeezLoad now accepts MP3 by filename
or MIME as well as FLAC. Byte completion stays below 100% until the file is saved; backup
and finalization stages are explicit.

**Live bot quality menus have not been tested against a Telegram account.** The code refuses
ambiguous/unconfirmed settings and reports the problem instead of guessing. This is still
a live-compatibility gate before release. The friend's exact 1.6.3.3 hang was not reproduced
without their status/log; this checkpoint fixes confirmed format/progress issues, not every
possible network or bot stall.

Playlist import is **Playlists → + New → Import Playlist**. Its subtitle now names Spotify
and Deezer alongside m3u. The native import pill was opened in the synthetic app; no playlist
was imported or downloaded during this check.

## Final gates

- Full Noctis suite: **6,504 total, 6,461 passed, 43 skipped, zero failed**, 485.336 s.
  `artifacts/plus/codex-native-verified-suite.log` and `codex-native-verified.xml`.
- Finder checks: **1,129 ok, zero wrong**, `artifacts/plus/codex-finder-verified.log`.
- Both projects build successfully with the existing SDK, `--no-restore`; existing warnings
  remain. `git diff --check` is clean.
- The first full run had one stale source-scan assertion expecting the old shuffle binding;
  it now verifies the native button and IsShuffleLit while retaining the accent-glyph check.
- Unwired WordLyrics mode-policy groundwork is covered in this final suite. Native WordLyrics
  worker/UI and progressive playback remain open. No release tag/installer was created.
