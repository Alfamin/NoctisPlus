# Discover / For you: plan and continuation notes

Branch `discover-a`, started from `native` at v2.2.0 (`11995e63`). Milestone A is the only milestone done so far.
Don't start B until the owner says so.

> **The brief was cut off.** The copy this work started from stops in section 5 ("…should not be ad"). The definitions
> of Milestones A–D and the section 10 docs never arrived. Milestone A below was scoped from sections 1–4 and the
> parts of 5 that did arrive. The B–D plans further down are proposals, not the owner's definitions. Check them against
> the full brief before starting B.

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

**CI (Windows, `tests/Noctis.Tests/DiscoverLearningTests.cs`, synthetic only).** Covers:
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

## 4. Known gaps (deliberately not in A)
- **Plays are recorded only while True Shuffle is enabled.** `PlayTracker` lives in `TrueShuffleService`. Moving it out would
  change True Shuffle's lifecycle; left for B or C with the owner's agreement.
- **Exposure is shelf-level, not viewport-level.** Only the first 10 positions count toward resting; cards further right may
  never have been scrolled into view.
- **No track-level retrieval yet.** Candidates are still "top songs of related artists".
- **Not checked in the real app:** the Reset menu item, and the expanded/collapsed exposure rule. Both are covered only by
  view-model logic; no UI test was run.

## 5. Proposed B–D (to check against the full brief)

**B. Track-level candidates**
- Last.fm `track.getSimilar` and ListenBrainz/LB-Labs similar recordings, seeded from recently loved recordings rather than
  artists, mixed with the artist path under a per-source quota.
- Recorded fixtures for every provider response, with no live calls in tests.
- Read the provider docs (section 10) only for the exact endpoints, parameters and rate limits.
- Extend the replay to a song-level offline metric using cached catalog pages.

**C. Feedback quality and UI**
- Viewport-accurate exposure (the cards actually scrolled into view).
- Show "why" more concretely.
- Settings entry for reset and retention.
- Move `PlayTracker` so listening is recorded without True Shuffle, if approved.
- Headless UI tests for the menu and exposure rules.

**D. Neural-network readiness, no model shipped**
- A `IRecordingFeatures` seam (an embedding per recording key, a version, a source), stored separately from behaviour data.
- An offline evaluation harness that can compare "taste + embedding similarity" against the current ranker on replayed data.
- A short design note on candidate local models and their licence, size and CPU cost.

### Rough cost (Claude cloud credits, very uncertain)

| Milestone | Estimate | Main uncertainty |
|---|---|---|
| A (this) | ~$12–18 used | Included a cold .NET install and two full local test runs. |
| B | $15–25 | Provider fixtures and the docs check; quota mixing and the replay extension. |
| C | $8–15 | Headless Avalonia UI tests are slow to iterate. |
| D | $12–25 | Scope of the evaluation harness; no model training or inference included. |

Total B–D is about $35–65, which may not fit what's left of the $70. B is the one most likely to improve picks.

## 6. Continuation notes
- Entry points:
  - `ForYou.cs`: `Taste.Seeds`, `TasteSources`, `Discover.BuildAsync`, `DiscoverHidden`, `ForYouSource.ResetLearning`
  - `DiscoverIdentity.cs`, `DiscoverFeedback.cs`, `DiscoverReplay.cs`
  - `ForYouShelfViewModel.cs`: `RecordShown`, `Record`, `ResetLearning`
- Tuning constants:
  - `Taste.DurableSeeds`
  - `Discover.Shelf`, `PerSeed`, `OwnArtists`
  - `DiscoverLearning.Rest` and `ArtistFactor`
  - `DiscoverFeedback.Keep`, `MaxBatches`, `MaxInteractions`, `FrontOfShelf`
- Local runs on Linux:
  - Build: `dotnet build tests/Noctis.Tests/Noctis.Tests.csproj -c Release`
  - New tests: `dotnet tests/Noctis.Tests/bin/Release/net10.0/Noctis.Tests.dll -class Noctis.Tests.DiscoverLearningTests`
  - Finder suite: `dotnet run --project tests/FreeMusicFinder.Tests -c Release`. On Linux, 7 checks fail on `native` too
    (Windows file locking and `C:\` paths).
