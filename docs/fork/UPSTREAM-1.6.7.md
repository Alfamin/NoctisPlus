# Noctis 1.6.7 integration — owner #252

Integration branch: merge-167 from native e0f2a040. Upstream tag v1.6.7 includes v1.6.6.
LOCAL ONLY. Plus version stays 2.6.1; this is not a release. Base credit/API compatibility becomes 1.6.7.

## Merge policy
Plus behavior wins unless an upstream change is demonstrably compatible and better.
This includes clean merges, not merely conflict markers. No new stock lyric renderer on desktop.
783 upstream changed paths (~100k added lines),408 source paths,80 overlap with Plus,47 initial conflicts.

## Decisions and interaction checks
| Area | Decision |
|---|---|
| Desktop lyric words | Keep MotionWordCell/MotionWordPresenter/LetterLayer, fonts, native RTL wrapping, Plus sweep/converters and timing clock. No KaraokeWordText painter takeover. |
| Line motion | Move the unchanged Plus650ms curve to PlusLineMotion/PlusLineMotionEase; desktop page/panel/Reel explicitly reference it. Shared/mobile LineMotion can evolve independently. Keep active-line scrolling, no upstream early-scroll/IsSweepFocused redesign. |
| Mini lyrics | Retain complete Plus template and native code hooks; selective frost-edge fix from upstream only. Native shuffle shortcut retained. |
| Long lyric documents | Adopt word-layer window for >2000 words: existing native word template near active lines; shared resting-text control far away. Layout comparison tests at900/1280px must pass. RTL documents retain existing full native layers until parity is proven. Resting control's zero-size font measurer is NOT an active word renderer. |
| Reel | Preserve #243 styles and #251 pixel-snap fix. EXACT native Reel parity remains OPEN; migration does not mark it done. |
| Music videos | Add upstream full-size/behind-lyrics placement and audio/video switch without replacing lyric templates or Plus Sync pill. Keep buffering/seek/native bottom controls. |
| Streaming queue | Keep pending stream entries, cache refetch, latest-click and native shuffle logic. Upstream missing-local-file detection must bypass pending/refetchable streams in playback and queue marks. Regression added. |
| Playlist search | Use complete playlist as playback context during search while keeping online missing-song rows and their order. Remove during search remains effective. |
| Plugins | Adopt submission checker/form/guide, trust verification, grid, hosted views, APIs1.3–1.6, sidebar entries. Keep native Features separate from stock optional-feature switches; preserve action settings and source-free notices. |
| Duplicate effects | Official stock Lyric Motion/True Shuffle have different IDs: suppress their catalog/load/install paths while the Plus equivalent is built in. Built-in identity/settings/data directories unchanged. Regression added. |
| Last.fm | Keep Plus generation-aware verification and retain sessions on network/VPN failure. Reject upstream catch-all logout regression. |
| Profile/startup/update | Plus product/profile/mutex/pipe/feed/version retained. Preserve import-aware warmup; combine output-device, MP3 fallback and UI-scale initialization. Submission form gets base Noctis version, not Plus version. |
| Audio | Keep progressive duration and seek safeguards; combine headerless-VBR frame duration and decoder fallback fixes. Preserve buffering/offline recovery and native queue priority. |
| Lyrics sources | Preserve Plus apostrophe/diacritic folding and title fallback. Move existing featured-credit stripping to Core after upstream moved selector there. Keep new wrong-song/instrumental validation. |
| No-lyrics mark | Integrate the new instrumental mark with Plus online and automatic WordLyrics paths. Explicit single-song timing overrides the mark; validate persistence. |
| Library/Home/lists | Keep welcome/For you/search/import metadata. Add new-library shelf/row controls, configurable columns, ReplayGain fields/backfill, album carousel, queue customization, play-count import and reset fixes. |
| Memory | Keep upstream nonblocking collection during audio playback; aggressive compaction only while audio is not playing. |
| Installer | Preserve Plus identity/paths; add uninstall cleanup for generated VLC cache only. No installer built/released. |
| Resources/tests | Three-way resource-key union, Plus edits win; no new translations authored. Adapt upstream assertions to existing Plus branding/defaults/routes, retain new trust/parser/queue checks. |

## Verification checkpoint
First full codex-167a:9476tests,12failed,83skipped; Finder1760/0; published but NOT a validated TEST candidate.
Failures included real clean-merge renderer, search-play context, plugin trust and video-placement problems; corrected.
Focused2:143tests,1failed(video placement). Focused3 after fix:71tests,0failed (native motion, lyric layout, streaming queue, builtins, video placements).
Full codex-167b:9480tests/4failed/83skipped; Finder1760/0; publish0. All4 were mini repeat mode labels; connected the6 tooltips to RepeatModeTip.
Full codex-167c:9483tests/4failed/83skipped; Finder1760/0; publish0. Different failures exposed test isolation/timing issues:3 blur assertions share the global Settings radius;1 search focus-ring assertion sampled an unfinished transition. Backdrop tests now run non-parallel with a known default; search waits for the end state. No app behavior changed for these failures.
Final codex-167d:9483total/0failed/83skipped,Finder1760/0,publish0. TEST updated to publish-codex-167d; existing profile kept, PID24644. Live Home and Songs pages opened with260songs and existing player restored paused. Attempt to play Family Business was blocked by detected user input. Owner then reported3% remaining; computer-use kernel immediately RESET. Native lyrics playback, shuffle, Settings/Plugins/About live checks remain OPEN. Migration is BUILT, NOT DONE; do not promote native until those checks pass.
Stock plugin release-package tests skip unless their local Release builds exist; no stock plugin installed in user profile.

## Files for detailed review
artifacts/plus/merge-167-overlap-paths.txt (80 overlapping source paths)
artifacts/plus/review-167-{lyrics-vm,player,timeline,mini-code}.diff
artifacts/plus/merge-167-build*.log and codex-167*-{suite,finder,publish}.log

No installed profile/session writes, no Telegram-session copying, no WordLyrics models or song downloads, no D:\music writes. No push/tag/release.