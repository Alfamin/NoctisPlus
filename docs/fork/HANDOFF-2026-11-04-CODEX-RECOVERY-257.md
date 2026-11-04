# Claude interruption recovery — owner #257

Read this instead of treating the old NIGHT-255 table as current. LOCAL ONLY: no push, tag, installer or release was requested.

## Location and state

- Main repository remains `C:\Users\Alfamine\noctis-fork` on `hotfix-271` / b30fc8dd. Do not overwrite it or other worktrees.
- **Continue in `C:\Users\Alfamine\nplus-codex-257`, branch `codex-recovery-257`.** It starts from android-plus-integrate 7e0e2d37 and merges the completed desktop and Android branches, retaining their history/attribution.
- Included: hotfix-271 + its regression test, fix-272-shuffle/offline, stage1-next/fixes/map/review/handoff, finder2, shuffle2/phoneshuffle, signin, report, hopfix/hoptest. Finder2b had no additional changes. Review branches remain available. No new subagents were launched.
- All pre-existing source worktrees/branches remain. `noctis-167-stock` still has its pre-existing uncommitted KaraokeWordText edit; it was NOT overwritten or used as the recovery source.

## New local commits

| Commit | Change |
|---|---|
| 56c2d7ee | Playback/refetch/shuffle use IsDownloading rather than mere registry membership; failed transfers held by an old VLC reader are fetched again. Offline prevents background asks/downloads. Regression covers actual failed-reader replay and offline queue requests. |
| 23d634cf | Sync log persists ledger/cursor/pending/exact in-flight batch together; retry after ambiguous acceptance keeps the same immutable seq/payload. Async operations serialized; recording during posting kept. Snapshot includes high seen seqs above old gaps. Channel guard rejects absent creators, checks recreated channel, matches gone errors to the correct channel, propagates cancellation. Bounded post/decompression, malformed-item filtering. Upload single flight/dedup/pacing/Persian caption limit/error; file-map stale/alias/null fixes; snapshot deletion uses covered UpTo, not snapshot post id. Downloads align partial offsets, return actual collision path, bound bad-size retries. |
| beb05d52 | Phone sign-in submits only a known code length (otherwise Continue), handles unsupported signup/email steps with an explanation/reset, blocks number change while busy, supports cancellation and ignores a late response. Phone shuffle policy tests added; duplicate recording identity and empty NeverPlayed results intentionally follow desktop rules. Plus report-link test follows selected app variant. Android release script chooses Plus package filename by default, -StockNoctis selects upstream. |
| 700953a5 | Shared posed KaraokeWordText keeps one raster transform through motion/rest and draws fallback in the failure frame. Replaced weak no-effect test with real Skia pixels. Folder rows prepared before visual attachment now defer Unfold until attached (actual existing UI bug found by full suite). Worker restart regression waits for its asynchronous source callback. |

These are NOT a new released version. Desktop version remains 2.7.1. Plus native lyrics templates/MotionWordPresenter/LetterLayer and #251 Reel work were not replaced. The shared posed-word fix is not a claim of exact native Reel parity and is not part of upstream PR #168 yet.

## Exact checks

- Combined full gates **codex-257b: 9,503 total, 0 failed, 83 skipped; Finder 1,760 correct / 0 wrong; publish exit 0**. Ten shards, including the separate motion sampling processes. 83 optional checks skip as before (real-render/performance opt-ins or unbuilt plugin release packages); do not call those installs verified.
- TelegramSync **56 passed, 0 failed**, with fake Telegram only. No real channel created, song uploaded, bot request, account login or session copy.
- Focused desktop/phone checks: 80/0 before final replay test; final replay is included in full gates. Sidebar 24/0 after the attachment fix.
- Real Skia: 25 total/0 failed/1 package skip initially; final focused raster/lift 3/0. Two new pose checks **both fail against Claude's old hoptest renderer**, then pass against the fix: motion->rest jumped 0.193 px on the old code; error frame was blank. Counterfactual logs kept.
- First full codex-257a was 9,502/3failed/83skipped. Worker restart and artwork passed isolated (80/81; only Sidebar still failed). Fixed the timer test race and actual Sidebar attachment bug; final full b is green. Do not claim the artwork production code was changed.
- Android final Debug x64 build: **0 errors**, existing warnings remain. This APK is **emulator x64**, not a distributable ARM-phone release.
- Real isolated emulator `NoctisPlusRecovery`, port5580: installed/launched Plus app; declined notification permission; selected ONLY `/sdcard/Music/NoctisRecovery` containing our synthetic silent WAV; Songs lists it; Media3 reports PLAYING with no error. Pause held position17476ms across2s; resume PLAYING at17479ms. Fresh Home and Songs screenshots inspected. No owner profile/session/music was used. This verifies the existing mobile shell/local playback, not unfinished Telegram screens or cross-device sync.
- Release PowerShell script syntax checked; signed Release/AAB and stable signing path NOT exercised.

## Builds

- Desktop TEST has been **staged, not launched**, from `C:\Users\Alfamine\nplus-codex-257\artifacts\plus\publish-codex-257b`. Its updater now points there. Existing TEST profile/settings/login preserved. Installed app was left alone (it was running). Real desktop failed-network/bot playback checks are still pending; do not mark owner #256 DONE from unit tests alone.
- TEST launcher: `powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\Alfamine\NoctisPlus-Test\Start-NoctisPlus-Test.ps1`.
- Emulator APK: `src\Noctis.Android\bin\Debug\net10.0-android\android-x64\com.alfamin.noctisplus-Signed.apk`. Original Claude APK under nplus-apk was kept.
- SDK: `C:\Users\Alfamine\dotnet-sdk\dotnet.exe`; JDK `C:\Users\Alfamine\android-tools\jdk-21.0.13.0.2+1`; Android SDK `C:\Users\Alfamine\android-tools\sdk`.
- Local gates copies under `artifacts\plus\tools` point at THIS worktree. Original FastGates/ShardedSuite hard-code noctis-fork: do not accidentally test the old checkout. Logs and timing XML are under this worktree's artifacts/plus.

## Next, highest priority first

1. Finish stage1 protocol blockers **before wiring real automatic uploads**. Original REVIEW-255-STAGE1.md still matters: receiver-relative future-stamp clamping can diverge when devices read at different times (M2); reused install device IDs can reset per-device play counts (M7). Choose a canonical time/identity strategy and add adversarial tests. A new install must get a fresh device ID and initial pull/snapshot before writing counters. Do not pretend these are fixed by the normal fake-clock tests.
2. Real WTelegram ITelegramDrive adapter + desktop opt-in wiring are NOT built. Bind guard/map/uploader/error states to the host; validate owning account, require explicit sync enable, use existing TEST account in place only. No real library auto-upload by default. Durable transfer-job storage and stable messages.sendMedia random_id across ambiguous upload retries/restarts remain for the adapter. Current upload single-flight/dedup is per instance, not a guarantee of crash-proof exactly-once media upload. Real multipart upload resume is also not implemented here.
3. Snapshot bootstrap after long-offline/pruned logs, final filename sanitization/hash checks, storage/SAF recovery UI, LAN+Telegram double-apply guard and deletion/missing-online handling remain from the plan. Do not call the entire sync stage DONE yet. Use snapshot.UpToMessageId for pruning.
4. Finder extraction remains mostly pending. OnlineMusicSearch is in Core; Downloader root has an override. Finish host injections (CatalogPages, playlist library, stream-cache paths, packed defaults, stand-in factory), then Noctis.Finder/FinderSession and mobile host. The phone does NOT currently have Music Finder search/download screens.
5. TelegramPaneViewModel is logic only; **no real sign-in service or native screens yet**. Service must report ExpectedCodeLength if known, honor cancellation and implement ResetLoginAsync without logging out an existing account. Keep manual Continue for unknown lengths. Wire phone shuffle, lyrics/online timing and cross-device sync afterward; no local WordLyrics model on phone.
6. Plus APK release signing needs the OWNER's stable keystore and GitHub secrets. Never commit private keys or distribute an ephemeral CI debug-key build as a normal update. No new signing key was created here. Existing release script still needs the caller's real SDK/JDK/tool configuration; only filename/variant and syntax were checked.
7. Desktop TEST live checks for interrupted-download replay/offline prefetch and new shared folder/pose behavior. Installed app/profile stays untouched; no focus stealing while gaming.

## Cleanup — owner #258

See CLEANUP-2026-11-04.md. About109GiB net recovered (95.20GiB free -> about204GiB). Older publish/build-output paths in old handoffs were intentionally removed; **source, git history, all branches and uncommitted work were kept**. Rebuild old binaries if needed. Current recovery outputs, latest prior publish167d, current TEST/profile and recent rollback, original APK and Android SDK/models remain.

## Standing rules

Log owner requests in the external LEAN-PLAN before work; DONE requires the relevant live check. D:\music read-only. Installed profile/session untouched. Never copy Telegram sessions. TEST profile stays in place. Stage named files only; no git add -A, force push, main merge, tag or release without owner go. PowerShell commands for owner. Native minimal UI, no new paid services/API keys/model downloads. No computer use/download/install while gaming. End computer-use helpers promptly. No new agents unless owner asks. No release implied by this recovery request.

## Final checkpoint update
- Final Android APK was reinstalled after the shared folder fix and launches (Status:ok). It asked for notification permission again after the earlier decline; saved android-recovery-final.xml confirms this. Earlier synthetic playback/pause/resume was checked before that final shared-control rebuild. Do not claim a second complete playback pass on the final APK. Treat notification re-prompting as a mobile UX follow-up, not a fixed feature.
- Isolated emulator was shut down via adb emu kill; no NoctisPlusRecovery process remains. No Windows computer-use session was opened for this recovery. TEST remains staged and closed; installed Noctis was left running.
- Docs checkpoint d53572d1 follows the four fix commits above. Check git log for the final documentation-only follow-up hash. Source worktree is clean.
