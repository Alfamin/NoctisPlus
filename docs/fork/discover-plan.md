# Discover / For you: plan and continuation notes

Branch `discover-a`, started from `native` at v2.2.0 (`11995e63`), draft PR Alfamin/NoctisPlus#12 into `main`.
**Done:** Milestones A, B, C (criteria in section 5, written before measuring; results in section 6) and D (the model gate:
no-go, section 7 and `discover-model-gate.md`), then song-to-song candidates (section 10: Deezer artist radio, plus Last.fm
`track.getSimilar` with the user's own key). **Next:** only what the owner approves. Never merge, push to `main`/`native`, or tag.

The milestones are the owner's (brief section 9):
- **A. Working Discovery improvement:** ranking and identity audit; filtering, personalisation, novelty, diversity, song-level
  dismissal. The shelf must stay usable with no telemetry or provider data.
- **B. Learning data:** only the missing high-value exposure and feedback telemetry, reuse playback tracking, update derived
  preference statistics safely, focused persistence and Undo tests.
- **C. Evaluation and reliability:** compare baseline and improved behaviour, failure cases, offline and cache behaviour,
  performance. Fix correctness problems before adding complexity.
- **D. Future-model readiness:** a concrete model adoption gate and the architectural boundaries it needs. No model download or
  execution, no whole-library processing, no vector database, no speculative ML without separate authorisation.

> The first copy of the brief stopped in section 5, so the B–D in the first version of this file were my guesses (B was
> "track-level candidates"). They have been replaced by the owner's definitions above. Track-level retrieval is not in any
> milestone; D outlines it (`discover-model-gate.md`, section 6) without building it.

## 1. What was already there (audit)

| Area | Where | What it does | Gap found |
|---|---|---|---|
| Taste | `ForYou.cs` `Taste.Seeds` | Artist weights from plays (single 30-day half-life) plus `0.4·ln(1+owned)` | One time horizon: a 3-week phase pushes years-long favourites out of the 15 seeds. Every replay counted in full (one looped song outweighs many liked songs). Favourites, ratings and playlists ignored. |
| Play evidence | `TrueShuffle/ListeningLog.cs` `PlayTracker`/`PlayRecord`, read by `TasteSources.ShuffleListens` | Audible ms, coverage, end reason; pauses and seeks handled | Fine. Only recorded while the True Shuffle feature is enabled; left unchanged (see gaps). |
| Old history | `play_history.json` (`PlayHistoryEvent`) | Start time, coarse `Skipped` | Already used only for the time before listens.jsonl starts. Kept as lower-confidence evidence. |
| Richer taste model | `TrueShuffle/ForYou.cs` `TasteModel` | Track-level: favourites, ratings, playlists | Library-only (scores owned tracks). Its weights were reused as a reference, not the class itself. |
| Candidates | `CatalogForDiscover`, `LastFmSimilar` (artist.getSimilar), `SimilarArtists` (ListenBrainz similar *artists*), Deezer related artists, `CatalogRecommend` (artist top songs) | Artist-level retrieval, then each artist's top songs | **No track-level similarity anywhere.** Retrieval leans on popular songs of related artists. |
| Duplicates | `SearchRanking.DeduplicateSongs` | Same title key + same artist IDs + same rounded seconds | Missed the same recording on another provider (different artist IDs), with a remaster tag, or 1 s longer. |
| Familiarity | none | Only owned or queued songs were excluded | Songs the listener had already **heard** (streamed, or deleted after playing) came back as "discoveries". |
| Credits | none | — | A related artist's "top songs" could include a compilation or namesake song that doesn't credit them. |
| Dismissal | `DiscoverHidden` | Song-only, persistent; Undo works; legacy artist bans ignored | Matched on `CatalogEditions.Key`, which keeps "Remastered", so a remaster of a dismissed song came back. |
| Exposure | none | — | No record of what was shown, so the same top songs repeated on every refresh. |
| Stability | `ForYouShelfViewModel.NewPicks`, `ForYouSource` 12 h page | Background results never move cards; Refresh adopts | Fine, kept. |

## 2. Milestone A: what changed

All of this is deterministic and offline-testable. Nothing new is sent to any provider.

### Taste (`Taste.Seeds`, `ForYou.cs`)
- **Two horizons.** Recent (30-day half-life) plus lasting (365-day half-life, at half weight). Lasting only counts for an artist
  played well on 3 or more separate days, so one old play isn't a "lasting interest".
- **Kept places for lasting interests.** 5 of the 15 seeds go to the strongest lasting interests that recent weight left out.
  A recent phase can't erase older taste, and there's no clustering.
- **Diminishing returns per recording.** Each track's evidence goes through `2·ln(1+x/2)` (negatives are capped at −3).
  Ten liked songs now say more than one song on repeat.
- **Deliberate marks** from the library: favourite +2, rating 5 +1.5, rating 4 +1, rating 1–2 −1, disliked −1.5. These go per
  track, are compressed, and count at ×0.6. One disliked song lowers its artist a little; it never bans the artist.
- **Playlists** (`TasteSources.Playlists`, which reads `playlists.json`, read-only):
  - Songs in the listener's own playlists: +0.6 each, decayed by `TrackAddedAt`.
  - Imported playlists ("Imported from Spotify/Deezer…"): +0.05 per song, capped at 0.5 per artist per playlist. A
    2,000-song import counts as one decision, not 2,000 endorsements.
  - Smart playlists are skipped.
- **Saves from the shelf** (Download now / Add to downloads) count as a small fresh positive (+0.5) for that artist. A Play
  click counts for nothing by itself; the actual listen (`PlayRecord`) speaks for it.

### Candidates and ranking (`Discover.BuildAsync`)
- **Familiar recordings are left out.** A recording counts as familiar once it was audibly heard (3 s or more, or finished),
  or played per the old history. This is matched by recording identity, so a song can be not owned and still not new.
  Slips under 3 s (and picks replaced within 3 s) don't count.
- **Credit check.** A song has to credit the artist it was found for. Otherwise it's left out rather than guessed.
- **Recording identity** (`DiscoverIdentity.cs`):
  - Ignores clean/explicit labels, remaster tags, "album version", "bonus track" and featured guests in the title.
  - Keeps live, remix, radio edit, acoustic, instrumental, demo, sped-up and similar as separate recordings.
  - Two lengths more than 4 s apart are different recordings.
  - Duplicates across providers and albums collapse to their best-scored instance, preferring explicit.
- **Exposure rest.** A card that sat in the first 10 places of an earlier page without being used:
  - 1 page: ×0.8
  - 2 pages: ×0.5
  - 3 pages: rested for 30 days

  Exposure never counts against the artist.
- **Dismissals stay song-level.** One "Not for me" hides only that recording (now also its remasters and other-provider
  copies). Only from the second dismissal of one artist within 90 days are that artist's other songs lowered, by
  `1/(1+0.3·(n−1))`, never below ×0.4. Nothing spreads to the seed, genre or neighbours.
- **Position decay** uses the catalog's original order. Filtering one song never changes another song's score.
- **Variety.** Within the 20 visible cards:
  - at most 6 per seed (its own songs and its neighbours together)
  - at most 8 from the listener's own artists
  - still at most 3 per artist and never the same artist twice in a row

  When nothing else is left, the shelf is still filled.

### Telemetry (`DiscoverFeedback.cs`, `for-you-feedback.json` beside `for-you-hidden.json`)
- **A page counts as shown** only when the shelf is active and expanded, once per page id (`DiscoverResult.Batch`). Each card
  records its recording key, song id, artist key, seed, own/neighbour flag and position. Picks retrieved in the background
  but never displayed aren't recorded. Re-showing, Undo or navigating back never re-counts.
- **Actions per card:** Play, Download, Queue, Open (album or artist), Dismiss. Each kind counts once per card per page,
  however often it's clicked. Undo retracts the Dismiss.
- **Listening outcomes aren't copied.** They stay in `listens.jsonl` and get joined by recording key when needed, so
  there's no second playback ledger.
- **Retention:**
  - 180 days, at most 120 pages and 3,000 actions; pruned on load and on write
  - Written off the UI thread with an atomic write
  - Refuses to write through redirected (reparse-point) paths
- **Reset.** The "For you" header has a right-click item, "Reset For you…". `ForYouSource.ResetLearning` clears dismissed
  songs, the feedback file and the saved page, then rebuilds. Listening history, favourites, playlists and play counts
  aren't touched.

Not changed: play-count semantics, Statistics, True Shuffle's behaviour and its `listens.jsonl` format. The old 2.2.0
`Saved` format of `for-you-hidden.json` still loads (v3 adds `Identities`). The cached page format is still Version 1;
old pages without a `Batch` are just not recorded as exposures.

## 3. Evaluation

**CI (Windows, `tests/Noctis.Tests/DiscoverLearningTests.cs`, synthetic only; B adds `DiscoverTelemetryTests.cs`, see section 4).** Covers:
- identity merges and keeps
- heard, namesake and compilation exclusion
- cross-provider dedupe
- seed and own-artist caps
- exposure rest and per-click dedupe
- one-vs-several dismissals and Undo
- persistence, retention, reset
- lasting interest beside a 16-artist recent phase (the 2.2.0 baseline drops it)
- replay compression, imported vs deliberate playlists, playlist parsing, familiarity slips
- an offline chronological replay on a synthetic persona

**Finder suite (`tests/FreeMusicFinder.Tests`, not run by CI).** The native shelf test now also checks:
- a page is recorded as shown once, with positions
- a double Play click counts once
- Undo retracts the dismissal's learning
- returning to the page doesn't re-count it

### Replay on your own data (local, offline, read-only)

Run it on a **copy** of your Noctis data folder, with Noctis closed. Nothing is sent anywhere: every HTTP request goes to a
handler that refuses it before it leaves. Nothing in the copy is written (a Finder-suite check hashes every file before and
after); the shelf's state is copied again to a temporary folder before the engines use it. It prints only counts and
percentages, never titles or names.

```powershell
# PowerShell, from the repository root
$copy = "$env:TEMP\noctis-replay"
Remove-Item $copy -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $copy | Out-Null
$data = "$env:APPDATA\Noctis Plus"   # or your NOCTIS_PLUS_DATA_DIR
Copy-Item "$data\play_history.json","$data\library.json","$data\playlists.json" $copy -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$copy\plugin-data\dev.moshi.trueshuffle" -Force | Out-Null
Copy-Item "$data\plugin-data\dev.moshi.trueshuffle\listens.jsonl" "$copy\plugin-data\dev.moshi.trueshuffle\" -ErrorAction SilentlyContinue
# Milestone C: the shelf's own files and the saved catalog (metadata only: names, IDs, top songs; it can be a few hundred MB)
$finder = "$data\plugin-data\dev.moshi.freemusicfinder"
New-Item -ItemType Directory "$copy\plugin-data\dev.moshi.freemusicfinder" -Force | Out-Null
Copy-Item "$finder\for-you-feedback.json","$finder\for-you-hidden.json","$finder\for-you-last.json" "$copy\plugin-data\dev.moshi.freemusicfinder\" -ErrorAction SilentlyContinue
Copy-Item "$finder\artist-catalog" "$copy\plugin-data\dev.moshi.freemusicfinder\artist-catalog" -Recurse -ErrorAction SilentlyContinue
$env:FMF_DISCOVER_REPLAY = $copy
dotnet run --project tests/FreeMusicFinder.Tests -c Release
Remove-Item Env:FMF_DISCOVER_REPLAY
Remove-Item $copy -Recurse -Force
```

The output has four parts:
1. **Seed replay** (chronological): one line for `before` (the 2.2.0 seed step) and one for `now`:
   - **recall:** share of artists you enjoyed in the next 14 days (2 or more good plays) that were seeds
   - **returning favourites:** the same, but only for artists you loved months earlier and came back to
   - **seeds skipped next:** seeds you only skipped in the following 14 days
2. **Shelf** (from `for-you-feedback.json`): pages, cards, actions and outcomes, then the M1–M6 metrics averaged over the
   pages the shelf actually recorded, and how many dismissed songs were shown again later.
3. **Saved page** (`for-you-last.json`): its metrics, including the median Deezer rank of its songs.
4. **Both engines on your saved catalog**, as of the saved page's date: the M1–M10 metrics for `before` and `now` over three
   pages (each with its first 12 cards in view; on page 1 the first card and the next by its artist are dismissed and one
   other card is finished), and how many lookups the saved catalog could not answer. Those artists count as unavailable for
   both engines; a high count means the comparison covers only part of what the app would see.

Caveats:
- Without `library.json` the replay runs on plays alone (no favourites, ratings or ownership).
- Library marks and playlists are today's, so they are applied at every cutoff (a mild look-ahead).
- Owned leakage uses today's library by title and artist; the app also checks the files and the download queue.
- The saved catalog is whatever For you and Search fetched lately; the baseline's seeds may need artists it never fetched.

## 4. Milestone B: what changed

Every derived statistic is recomputed from raw records at each build (`DiscoverFeedback.Learn`). Nothing derived is stored, so
Undo, Reset and pruning cannot leave stale statistics behind.

### Exposure that was actually on screen
- The shelf is a wrapping grid inside the page's vertical scroll, so "first 10 positions" was a guess.
- `ForYouShelfView` now reports, after each scroll or layout pass (coalesced, background priority), the cards that are at least
  half inside the scroll viewport (`ForYouShelfView.InView`).
- The view model passes their page positions to `DiscoverFeedback.Seen`. A write happens only when a new place comes into
  view, and nothing is recorded while the shelf is inactive or collapsed.
- `DiscoverBatch.Seen` holds the positions in view. Resting uses only those. Pages recorded before this (`Seen` = null) keep
  the old "first 10" rule.
- Not done: dwell time. A card scrolled past quickly still counts as seen once.

### Outcomes from real listening (reusing `PlayTracker` records)
- **Outcome per played or saved card,** joined by recording key to the listener's own records from the moment they first acted
  on it (2 minutes of clock slack). From best to worst:
  - **Kept:** saved, then favourited, rated 4+, or put in one of their own playlists (not an import); or two good plays on
    separate days
  - **Saved:** downloaded or queued
  - **Enjoyed:** a finish or qualified play
  - **Sampled:** a partial listen
  - **Skipped:** an audible early skip and nothing better
  - **None:** a quick skip, a failed start or no play. A shown card with no action has no outcome, which is never a negative.
- **Per-artist effect (`ArtistFactor`):** dismissals plus early skips, minus good outcomes, minus one. Each unit over zero
  lowers the artist's other songs (never below ×0.4). Good outcomes alone lift the artist by up to ×1.25, so a new artist the
  listener took to gets more songs. One dismissal or one early skip still changes nothing beyond that song (it is now heard,
  so excluded anyway).
- Plays from before the card was acted on never count as its outcome.

### Playback evidence that is not taste (`TasteSources.ShuffleListens` / `Load`)
- **Under 3 audible seconds is neutral**, whatever the end reason (a slip, a failed stream start or buffering). It was −0.3
  for a quick Next; it is no longer a dislike and no longer marks the song as heard.
- **"Finished" needs at least half the song or 30 seconds heard.** Otherwise it was a failed stream or an unknown length,
  not a finish (it used to count +1.5).
- **Duplicate play events** (the same recording starting within the same 2 seconds) count once (`TasteSources.Distinct`).
- `listens.jsonl`, True Shuffle and play counts are unchanged; only For you's reading of them changed.

### Persistence and Undo (tests)
- Truncated or unknown-version feedback files are ignored, and the next write replaces them.
- Reset between queued writes leaves only post-Reset data.
- Seen positions and retractions survive a reload.
- Pages cached by 2.2.0 (no `Batch`, `Seed` or `Own`) still load and are never counted as shown.
- Undo retracts the dismissal's learning (Finder suite, view-model level).

### What leaves the computer (A and B; section 10 adds two items, marked)
- **Last.fm `artist.getSimilar`** (only with the user's own API key): a seed artist name.
- **Last.fm `track.getSimilar`** (section 10; only with the user's own API key): the title and artist of each of up to 10
  best-liked recordings.
- **Deezer artist radio** (section 10): a Deezer artist ID only.
- **MusicBrainz:** a seed artist name and up to 3 of the listener's song titles for that artist, to identify the right artist
  (namesake safety).
- **ListenBrainz Labs similar artists:** an artist MBID.
- **Deezer, else Apple:** artist names and IDs, for search, related artists and top songs.

Not sent: listening history, play counts, the feedback file, library paths, audio, Telegram sessions, credentials. Requests
go through Finder's existing HTTP clients (its configured proxy, pacing and caches). Offline-mode handling is checked in C.

## 5. Milestone C: metrics and criteria (written before any results)

Written and committed before the comparison was run, so the results can't move the goalposts. Constants from A and B are not
tuned in C; only correctness bugs are fixed, each with a regression test.

### Metrics (per page = the 20 cards the shelf shows)
| # | Metric | Definition |
|---|---|---|
| M1 | Duplicate rate | Cards whose recording (`DiscoverIdentity.Key`) already appeared earlier on the same page, ÷ cards. |
| M2 | Owned leakage | Cards matching a library song by recording identity, ÷ cards. |
| M3 | Heard leakage | Cards matching a recording heard (3 s or more, or the old history) before the build, ÷ cards. |
| M4 | Repeat exposure | Cards that were in view on the previous page and not used, ÷ cards. Also: cards in view on each of the last 3 pages. |
| M5 | Artist spread | Distinct artists on the page; most cards from one artist. |
| M6 | Seed spread | Distinct seeds (the listener's artist a card came through); most cards through one seed. |
| M7 | Popularity concentration | **Top-3 share:** cards that are among the first 3 songs of their artist's catalog top list, ÷ cards. Also the mean place in that list (1 = the artist's most popular song) and the median Deezer `rank` where known. |
| M8 | Dismissal effect | Dismissed recordings (any edition or provider) on the next page; cards of an artist with 2+ dismissals, this page vs next. |
| M9 | Outcome effect | Cards of an artist on the next page after an enjoyed pick of theirs, and after 3 early skips, compared with no outcome. |
| M10 | Cost | Build time and bytes allocated with an in-memory catalog (network excluded) on a 50,000-song library with 100,000 plays; how often the in-view report runs. |

Baseline: the 2.2.0 engine (seed step, `listens.jsonl` reading and ranker) kept runnable as `DiscoverBaseline`, fed the same
catalog. It had no exposure memory or outcomes, and it hides dismissed songs the 2.2.0 way.

### Promotion criteria (the new engine stays the default only if all hold, on every synthetic persona)
- **P1:** M1, M2 and M3 are 0 for the new engine.
- **P2:** no dismissed recording returns on the next page (M8), in any edition.
- **P3:** repeat exposure on the second page (M4) is at most half the baseline's, or at most 25 %.
- **P4:** at most 3 cards per artist and 6 per seed when the pool allows it, and at least as many distinct artists as the baseline.
- **P5:** top-3 share (M7) no higher than the baseline's. Retrieval is unchanged, so this is measured, not something C has to improve.
- **P6:** in the chronological seed replay, recall at least the baseline's minus 2 points, seeds skipped next at most the
  baseline's plus 2 points, returning favourites at least the baseline's.
- **P7:** the local part of a build (everything but the network) takes under 2 s on the 50,000-song library, and ranking alone
  under 250 ms. The tests assert a looser bound so a slow CI runner doesn't make them flaky.
- **P8:** in every failure case (provider unavailable or slow, Offline mode, cancellation, corrupt files, empty or tiny library,
  huge imported playlist) no exception escapes, the shelf keeps or brings back its cached picks, Offline mode sends no request,
  and cancellation ends the build within 1 s.
- **P9:** layout passes elsewhere in the window (the playback bar, lyrics) trigger no in-view computation; scrolling, resizing
  and a new page trigger at most one per dispatcher pass.

### Stopping criteria
- **S1:** if a hard criterion (P1, P2, P8) fails and the cause isn't a local correctness bug, stop and report, recommending the
  baseline behaviour for that part, instead of adding complexity.
- **S2:** if more than half of the new engine's cards are an artist's top-3 songs (M7) on the synthetic personas or on the
  owner's replay, re-ranking inside artist top lists can't fix the famous-songs lean. That is the evidence threshold for
  considering track-level retrieval later; C doesn't add it.
- **S3:** no tuning to my own synthetic personas. They are written by me and can favour the new engine; a criterion that only
  passes after tuning counts as failed.
- **S4:** if the owner's replay shows the new engine worse than the baseline on P1–P4, stop and report before D.

Offline evaluation of logged data can't show causal satisfaction gains; only real listening over time can.

## 6. Milestone C: what changed and what the numbers say

### Baseline kept runnable
- `DiscoverBaseline.cs`: 2.2.0's `listens.jsonl` reading, seed step and ranker, frozen. Its dismissals use 2.2.0's rule
  (`DiscoverHidden.HidesEdition`). Only the evaluation and the replay call it.
- `Discover.ForListenerAsync` is now the one build path; `ForYouSource` and the evaluation both call it.
- `DiscoverEvaluation.cs`: the metrics (`DiscoverPageMetrics`), a catalog wrapper that records each song's place in its
  artist's list (`PlaceRecorder`), and the three-page script both engines run (`DiscoverEvaluation.RunAsync`).

### Synthetic comparison (`DiscoverEvaluationTests`, CI)
The world: 4 genres of 12 artists, plus 3 megastars on every similar-artist list. Each artist's list is popularity ordered and
carries real-list traps: song 1 again as a remaster on another provider (placed 2nd: at 4th the 3-per-artist cap hid it from
both engines, so the trap never fired; moved before any criterion was evaluated against it), a compilation song that does not
credit the artist, and a live recording. Personas: **steady** (two bands for a year, a three-week phase, hits of related bands
streamed but not owned), **streamer** (no library, duplicate events, failed starts, empty "Finished" streams), **collector**
(owns whole catalogs, a 90-song Spotify import, ratings), **tiny** (one song). The world and personas are mine (S3): they can
favour the new engine.

Mean of the three pages, baseline → now:

| Persona | Duplicates | Heard | 2nd-page repeat | Artists (max each) | Seeds (max each) | Top-3 share | Uncredited |
|---|---|---|---|---|---|---|---|
| steady | 30 % → 0 | 87 % → 0 | 55 % → 15 % | 8 (3) → 12 (3) | 6 (5) → 7 (6) | 97 % → 48 % | 0 → 0 |
| streamer | 32 % → 0 | 90 % → 0 | 50 % → 20 % | 8 (3) → 13 (3) | 6 (5) → 6 (6) | 97 % → 52 % | 0 → 0 |
| collector | 15 % → 0 | 7 % → 0 | 55 % → 30 % | 10 (6) → 14 (3) | 7 (13) → 6 (6) | 55 % → 57 % | 23 % → 0 |
| tiny | 32 % → 0 | 7 % → 0 | 55 % → 25 % | 8 (3) → 9 (3) | 2 (20) → 2 (20) | 90 % → 62 % | 0 → 0 |

Owned leakage was 0 for both engines everywhere (both get the same library check). Dismissed songs came back 0 times for
both. A twice-dismissed artist went from 3 cards to 0 on the next page with the new engine (two songs hidden, the third
lowered ×0.77 and rested ×0.8) and 3 → 2 with the baseline. Cards in view on each of the last 3 pages: 40–55 % → 0–15 %.
"Uncredited" (cards whose credits don't name the artist whose list they came from) is a diagnostic I added after the
criteria were set, to explain the collector's P5 result.

**Verdicts against section 5:**
- **P1 PASS** (0 duplicates, owned or heard cards) and **P2 PASS** on every persona.
- **P3:** PASS for steady, streamer and tiny; **FAIL for collector** (30 % against a limit of 27.5 %, half the baseline’s 55 %). One page of passing over
  only rests a song ×0.8, by design; repeats fade over three pages. Not tuned (S3).
- **P4 PASS** (tiny has too few seeds for the per-seed cap; the shelf is filled anyway, as designed).
- **P5:** PASS for steady, streamer and tiny; **FAIL for collector** (57 % against 55 %). The baseline's lower share comes
  from deep-list cards that shouldn't be there: 23 % of its cards were compilation songs not crediting the artist.
- **P6:** equal on steady (recall 86.1 %, returning favourites 100 %, skipped 0 %); streamer and collector have too little
  history for a chronological cutoff.
- **S2 triggered:** the new engine's top-3 share is 48–62 %, over 50 % on three of four personas. Re-ranking inside artist
  top lists halves the famous-songs lean but can't remove it. That is the evidence threshold for considering track-level
  retrieval later. The owner's replay (M7 on the saved catalog) is the real check.

### Reliability fixes, each with a regression test (`DiscoverReliabilityTests`, CI, unless noted)
1. **A catalog outage blanked the shelf.** With no catalog reachable, Refresh got an empty page and showed it. Results now
   carry `Unreachable`; the shelf keeps its picks, or brings back the last saved page, with the existing "Couldn't refresh
   right now. Showing your last picks." (view-model check in the Finder suite).
2. **An empty failure lit the "new picks" dot.** `PicksReady` now fires only for a page that was adopted.
3. **One provider timeout aborted the whole build.** ListenBrainz's HTTP client timeout is a `TaskCanceledException` nobody
   asked for; it escaped the per-artist catch. It now fails that artist only. A real cancellation and Offline mode still stop
   the build.
4. **A malformed card in for-you-feedback.json broke every later build** (Learn threw on a null card or key until Reset).
   Malformed entries are now skipped one by one.
5. **The build ran on the UI thread.** After its first await, reading the history, learning, taste and ranking all resumed on
   the UI thread (about a second on a large profile). The whole build now runs on the thread pool (test: 72 of 72 lookups ran
   on the UI thread before, 0 now).
6. **A late progress report overwrote the final status** (the shelf could end on "Picking songs… 1 of 1"). Progress is ignored
   once loading ended (found by the Finder-suite outage check).

Also tested, no change needed: Last.fm and ListenBrainz down (own artists still fill the shelf), slow lookups, cancellation
mid-build (ends in well under 1 s, cache unchanged, next build works), truncated `for-you-last.json`, `for-you-hidden.json`
and `listens.jsonl`, an empty library, no data folder, one song, a 50,000-song import.

**Offline mode:** no request leaves (checked through `OnlineAccessHandler`); the saved page stays. Found, not changed: the
catalog turns Offline mode's refusal into `CATALOG_TIMEOUT … did not finish its response in 25 seconds`. It is catalog-wide
(Search too), and that conversion is also what lets the catalog serve stale saved data offline, so changing it is out of
scope. With fix 1 the shelf never shows that text over existing picks; on a first-ever load in Offline mode it would.

### Performance
- **In-view reporting:** `ForYouShelfView` no longer subscribes to `LayoutUpdated`, which fires for every layout pass in the
  window. It reacts to the page's `ScrollChanged` (offset, extent, viewport), the list's `EffectiveViewportChanged`, a new page
  and opening the shelf, coalesced to one report per dispatcher pass. Headless test: 60 unrelated layout passes (a ticking
  clock) caused **60 reports before, 0 now**; one scroll, one resize and one new page cause 1 each.
- **Build cost** at the bounds (50,000 songs, 100,000 listens rows, a 50,000-song import, 120 pages, 3,000 actions, 5,000
  dismissed songs), in-memory catalog, three cold processes on this Linux container:

  | Step | Time |
  |---|---|
  | Reading `listens.jsonl` and `playlists.json` (74,721 plays kept of 100,000 rows) | 1.0–1.3 s |
  | Learning from 120 pages | 0.14–0.34 s |
  | Taste (seeds) | 0.48–0.72 s |
  | Heard set | 0.10–0.17 s |
  | Ranking alone | 18–53 ms |
  | **Whole local build** | **2.7–3.1 s**, about 300 MB allocated (short-lived) |

  An earlier warm run (same process as other tests) took 1.4 s for the whole build.
- **P7 verdict:** ranking alone **PASS** (18–53 ms, under 250 ms). The whole local part: **FAIL** against 2 s on a cold process
  (2.7–3.1 s; reading the 100,000-row log and the title normalisation dominate), about 1.4 s warm. It now runs off the UI thread (fix 5), so it delays the page,
  not the window. Not optimised further in C.
- A "Not for me" click with 5,000 songs already dismissed costs about 5 ms on the UI thread; each dismissal lookup about
  0.1 ms (linear in dismissed songs, capped at 5,000).

## 7. Milestone D: the model gate (done; documentation only)

The full gate is in [`discover-model-gate.md`](discover-model-gate.md). In short:
- **Verdict: no-go.** For you's candidates are songs the listener doesn't own and hasn't heard, and Noctis has no audio or
  compatible embedding for them:
  - the stream cache holds only played (heard) songs
  - the catalog takes no preview URLs by design
  - Apple's previews are for promotion only
  - full downloads are ruled out
- **Licences:** Essentia's Discogs-EffNet, the only candidate with a first-party ONNX export, has CC BY-NC-SA 4.0 weights
  and unreleased training data. The LAION-CLAP and MERT weight licences couldn't be confirmed from primary sources, and
  neither model has a supported ONNX export.
- **Gate G1–G7:**
  - G1 coverage ≥ 80 % of candidates
  - G2 licences
  - G3 pinned first-party ONNX with matching C# preprocessing
  - G4 Windows resource limits, paused while gaming or WordLyrics runs
  - G5 top-3 share −10 points with P1/P2 holding, on the replay and the personas
  - G6 two weeks of real outcomes no lower
  - G7 the shelf unchanged with the model missing or failing
- **If a model fails,** nothing ships, the gate isn't loosened, and it's re-run only when the failing condition changes.
- **Boundary:** one bounded multiplier on the per-song score in `Discover.BuildAsync`, after the safety filters and before
  `Distinct` and the caps. Features would be keyed by `DiscoverIdentity.Key` plus model hash and preprocessing version,
  and a missing feature means a factor of 1.0.
- **No code added in D.** An interface would be unused while G1 fails, and the existing tests already pin the no-model
  shelf.
- **Next lever: track-level retrieval** (gate section 6). Smallest option: Last.fm `track.getSimilar` for about 10
  best-liked recordings. It sends those song titles with the user's key. About $8–14 to build. Not started.

### Rough cost (Claude cloud credits, very uncertain)

| Milestone | Estimate | Main uncertainty |
|---|---|---|
| A | ~$12–18 used | A cold .NET install and two full local test runs. |
| B | ~$8–12 used | Two full local test runs again. |
| C | ~$15–22 used | Six reliability fixes instead of the expected one, and four full local Finder-suite runs. |
| D | ~$3–5 used | Primary-source reading through a restricted network; no code. |
| Song-to-song candidates (section 10) | ~$11–14 used | A third engine in the comparison, a place lookup for the top-3 metric, and full local runs. |

## 8. Acceptance criteria: verified vs pending

| # | Criterion | Verified here | Pending (owner, Windows app, isolated test profile) |
|---|---|---|---|
| 1 | Discovery improved without a model or paid inference | Synthetic comparison against the runnable 2.2.0 engine (section 6) | The owner's replay on the saved catalog; real listening over some weeks |
| 2 | Ownership, identity and playback safeguards intact | Unit tests; synthetic comparison: 0 duplicate, owned or heard cards | Real library with remasters and compilations |
| 3 | Learns from trustworthy evidence, not exposure, failures or imports | Unit tests (B); streamer persona | Real playback failures and streams |
| 4 | Song-level dismissal and Undo | Unit and view-model tests; 0 dismissed songs back in the comparison | — (owner checked the app's Play path; Not for me/Undo covered by the Finder suite) |
| 5 | Offline, cancellation, cache, queue semantics preserved | `DiscoverReliabilityTests` (C); Finder-suite outage check | Offline mode in the app; the catalog's misleading "CATALOG_TIMEOUT" wording in Offline mode (pre-existing, catalog-wide) |
| 6 | Automated checks for ranking, telemetry, identity, feedback | `DiscoverLearningTests`, `DiscoverTelemetryTests`, `DiscoverEvaluationTests`, `DiscoverReliabilityTests`, Finder suite | — |
| 7 | Baseline comparison and limits documented, no invented results | Criteria committed before results (`79396c1`); section 6 | The owner's replay numbers |
| 8 | Evidence-based model gate | `discover-model-gate.md`: no-go, with sources | Weight licences unverified here (Hugging Face blocked): LAION-CLAP, MERT |
| 9 | Verified vs pending kept apart | This table | — |

**Checked by the owner on Windows (61d10fe, isolated test profile):** shelf opens and Refresh builds in 20 s; the first 20
picks had 0 owned, 0 heard, 0 duplicate recordings and at most 3 per artist; one page with 12 cards in view and one Play
recorded once; the played pick only streamed; Finder suite 1557 ok, 0 wrong.

**Pending in the Windows app (section 10):**
- a live For you build with and without a Last.fm key: Deezer's radio answers as the fixtures assume (`/artist/{id}/radio`,
  a `data` list of track objects), and the shelf fills
- then the replay (section 3) on a copy made after that build, for the real top-3 share of `artists` → `now`
- the owner's approval of the changed Last.fm setting sentence

**UI checks pending in the Windows app (C changes):**
- the shelf keeps its picks on Refresh with the network off
- the in-view report while scrolling and resizing, with the new event wiring
- no hitch on Refresh with a large profile (the build now runs off the UI thread)
- the "Reset For you…" header menu (from A)

## 9. Continuation notes
- Entry points:
  - `ForYou.cs`: `Taste.Seeds`, `TasteSources` (`Load`, `Distinct`, `Familiar`, `Playlists`), `Discover.ForListenerAsync`,
    `Discover.BuildAsync`, `DiscoverHidden`, `ForYouSource.ResetLearning`
  - `DiscoverBaseline.cs` (2.2.0, frozen), `DiscoverEvaluation.cs` (`DiscoverPageMetrics`, `PlaceRecorder`, `RunAsync`),
    `DiscoverReplay.ReportAsync` (the owner's local report)
  - `DiscoverIdentity.cs`, `DiscoverFeedback.cs` (`Shown`, `Seen`, `Record`, `Retract`, `Learn`, `Outcome`, `Summary`),
    `DiscoverReplay.cs`
  - `ForYouShelfViewModel.cs`: `RecordShown`, `InView`, `Record`, `ResetLearning`
  - `ForYouShelfView.axaml.cs`: `InView`, `Reports` (what triggers a report: `ScrollChanged`, `EffectiveViewportChanged`,
    the list's items, `IsExpanded`, visibility)
- Tuning constants:
  - `Taste.DurableSeeds`
  - `Discover.Shelf`, `PerSeed`, `OwnArtists`
  - `DiscoverLearning.Rest` and `ArtistFactor`
  - `DiscoverFeedback.Keep`, `MaxBatches`, `MaxInteractions`, `FrontOfShelf`, `Slack`
- Song-to-song candidates (section 10): `Taste.Seeds(..., liked:)`, `IDiscoverCatalog.SongsLikeAsync`,
  `CatalogForDiscover.SongsLikeAsync`, `ArtistCatalog.RadioAsync` (`CatalogRecommend.cs`), `LastFmSimilar.SimilarTracksAsync`; in
  `Discover.BuildAsync` the song lists, the per-liked claim (`PerLiked`) and the turn-taking `Next`. `ForListenerAsync(songs: false)`
  and `DiscoverContext.Liked == null` build Milestone C's page (the comparison's "artists" engine). Constants: `Taste.LikedRecordings`,
  `Discover.PerLiked`, `CatalogForDiscover.LastFmSongLookups`. Tests: `DiscoverSongSourceTests`.
- Model gate (D): `discover-model-gate.md`. The plug-in point for a future model is the per-song score in `Discover.BuildAsync`.
  Nothing is wired; re-open only if gate condition G1 (audio or embeddings for remote candidates) changes.
- Known gap kept: plays are recorded only while True Shuffle is enabled (`PlayTracker` lives in `TrueShuffleService`). Moving it
  would change True Shuffle's lifecycle, so it needs the owner's approval.
- Local runs on Linux:
  - Build: `dotnet build tests/Noctis.Tests/Noctis.Tests.csproj -c Release`
  - Discover tests: `dotnet tests/Noctis.Tests/bin/Release/net10.0/Noctis.Tests.dll -class Noctis.Tests.DiscoverLearningTests -class Noctis.Tests.DiscoverTelemetryTests -class Noctis.Tests.DiscoverEvaluationTests -class Noctis.Tests.DiscoverReliabilityTests -class Noctis.Tests.DiscoverSongSourceTests`
  - The comparison table: set `DISCOVER_EVAL_REPORT` to a file path and run `-class Noctis.Tests.DiscoverEvaluationTests`.
  - Finder suite: `dotnet run --project tests/FreeMusicFinder.Tests -c Release`. On Linux, 7 checks fail on `native` too
    (Windows file locking and `C:\` paths). "after the restart the song finishes and is copied" failed once in four runs
    (a download-restart timing check, unrelated to Discover).
  - Full `Noctis.Tests` on Linux: 49 tests fail on `native` too (Windows paths, the updater, the Windows player, a socket
    broken-pipe); CI on Windows is the reference.

## 10. Song-to-song candidates (after D)

The owner's next step after D, key-free first: most users have no Last.fm key.

### Source chosen: Deezer artist radio (`GET /artist/{id}/radio`)
- **Why not ListenBrainz Labs `similar-recordings`?**
  - The official ListenBrainz API reference (`docs/users/api/index.rst` in metabrainz/listenbrainz-server) doesn't list
    Labs at all, so it has no documented contract.
  - The query reads a table named `recording_dev`, and its algorithm names are discovered at run time.
  - It needs a recording MBID per seed, which Noctis doesn't store: one more MusicBrainz lookup each, at 1 per second.
- **Why Deezer radio?**
  - It's on the host the catalog already uses, with no key and no account.
  - It sends only a Deezer artist ID. The ID comes from the identity lookup the build already does for the listener's
    artists.
  - It answers for any Deezer artist, so coverage is the catalog's own.
  - The cost is that it's seeded by a liked recording's **artist**, not the recording itself. Deezer's public API has no
    song-seeded similarity.
  - Deezer's documentation pages couldn't be read from this environment (developers.deezer.com is blocked). The response
    is taken to be the same track list as `/artist/{id}/top`. That is an assumption, to be checked live on Windows.
- **With the user's own Last.fm key, also `track.getSimilar`** for the same recordings.
  - Its songs are found among their artists' catalog songs (the existing, cached `SongsByArtistNameAsync`), so Deezer still
    gets only artist names.
  - A song not in that list is left out, not guessed. That also limits it to each artist's top 25.
  - At most 6 artist lookups per liked recording (`LastFmSongLookups`).
- **The setting sentence** "Only artist names are sent to Last.fm" now reads "Only artist names and the titles of up to 10
  songs you like are sent to Last.fm". Flagged in the PR for the owner's approval. No other text or UI changed: song cards
  reuse "Because you play …" and "More from …".

### What changed
- **Seeds:** `Taste.Seeds(..., liked:)` returns the 10 recordings with the most of the same evidence Taste already weighs:
  recent and lasting plays, favorites, ratings and the listener's own playlists. Never imports or merely owning a file.
- **Same filters as every card:** dismissed, credited, heard (the liked recording itself included), rested, owned or
  queued, one recording once.
  - Each liked recording keeps at most `PerLiked` = 3 songs that no other liked recording took. Two liked songs by one
    artist share a radio, so they get different songs from it.
  - Then the existing per-artist (3), per-seed (6) and own-artist (8) caps.
- **Spread on the page:**
  - The two sources take turns.
  - A seed never comes twice in a row while another is available.
  - A seed's turn comes round in proportion to the square root of its weight, so a weak old interest can't fill the top.
    This was the owner's live test: the first 6 cards from 5 Pink Floyd plays on a mostly hip-hop profile.
- **Networking:**
  - Radio goes through the catalog's existing client: proxy, Offline mode, 3 concurrent requests, Deezer quota retries,
    the 25 s timeout and the cache. It's kept for 3 days and served stale when Deezer can't be reached.
  - Last.fm uses the existing client's pacing (350 ms), key handling, error codes and persisted cache (14 days).
  - Each liked recording's lookup is one more slot in the build's six-at-a-time lookups.
  - A failing source fails alone (the existing "Some artists could not be loaded" notice). Cancellation and Offline mode
    stop the build as before.
- **Requests per cold build:**
  - Deezer: up to 10 radio requests, one per liked artist. Identity lookups are usually already cached by the artist path.
  - With a key: up to 10 Last.fm requests, plus up to 60 artist lookups (two Deezer requests each, cached 7 days and 1 day).
  - Warm builds send almost nothing.

### Metric note
- Top-3 share (M7) is now taken over the cards whose place in their artist's list is known (`Placed`). Song-source cards
  come from no artist's list, so the evaluation looks their place up; one not in the list counts as below it.
- For the 2.2.0 and C engines every card is placed, so their numbers are unchanged.

### Synthetic results (`DiscoverEvaluationTests`; criterion T1: top-3 share at least 10 points below the artist-only engine)
The radio is **modelled, not recorded**: 25 songs, a third by the artist and the rest by its first ten similar artists,
drawn with weight 1/n^0.7 over each artist's whole list. A second run uses a hits-only radio (each artist's first four songs)
to show how much the result depends on that.

| Persona | Top-3 share, artist-only (C) → now | T1 | Hits-only radio: now | P1–P4 |
|---|---|---|---|---|
| steady | 48 % → 28 % | PASS (−20) | 58 % (FAIL) | pass |
| streamer | 52 % → 33 % | PASS (−19) | 55 % (FAIL) | pass |
| collector | 57 % → 50 % | FAIL (−7) | 65 % (FAIL) | pass |
| tiny | 62 % → 60 % | FAIL (−2) | 65 % (FAIL) | pass (too few seeds for the caps) |

What it means:
- With a radio that reaches past the hits, the famous-songs lean falls by 20 or more points for listeners with history.
- The collector has few plays, and their liked songs are mostly owned artists. Tiny has one liked recording, so at most 3
  song cards.
- If Deezer's radio is mostly hits, songs from it make the lean slightly **worse**. The synthetic numbers can't settle
  this; one live build and then the replay on real data can.
- P1–P4 held on every persona and both radio models: 0 duplicate, owned, heard or dismissed cards; at most 3 per artist
  and 6 per seed.
- Distinct artists went up: steady 12 → 16, streamer 13 → 16, collector 14 → 16, tiny 9 → 10.
- Second-page repeats, artist-only → now:
  - steady 15 → 20 %
  - streamer 20 → 10 %
  - collector 30 → 40 % (P3 fails: limit 28 %)
  - tiny 25 → 10 %
- The collector's song cards come from few seeds whose other songs are owned. With the seeds taking turns, a seed with few
  songs left shows a passed-over one again. That's reported, not tuned (S3).
- One real fix came out of this run: each liked recording first claimed its songs in the source's order, so a song passed
  over before was claimed again unchanged. Claims now go by score, which carries that rest, as everywhere else.
