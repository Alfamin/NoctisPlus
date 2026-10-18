# Native polish checkpoint, 2026-10-18 night

Local-only changes after 927c79f6. No installer, release, push or version bump.

- Missing lyrics now has one **Time words** action. It submits the current complete local song to WordLyrics. An absent backend opens the native setup panel and retains the request; setup/model downloads still require explicit consent. Existing lyrics have a **Word timing** panel entry.
- The real queue drawer and Queue view expose **For you**, using the existing native shuffle service/menu. Its active state lights the player shuffle button and checks the chosen mode.
- Shortened 41 long statically referenced tooltips. New/edited resource values are English in every resource file, per owner instruction.
- WordLyrics progress detail is one line; errors use the native info icon. Setup consent states the approximate model size and optional Python/packages.
- Progressive transfers expose an incomplete-download predicate separate from reader availability, so a retained finished reader does not prevent timing.
- CachedImageRecycleTests now drains UI work before deciding an image is ready and requires two stable turns. A regression test covers the premature readiness case; production image caching was not changed.
- Settings has developer-only creation/attachment/measurement timing. The deferred-list experiment was removed because the actual first-open measurement did not improve.

## Verification

Final FastGates run: `codex-night-final-copy`.

- Full Noctis suite: **6,587 total, 0 failed, 43 skipped**, in 10 processes.
- Finder: **1,207 ok, 0 wrong**.
- Release win-x64 self-contained ReadyToRun publish succeeded. Build, suite, Finder and publish took about 105 seconds together.
- `git diff --check` clean.
- Launcher: owner independently confirmed it opens from their shell. Clean launch and already-open activation also checked; early native exit/missing executable covered by subprocess tests. The original silent failure's cause remains unproven.
- Actual app on a new synthetic, muted profile: one Time words button, no Search Lyrics or duplicate action; clicking submitted one generated WAV and opened the missing-setup panel without creating a backend/setup download. No real audio or Telegram data used.
- Actual queue drawer: For you selected, checkmark shown, player shuffle lit, current song kept. One synthetic three-song update logged 27.37 ms; this is not a library-wide benchmark.
- Final consent-copy/error-icon changes passed the full suite/publish after the targeted live check; their final pixels were not separately recaptured.
- Computer-use kernel was reset and own synthetic preview closed.

## Remaining limits

- First Settings opening still stalls briefly: baseline create 20.3 ms, attach 153.4 ms, measure 50.1 ms; UI.Stall 218 ms. The removed candidate measured 20.2/159.9/50.2 ms and UI.Stall 262 ms. Warm reopen produced no new stall. No speedup claimed.
- No AI alignment/model run or lyric-accuracy benchmark this turn. No claim that every lyric can be found or timed.
- No new live bot/authentication check. Phone-field real-click focus and the owner-reported lyric crescent still need relevant owner evidence; no reason to touch a login for those checks.
- Owner review of the preceding local Claude changes remains pending; approval of the launcher alone is not approval to push all changes.

The handoff FastGates/ShardedSuite tools outside this repository were fixed to return failure and skip publishing on failed checks. Their successful counts above were inspected, rather than inferred from a success banner.

## Test app replacement

`scripts/Update-NoctisPlus-Test.ps1` copies a complete publish first, verifies executable/assembly hashes, refuses a running test app, then swaps only app directories. It retains the previous app for rollback and never copies a profile. It has no recursive delete. The actual closed-app replacement and fresh-profile startup are recorded in the external handoff.
