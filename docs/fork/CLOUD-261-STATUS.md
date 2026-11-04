# Cloud #261 status (Claude cloud, Linux, no device/account)

Base: `cloud/android-plus-261` @ 63b97047 (contains 168a1266; ancestry verified). Task branch: `claude/android-plus-261`.
Legend: IMPL / AUTO (cloud tests passed) / NOT-LIVE / OPEN / BLOCKED. Nothing here is live-verified.

| # | Task | State |
|---|---|---|
| P0.1 | Online Play: latest tap wins through the generated PlayCommand; A->B->A, cancel/error/dispose | OPEN |
| P0.2 | M2 remaining edge (fast writer reads another post before its own read-back) | OPEN |
| P1 | Android progressive playback: portable blocking reader + Media3 DataSource, wired | OPEN |
| P2.1 | Mobile list churn (downloads/online rows) | OPEN |
| P2.2 | SAF playlist matching | OPEN |
| P2.3 | Sync controller / shuffle / lyrics | OPEN |

## New requirements logged
- (#261) Prove Play semantics through the generated command, not private logic.
- (#261) Report the broad Noctis.Tests result actually measured.
