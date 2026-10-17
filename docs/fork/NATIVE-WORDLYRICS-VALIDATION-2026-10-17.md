# Native WordLyrics validation

Noctis now owns word-timing settings, download/library discovery, a song's Lyrics → Word
timing / repair action and native pill dialogs. The Python alignment backend is retained;
this step changes integration, not the model or its lyric-accuracy policy.

Off / Light / Medium / Full replaces auto + speed while preserving the legacy keys and data.
Medium maps to Half. First library load is a baseline, not a mass-retiming instruction.
New completed downloads and newly added local tracks queue once. All configured roots and
hidden local tracks are included. An old checkpoint resumes with its retry times intact.

The native repair dialog offers missing-only timing, retiming current words, fresh lyrics and
custom lyrics. LRCLIB results show recording duration differences; an exact Genius song page
can be read. Custom text drops blank lines and .done, preserves line guides and lives in a
private draft until the backend finishes. Library batches over 20 songs require confirmation.

Native progress supports pause/resume, stopping and a result report. File locks/permissions,
missing setup and provider/network failures keep visible diagnostic states and saved requests.
Completed sidecars reload the current lyrics directly while preserving an unsaved Studio preview.
No directory watcher, injected menu, plugin reflection bridge or automatic progress window remains.

## Setup and connection policy

Missing program/packages/models do not start setup. Both setup entry points ask explicitly:
about 1.33 GB for two models, plus Python/packages; cancel keeps queued work and existing lyrics.
The background Python entry also blocks package repair and model downloads, including corrupt
model recovery after readiness was checked. Existing components are reused by explicit setup.

Metadata uses Finder's configured/system proxy even while Finder is disabled. Python's HTTPS
requests go through a process-scoped authenticated loopback CONNECT tunnel using that same
HTTP/SOCKS/system route. TLS remains end to end. It allows DNS hosts on port 443; plain-HTTP
backend endpoints are unsupported. Closing Noctis closes its setup tunnel, so leave Noctis open
while the separately launched setup terminal works. No user credentials are copied to a new profile.

## Evidence

- Focused suite: 39 checks passed, including real fake-model child-process protocol tests,
  Unicode/metacharacter paths, preserved line guides, pause/resume/stop, backoff/checkpoint
  recovery, setup decline, missing/corrupt model download guards, current-sidecar refresh,
  migration/packaging, file sharing and native dialog bounds.
- Full Noctis suite: **6,523 total / 6,480 passed / 43 skipped / 0 failed**, 491.815 seconds.
  Finder: **1,129 ok / 0 wrong**. The setup-setting wording was clarified afterwards; this
  resource-only wording is checked again by the build/focused packaging checks.
  Logs: `artifacts/plus/word-native-verified-suite.log`, `word-native-verified.xml`,
  `word-native-finder-final.log`. First run failed the initially-empty-library new-file case:
  creation timestamps can lag the precise startup clock. The fix allows two seconds of startup
  timestamp tolerance; the regression forces a rounded timestamp. It does not backfill old files.
- Real synthetic app: scratch generated 30-second WAV; isolated word-native-visual profile,
  Finder disabled, no Telegram credentials/session, nonexistent backend and timing Off.
  Inspected library/folder pill, song menu → repair, action choices/custom editor, setup error
  wrapping, Features' four defaults and explicit setup confirmation. Cancel returned to the
  timing dialog, retained one queued synthetic song and created no setup.cmd or backend.
- During visual testing the shared ProgressRing exposed an Avalonia 12 error: infinite
  animations cannot use RunAsync. Replaced it with a cancellable sequence of finite turns;
  lifecycle test passes and a fresh preview added no crash log.
- Own preview closed and computer-use kernel reset. No downloads, installs, actual model run,
  real profile or D:\music writes. Alignment quality and live provider/Telegram behavior were
  not re-benchmarked here. No installer, release tag or main merge was produced.
