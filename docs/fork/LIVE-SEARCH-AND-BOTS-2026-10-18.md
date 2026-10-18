# Native Search and live bot checks — 2026-10-18

Continuation from `1637c278` on `native`. No installer, tag or main merge.

## Changes

- `SearchBoxBinding` gives the shell's shared input a typed connection to Search. Navigation clears/replaces the box
  without treating that as the user deleting Search's query. History explicitly restores its query; sidebar Search
  restores the last query, results and filter chip. Already-posted stale debounce callbacks are ignored. Back opens
  the Search box so the restored query is visible. User Clear still clears the query normally.
- Artist `nb_fan` metadata is retained in a bounded, persisted side table keyed by provider ID. `CatalogArtist` record
  equality is unchanged. Ordinary Search reads these counts from its existing artist response; ranking makes no
  per-artist HTTP requests. The small logarithmic term does not replace title/name relevance or library evidence.
- Name-only artist navigation applies album/recording evidence and explicit choices first. Only exact Deezer namesakes
  with known counts and a decisive gap (at least 1,000 fans, at least 10 times the next) resolve automatically. Comparable
  or missing counts retain the chooser. An old ambiguous cache may get one artist-search refresh, bounded to six seconds;
  cancellation allows a later attempt. There is no list of famous artists or cross-provider ID guessing.
- Bot quality changes read edited menus while awaiting callback acknowledgement. Bots can edit immediately but leave
  Telegram waiting about 15 seconds for the acknowledgement; waiting serially consumed the settings deadline.
  The late task is observed, no second press is sent, and outstanding polling is cancelled on completion/cancellation.
- Music Hunters' actual nested menu distinguishes **Per-Service Quality** from the adjacent **Quality Info** toggle.
  The root **Audio Quality & Info** entry remains valid. No naming/ZIP/caption option is pressed.
- With diagnostic logging enabled, `Finder.Quality` writes menu labels/stages locally to `session.log`, without callback
  bytes, credentials or account details. A menu timeout names the pressed button and gives two recovery steps; the
  saved queue and the working provider continue. This is not interpreted as an authentication failure.

## Actual app checks

The staged `1637c278` publish was first installed into `C:\Users\Alfamine\NoctisPlus-Test\app` while closed. Only that
app directory was replaced. Its separately signed-in test profile was preserved; the installed app's profile/session
was never read or copied. An accidental physical Escape ended computer use; the owner explicitly authorized resuming.

- Live Search: `drake passionfruit`, `passionfruit drake`, `drake`, `hotline bling`, `j cole` led with the expected
  original recordings/artists. One warm final-candidate `j cole` observation: first online result 16 ms, complete 56 ms.
  This is a single observation, not a controlled speed comparison.
- Search -> Artists chip -> J. Cole -> Back retained `j cole`, results and chip. Library Artists filtered with `drake`
  -> close local filter -> Search rail restored the separate `j cole` Search query and Artists chip. The last small
  follow-up automatically reopens the box on Back; this was rechecked live in the final candidate with the diagnostic
  flags restored to the owner's values: Back immediately showed `j cole` and the previous results.
- J. Cole's Search tile opened online top songs and releases rather than only the owned ATM recording.
- A second fresh synthetic profile under `artifacts/plus/artist-popularity-live` contained only a scratch copy of ATM,
  with no Telegram login or credentials. The library Artists tile automatically chose `deezer:339209` (2,747,899 fans)
  over `deezer:412362611` (0); full online releases/top songs appeared. Playing that muted scratch copy and clicking
  the native player artist link opened the same full catalog. The synthetic app was closed afterward.
- The native accent spinner and loading labels were visible during the uncached artist load.
- Real signed-in test-app runs: Passionfruit, One Dance, No Role Modelz, then the final candidate's MIDDLE CHILD, KOD,
  Work Out. Artist-page Play started the latter songs while transfer was incomplete (seek disabled), then saved/imported
  them with no second Play click (seek enabled). The last three queue entries reached Saved with no errors.
- Telegram chats: the first three-song run used at most one `/settings` per bot. The final three used no new settings
  request in DeezLoad; Music Hunters retried once on the first song after its ten-minute wait expired, not per song.
- DeezLoad switched FLAC -> MP3 320 -> FLAC through its actual menu and persisted confirmation. Music Hunters traversed
  the full nested menu and confirmed FLAC once in ~6 seconds with the concurrent-read fix.

## Open limitation — do not claim full Music Hunters verification

Music Hunters intermittently ignores its first Audio Quality & Info callback, even though it returns the root menu.
Later MP3 and FLAC attempts hit the bounded menu timeout. Both-format switching for that bot is **not verified**.
The timeout is remembered, so later songs proceed through DeezLoad without re-opening settings per song. DeezLoad's
FLAC record stayed unchanged across the final three saved tracks. The actual audio format is still checked, including
when an unconfirmed provider is tried. Do not describe this as a dead bot or erase/recreate the login to fix it.

For a repeated menu failure, the app now says which button did not respond and asks the user to set the bot's audio
quality in its chat and retry. Investigate the intermittent callback separately; do not hide it behind replay-only tests.

## Verification provenance

- Quality-only full Noctis suite: **6,569 total, 0 failed, 43 skipped**, 478.140 s (`codex-quality-full.log/xml`).
- Combined Search/artist candidate: **6,571 total, 0 failed, 43 skipped**, 496.574 s (`codex-native-final-suite.log`, `codex-native-final.xml`).
  This precedes only the Back-box reopening follow-up.
- Finder final checks: **1,201 ok, 0 wrong** (`codex-native-final-finder.log`).
- Focused native Search navigation: **2 passed** (`codex-search-focused.log`).
- Final Noctis run including Back-box reopening: **6,571 total, 0 failed, 43 skipped**, 487.959 s,
  `codex-native-ui-final-suite.log`, `codex-native-ui-final.xml`. No production source changed after this gate.

Additional tests cover the real nested menu, late callback/menu races and poll cancellation; stable artist equality,
popularity persistence offline, missing/malformed counts, comparable namesakes, legacy cache repair, library/manual
evidence precedence, and Search query/result/chip preservation with normal user Clear.

`D:\music` was not modified. Live downloads went only to the test app's existing configured destination. No models,
dependencies or installers were downloaded. Diagnostic settings are restored selectively from the owner's backup after
testing, preserving newer volume/library/preferences; the Telegram login is retained.
