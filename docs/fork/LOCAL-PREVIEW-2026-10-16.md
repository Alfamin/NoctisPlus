# Noctis Plus: first local preview

Based on upstream stable Noctis 1.6.3 (`ad41b97c`), with namespaces/assembly names kept
compatible with existing plugins. Upstream main has not been merged.

## Delivered

- Separate product/profile/installer/Windows registration and startup identities.
- Empty release and Debug profiles; explicit `NOCTIS_PLUS_DATA_DIR` override.
- Profile-specific activation locks, canonicalized for equivalent path spellings.
- Plus branding and upstream MIT credit; original LICENSE retained.
- Own pinned updater and developer release feed, consistent installer/temp-file names,
  explicit refusal of official-app update assets, and empty-feed handling.
- Finder 2.11.1, True Shuffle 1.5.0, Lyric Motion 1.4.3 and WordLyrics plugin 1.2.4,
  bundled unchanged with source/version/hash manifest. Built-ins have individual switches
  and run independently of the community-plugin gate. Duplicate community copies cannot run
  twice, and the plugin manager cannot remove or overwrite application-owned module files.
- Process-local backend-only WordLyrics setup, preventing the legacy bootstrap from installing
  official Noctis or writing its plugins. Python/models remain a separate explicit setup.
- Last.fm session retention on connectivity/timeout/rate-limit/uncertain failures; only numeric
  error 9 expires a login. Stale verification/scrobble responses cannot clear a newer login.
- PowerShell 5.1 offline build script, bundled-file integrity checks, self-contained portable
  package, atomic ZIP replacement and SHA256SUMS. Own Windows CI artifact workflow; inherited
  upstream deployment/package-manager jobs are guarded to the upstream repository.

## Validation

- Unchanged baseline: 6,342 total, 6,300 passed, zero failed, 42 skipped (473.070 s).
- Full changed suite: 6,386 total, 6,344 passed, zero failed, 42 skipped (504.667 s).
- Current-source focused checks: 44 passed, zero failed. Includes the final two startup
  safeguards added after the full run: backend-only child environment and path normalization.
  These runs overlap; their counts must not be added together.
- The four delivered real DLLs initialize against the fork SDK under the headless host.
- Self-contained Debug window/title/separate profile observed. Interactive end-to-end
  download, lyric-job and playback testing is not claimed for this preview.
- Official settings, library and playlists remained unchanged in the local check.
- ZIP runtime/module integrity and private-profile/session/key exclusion checked locally.
- Existing upstream compiler/analyzer/obsolete-XAML warnings remain; build has zero errors.

Reports remain local under ignored `artifacts/plus/`; user data and logs are not committed.
See [QUICKSTART](QUICKSTART.md) for building/running, and [PROVENANCE](PROVENANCE.md) for rights.

## Remaining scope

- Create the Plus repository and choose included-module licenses before public release.
- Compile/test the optional per-user Inno installer and validate on clean Windows 10/11.
  Neither a compiled Setup.exe nor remotely executed CI/VM tests are claimed here.
- Explicit copy-only profile import with a journal, SQLite-safe snapshot and rollback.
  No live profile or authenticated session is automatically imported.
- Independent module updates and typed native integration; included modules currently update
  with a Plus build. No feature port or expanded recommender is claimed for this milestone.

The next bounded installer/import stage is estimated at 1–2 five-hour windows, depending on
clean-machine testing and migration edge cases. Recommendation improvements remain deferred.
