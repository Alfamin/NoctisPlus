# Second cloud assignment — owner #261, 2026-11-05

## Start here

Continue implementation in Alfamin/NoctisPlus from `cloud/android-plus-261`. It contains your previous `claude/android-plus-260` result at168a1266 plus these instructions. Verify ancestry. Create a dedicated NEW task branch; commit/push only that branch. No main/native merge/push, force push, tag, release or store upload.

The owner has about US$9 remaining and wants as much useful cloud work as reasonably fits, including actual Android streaming. Do not stop at a plan or optional performance report. Prioritize coherent working changes and evidence, not quantity of features. Don't degrade correctness for cheaper output, don't burn unused credit deliberately. Do not ask routine questions; choose safe defaults and work on independent tasks when blocked.

Read `CLOUD-260-STATUS.md` and `HANDOFF-2026-11-05-CLOUD-260.md` once, then inspect relevant code. Previous `CLAUDE-CLOUD-260.md` still defines scope/owner constraints, but this file sets the new order. Historical paused-state handoffs do NOT prohibit this cloud assignment. Don't repeat the whole previous audit. Log work/new requirements in `docs/fork/CLOUD-261-STATUS.md` BEFORE edits.

## What local inspection confirmed

- Four prior cloud commits are present:322cf445 phone Finder wiring,38917dce M2/M7,8ffefe4d Telegram adapter,168a1266 docs.
- Streaming is NOT implemented. PhoneFinderHost enables FinderOnlineSearch.RequireCompleteFile; Media3 still has the ordinary file/content/http source stack. Play waits for a full transfer.
- Sync host/settings/durable job storage/sidecar integration remain absent. Real WTelegram adapter is unwired and only helper-tested.
- Your report documents an M2 convergence edge: fast writers editing in one server second can disagree with other readers depending on when they read other/own posts. This is correctness work, not something to label solved because unlikely.
- OnlineResultsViewModel says latest Play wins, but PlayAsync has default `[RelayCommand]`. Check toolkit concurrency semantics: with default non-concurrent async commands, the second tap may never enter PlayAsync while the first awaits, so its cancellation code cannot run. Prove behavior through the GENERATED command, not by invoking private logic alone.
- No Android compile/runtime proof exists. Prior Finder1753/7 was before the final Finder edits; its 7 Linux-dependent failures remain unresolved/classified, not a green suite. The broad Noctis suite result is missing from your report despite its reference to a section below. Report actual evidence, never infer it.

## Priority 0 — close concrete correctness gaps

1. Test the actual PlayCommand while A is waiting: B must cancel A's wait, B must be the only resulting playback; late A completion cannot switch back; repeated A taps must send at most one request. Set concurrency/cancellation semantics explicitly, maintain UI interaction while waiting, and preserve shared transfer/download ownership. Cover A->B->A, cancel/error/dispose. Don't cancel a user's explicit save when a playback wait is superseded.
2. Add an adversarial regression demonstrating the DOCUMENTED remaining M2 case, then fix the deterministic ordering/reconciliation. Cover two fast clocks, same server second, opposite delivery orders, own-post adoption, ambiguous duplicate replies, checkpoint restart and snapshot+tail. All devices must converge after the same accepted log. Preserve unposted edits and M7 early plays. Choose the smallest sound solution; explain it briefly. If protocol fields change, keep old data readable/migrate safely. No real automatic sync until this is sound.

## Priority 1 — real Android progressive playback

Build on existing shared ProgressiveTransfer/ProgressiveAudioBuffer. Use a portable, testable read abstraction plus a thin Media3 DataSource/Factory adapter; don't create a parallel download engine, local HTTP server, new paid dependency or proxy service. Match the PINNED AndroidX bindings/API. Preserve existing HTTP auth resolver for LAN streams and file/SAF playback.

End-to-end acceptance, subject to local runtime checks:
- Online Play starts using buffered bytes BEFORE the full transfer ends. Wire source selection into Media3 and phone host; merely adding an unused class or disabling RequireCompleteFile is not enough.
- Reads block OFF the UI thread when requested bytes have not arrived. Temporary underflow/network outage is NOT EOF. EOF only on genuine completion at the end; failure/cancellation surfaced deliberately. No spin loops, arbitrary whole-file RAM allocation or fixed percent that implies every format is playable.
- Closing/cancelling/replacing a source unblocks reads and releases its lease/events/streams safely. A superseded reader cannot tear down an explicit save/shared download. Keep current-song work ahead of prefetch.
- Pause/resume keeps position stable; a connectivity gap must buffer/retry without restarting a partial song, advancing the progress clock silently, losing the queue/player or looping the buffered prefix. Offline local music still works.
- Seeking within available bytes must work if the extractor supports it. Beyond buffered bytes either wait/reposition through an actually supported transport, or clearly restrict it; don't claim random-range fetching if Telegram transfer is sequential. Honor cancellation while waiting to seek/open. Extractors probing metadata near EOF must not deadlock or silently force all MP3/FLAC to full-download playback.
- Correct total length/unknown length handling, bounded read sizes, offsets, completion/failure transitions and retry/refetch of a failed transfer still leased by a reader.
- Cache-only Play stays out of Songs; explicit Download/Add persists/imports after a complete file. Completed cache replays immediately. An MP3 and FLAC must both be supported; any format-specific fallback must be explicit and documented.

Portable tests must cover real growing-file/buffer behavior (synthetic bytes): initial availability, blocked read->append, starvation->resume, exact EOF, failure, cancellation/Close from another thread, multiple readers, seek/probe and no leaked transfer lease. Add wiring tests where meaningful, not assertions mirroring your own constants. A compile/test fake cannot establish actual decoding/playback; leave a precise local checklist.

## Priority 2 — useful remaining work if budget permits

1. Fix observable mobile UI churn: FinderDownloadsViewModel.Refresh rebuilds every row/clears Entries on every progress notification; OnlineResultsViewModel.Show clears/recreates the list for incremental results. Reuse keyed row objects/update changed fields, coalesce frequent progress without delaying terminal/error/selection state, preserve scroll/selection and dispose cancelled query resources. Measure portable notification/allocation counts on a synthetic workload before/after; no unsupported FPS claims. Check startup FinderSession disk work; defer only if measured, preserve cancellation/lifetime.
2. SAF playlist matching: PhonePlaylistLibrary currently excludes content:// songs because PlaylistMatcher uses File.Exists. Inject a host accessibility/existence predicate or an equally small typed abstraction so already-owned SAF tracks match, preserving desktop missing-file filtering. Cover duplicate recording/version, granted/revoked SAF and private paths.
3. Then continue unfinished shared sync controller/durable jobs and opt-in host wiring per #260, or phone True Shuffle/native lyrics if the controller cannot be finished safely in the remaining budget. Complete one coherent tested slice rather than many empty settings/stubs. Sync remains OFF until an explicit choice and fully connected implementation; no upload-all startup.

## Tools, tests and budget discipline

Default one worker, no swarm. Bounded reads/rg, reuse existing tests, no repeated full scans. Use .NET10 and repository pins. Install needed portable dependencies once; --no-restore after restore. Run focused tests while iterating; once stable rerun TelegramSync, final Finder checks and relevant mobile/playback tests. Record exact totals/known Linux-only failures; do not weaken tests just to make Linux green.

Android SDK download was blocked by cloud network policy last time. Make at most one short availability check, then use an existing toolchain/cache if present; otherwise implement/test portable logic and inspect binding metadata without repeated SDK/emulator downloads. Mark Android compile/device checks pending. Do not claim streaming works on a phone until local Codex validates it.

Use actual platform credit telemetry if available. Don't infer dollars from context length. Reserve enough credit for commits, push and handoff; checkpoint after coherent slices. Stop only when cloud-suitable work is finished, real budget is near exhaustion, or no independent useful work remains. US$9 is a target, not a promise of finishing every item.

## Constraints and required delivery

Keep native minimal English mobile UI, desktop Plus lyrics/search/bot behavior and settings. No copied sessions, API secrets, production music, real Telegram requests/stress tests, new paid services or local WordLyrics model. Use synthetic files and injectable protocol seams. Preserve existing LAN protocol/auth scope. Never generate a release signing key or present debug APKs as official updates.

Deliver implementation commits on your dedicated task branch, pushed; exact base/final hashes and test commands/results. Update `CLOUD-261-STATUS.md` with implemented/automated-tested/not-live-verified/open/blocked. Write `HANDOFF-2026-11-05-CLOUD-261.md` with changed files, decisions, migrations, limitations and local-check order. Owner commands are PowerShell; cloud-internal commands use its actual shell.

Local Codex will do Windows full ten-shard gates/Finder, Android x64+ARM64 compile, isolated emulator screenshots, actual TEST Telegram/bot/channel checks, MP3+FLAC first-audio-before-completion measurement, network-drop/pause/resume/seek tests, and two-device sync. No need for cloud credentials. None of these local checks is established by cloud simulations.
