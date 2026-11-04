# Handoff 2026-11-04 night: #255 Android Plus branches

Base: `android-plus-integrate` = 7e0e2d37 (all stage 1 work merged up to `android-plus-stage1` efcd8803).
Test numbers come from the commit messages and branch docs.

| Branch | Head | What it holds | Tests | In android-plus-integrate |
|---|---|---|---|---|
| android-plus | b74bb7db | Plan, performance budgets, edge cases, stage 1 cloud prompt | docs | yes |
| android-plus-app | 5d93f6d8 | Notes for the Plus phone app variant and its CI run (ANDROID-APP-VARIANT-255.md) | docs | yes |
| android-plus-finderplan | f612872e | Finder move map for shared Noctis.Finder library (FINDER-MOVE-MAP-255.md) | docs | yes |
| android-plus-finder1 | 3530322d | Finder step 1 moved + notes (FINDER-STEP1-255.md) | see doc | yes |
| android-plus-finder2 | 63b7303d | Finder step 2 slice: Downloader default root injectable by host | - | no |
| android-plus-lyrics | fc07a08c | Phone Lyric Motion plan, stage 4 (LYRIC-MOTION-PHONE-255.md) | docs | yes |
| android-plus-phoneui | 2d1ab225 | Phone UI design: Telegram sign-in, Finder, sync, notices (PHONE-UI-255.md) | docs | yes |
| android-plus-shuffle | 9385c937 | True Shuffle engine moved into Noctis.Core | - | yes |
| android-plus-shuffle2 | 9c0f6cb5 | ShuffleScope + RollingQueue moved into Noctis.Core (shuffle step 1) | - | no |
| android-plus-stage1 | efcd8803 | Stage 1 TelegramSync + download flood-wait test + HANDOFF-255-STAGE1.md | 23/23 | yes |
| android-plus-stage1-ci | d0bd3d1d | Linux CI for Telegram sync tests | CI | no (in stage1-next) |
| android-plus-stage1-guards | f0004e9b | Sync guards: creator-account check, deleted/left channel recreate once, signed-out stop | 27/27 | no |
| android-plus-stage1-map | f87053f2 | Persisted documentId <-> track map (atomic JSON, link by size + recording key), snapshot cadence | 27/27 | no |
| android-plus-stage1-upload | 7ed37f72 | Paced upload queue (one at a time, JSON caption, flood wait, cancel keeps job) + forward pacer 1 per 2 s | 28/28 | no |
| android-plus-stage1-next | c4d1b785 | Merge line for stage 1 follow-ups (includes stage1-ci) | - | no |
| android-plus-stage1-review | b9e51a75 | REVIEW-255-STAGE1.md: TelegramSync findings ranked by severity | docs | no |
| android-plus-stage1-handoff | d6ec53dd | Stage 1 handoff update: parallel branches, what is left | docs | no |
| hotfix-271-test | bb57b567 | #271 regression test: a song still downloading is not missing | test | no (not in main either) |
| investigate-256-offline | a35b2319 | Investigation notes: offline For you Play flooding downloads | docs | no |

## Release state

- Release 2.7.1 (b30fc8dd, tag v2.7.1) fixed #256: streamed songs were skipped as missing.
- The regression test for it is on `hotfix-271-test` (bb57b567) and is NOT merged to main yet.

## Next steps in order

1. Merge stage1-next and the fix branches (stage1-guards, stage1-map, stage1-upload, stage1-review, stage1-handoff) into the stage 1 line, rerun the suite, then into android-plus-integrate.
2. Fix the REVIEW-255-STAGE1.md findings, highest severity first.
3. Wire the guard, the documentId map and the uploader into TgSyncLog.
4. Desktop wiring (stage 2).
5. Finder step 3+ (after merging finder2).
6. Phone shuffle service (after merging shuffle2).
7. Plus APK signing keystore: owner creates/holds it.
8. Local emulator run (Android SDK at C:\Users\Alfamine\android-tools).
9. Merge hotfix-271-test into main.
