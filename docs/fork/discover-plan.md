# Discover / For you: plan and continuation notes

Branch `discover-a`, started from `native` at v2.2.0 (`11995e63`), draft PR Alfamin/NoctisPlus#12 into `main`.
**Done:** Milestones A and B. **In progress:** C (criteria in section 5, written before measuring). Never merge, push to `main`/`native`, or tag.

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
> milestone; it is listed under "Later, only with evidence" in section 5.

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

### Chronological replay on your own data (local, offline, read-only)

Run it on a **copy** of your Noctis data folder, with Noctis closed. Nothing is sent anywhere and nothing is written; it
prints only counts and percentages.

```powershell
# PowerShell, from the repository root
$copy = "$env:TEMP\noctis-replay"
Remove-Item $copy -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $copy | Out-Null
$data = "$env:APPDATA\Noctis Plus"   # or your NOCTIS_PLUS_DATA_DIR
Copy-Item "$data\play_history.json","$data\library.json","$data\playlists.json" $copy -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$copy\plugin-data\dev.moshi.trueshuffle" -Force | Out-Null
Copy-Item "$data\plugin-data\dev.moshi.trueshuffle\listens.jsonl" "$copy\plugin-data\dev.moshi.trueshuffle\" -ErrorAction SilentlyContinue
$env:FMF_DISCOVER_REPLAY = $copy
dotnet run --project tests/FreeMusicFinder.Tests -c Release
Remove-Item Env:FMF_DISCOVER_REPLAY
```

The output has one line for `before` (the 2.2.0 seed step) and one for `now`, with these metrics:
- **recall:** share of artists you enjoyed in the next 14 days (2 or more good plays) that were seeds
- **returning favourites:** the same, but only for artists you loved months earlier and came back to
- **seeds skipped next:** seeds you only skipped in the following 14 days

Caveats:
- Without `library.json` the replay runs on plays alone (no favourites, ratings or ownership).
- Library marks and playlists are today's, so they are applied at every cutoff (a mild look-ahead).
- Catalog retrieval isn't replayed (no network), so this measures seeds, not songs.

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

### What leaves the computer (unchanged by A and B; listed for section 7)
- **Last.fm `artist.getSimilar`** (only with the user's own API key): a seed artist name.
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

## 6. Plan for C and D, and the gates after them

**C. Evaluation and reliability (next, when approved)**
- **Promotion and stopping criteria, written before measuring.** Proposed: the new engine is kept if, on synthetic personas
  and on the owner's local replay, it is no worse on recall and skip share and better on returning favourites. It is
  reverted if duplicate or familiar recommendations appear in synthetic checks. Any regression is fixed before new features.
- **Song-level offline comparison** of the 2.2.0 ranker against the current one on synthetic catalog fixtures:
  - duplicate rate, familiar/owned leakage, artist and seed concentration, repeated exposure
  - the outcome counts from `Summary` where feedback exists
- **Reliability:**
  - provider failures and partial failures
  - explicit Offline mode (verify that Discover respects it; fix it if not)
  - cached-page fallback and cancellation
  - stable visible batches while new picks arrive
- **Performance:** build latency and allocations on a 50k-song synthetic library, and the cost of `InView` on layout.
- **Kept explicit:** offline evaluation of biased historical logs cannot show causal satisfaction gains.

**D. Future-model readiness (documentation and boundaries only)**
- **The adoption gate (all must hold, each with evidence):**
  - a named model and exact pinned artifacts
  - compatible licences for code, weights and training data
  - a supported, auditable native Windows inference path (a real, built and validated ONNX export)
  - measured CPU, RAM, disk and optional GPU cost
  - a way to get audio or compatible embeddings for *remote* candidates, without downloading full songs to fill the shelf
  - enough candidate coverage
  - a separate evaluation showing a meaningful improvement over this deterministic baseline
- **Boundaries:**
  - any feature cache keyed by `DiscoverIdentity.Key` plus model and preprocessing version
  - optional and rebuildable; its removal or failure leaves Discover as it is
- No interface or storage is added until the gate is met.

**Later, only with evidence:** track-level retrieval (Last.fm `track.getSimilar` or ListenBrainz recording similarity). It
would need a docs check, recorded fixtures and a song-level offline comparison first.

### Rough cost (Claude cloud credits, very uncertain)

| Milestone | Estimate | Main uncertainty |
|---|---|---|
| A | ~$12–18 used | A cold .NET install and two full local test runs. |
| B | ~$8–12 used | Two full local test runs again. |
| C | $10–18 | Song-level comparison fixtures and the Offline-mode audit; a fix if Offline mode is not respected. |
| D | $3–6 | A written gate and a short boundary note; no code unless the gate demands it. |

## 7. Acceptance criteria: verified vs pending

| # | Criterion | Verified here | Pending (owner, Windows app, isolated test profile) |
|---|---|---|---|
| 1 | Discovery improved without a model or paid inference | Synthetic tests; replay on a synthetic persona | Real listening over some weeks |
| 2 | Ownership, identity and playback safeguards intact | Unit tests; Windows CI green on A | Real library with remasters and compilations |
| 3 | Learns from trustworthy evidence, not exposure, failures or imports | Unit tests (B) | Real playback failures and streams |
| 4 | Song-level dismissal and Undo | Unit and view-model tests | Clicking it in the app |
| 5 | Offline, cancellation, cache, queue semantics preserved | Existing suites pass; cached-page tests | Explicit Offline mode audit (C); app check |
| 6 | Automated checks for ranking, telemetry, identity, feedback | `DiscoverLearningTests`, `DiscoverTelemetryTests`, Finder suite | — |
| 7 | Baseline comparison and limits documented, no invented results | Replay tool and this file | The owner's own replay numbers |
| 8 | Evidence-based model gate | Draft in section 5 | Finalised in D |
| 9 | Verified vs pending kept apart | This table | — |

**UI checks pending in the Windows app:**
- the "Reset For you…" header menu
- exposure counts only while expanded
- the in-view report while scrolling
- that LayoutUpdated reporting causes no visible cost

## 8. Continuation notes
- Entry points:
  - `ForYou.cs`: `Taste.Seeds`, `TasteSources` (`Load`, `Distinct`, `Familiar`, `Playlists`), `Discover.BuildAsync`,
    `DiscoverHidden`, `ForYouSource.ResetLearning`
  - `DiscoverIdentity.cs`, `DiscoverFeedback.cs` (`Shown`, `Seen`, `Record`, `Retract`, `Learn`, `Outcome`, `Summary`),
    `DiscoverReplay.cs`
  - `ForYouShelfViewModel.cs`: `RecordShown`, `InView`, `Record`, `ResetLearning`
  - `ForYouShelfView.axaml.cs`: `InView`
- Tuning constants:
  - `Taste.DurableSeeds`
  - `Discover.Shelf`, `PerSeed`, `OwnArtists`
  - `DiscoverLearning.Rest` and `ArtistFactor`
  - `DiscoverFeedback.Keep`, `MaxBatches`, `MaxInteractions`, `FrontOfShelf`, `Slack`
- Known gap kept: plays are recorded only while True Shuffle is enabled (`PlayTracker` lives in `TrueShuffleService`). Moving it
  would change True Shuffle's lifecycle, so it needs the owner's approval.
- Local runs on Linux:
  - Build: `dotnet build tests/Noctis.Tests/Noctis.Tests.csproj -c Release`
  - Discover tests: `dotnet tests/Noctis.Tests/bin/Release/net10.0/Noctis.Tests.dll -class Noctis.Tests.DiscoverLearningTests -class Noctis.Tests.DiscoverTelemetryTests`
  - Finder suite: `dotnet run --project tests/FreeMusicFinder.Tests -c Release`. On Linux, 7 checks fail on `native` too
    (Windows file locking and `C:\` paths).
  - Full `Noctis.Tests` on Linux: 49 tests fail on `native` too (Windows paths, the updater, the Windows player, a socket
    broken-pipe); CI on Windows is the reference.
