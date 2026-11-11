# Merge of upstream Noctis 1.6.9 (owner #293, 2026-11-11)

Branch `merge-169`, from `android-merge-169` (44f3fbb3: main 2.7.3 + the Android work through #283) with `plus-286`
(#286, #292) merged in first, then `v1.6.9` (7337d337; 348 upstream commits since b2d418c2, i.e. 1.6.8 + 1.6.9).
The phone code of 1.6.9 was already in via the beta 3 picks (#289), so every phone block had 1.6.9's version already.

Rules followed (memory `merge-rules-protect-plus.md`): own branch; each conflict hunk decided and logged below; no whole-file
ours/theirs on a Plus file; every removed Plus line read (section at the end); no Plus test edited to fit; full suite and a
live check before it is called done.

Tools: `nplus-diag/tools/dump_blocks.py` (diff3 view), `resolve.py` (per block), `resx_merge.py` (per key),
`dup_added.py` (blocks the merge brought in twice), `plus_removed.py`.

## Conflict decisions (38 files)

| File | Block | Decision |
|---|---|---|
| installer.iss | 1 | ours: Noctis Plus name, version define, publisher |
| src/Noctis/Noctis.csproj | 1, 2 | ours: Plus product, company, version 2.7.3 |
| src/Noctis.Core/Models/AppSettings.cs | 1 | theirs: same phone settings as ours (only a blank line differed) |
| src/Noctis/Program.cs | 1 | both: Plus's "not while a profile import is pending" `if` opens first, upstream's multichannel launch read inside it (it reads the profile) |
| src/Noctis/Services/VlcAudioPlayer.cs | 1 | both: upstream's new `EngineBeginSegment(..., onAvcodec, SourceChannelsOf(...))` + Plus's download underrun refill |
| src/Noctis/ViewModels/LyricsViewModel.cs | 1 | both: Plus's in-place timing take (#285) kept; upstream's `MaybeUpgradePlainLyrics()` added in the branch that applies new lyrics |
| src/Noctis/Views/LyricsView.axaml.cs | 1 | both: upstream's `else if` (backdrop behind sidebar, #184) stays on the flowing `if` chain; Plus's buffered-timeline `if` after it; upstream's `ApplyBackdropBleed` method |
| src/Noctis/Helpers/LibraryRemovalHelper.cs | 1, 2 | theirs: same list as ours plus `.lyricsfile` |
| src/Noctis/ViewModels/MainWindowViewModel.cs | 1 | both: Plus Search page wiring + upstream's lyrics-search mode hook |
| | 2 | both: upstream's `SwitchToVersion` command, then Plus's `OnPlaylistOpened` + `OpenFoundPlaylist` (#69) |
| | 3 | ours: Home's search gesture opens the Plus Search page (upstream's hidden-Songs tweak is for its Songs jump, which Plus does not use) |
| src/Noctis/ViewModels/PlayerViewModel.cs | 1 | both: Plus `DropShuffleCopies` + upstream `MirrorIntoRepeatCycle` before Play Next's insert |
| | 2 | both: Plus's shuffle-aware Add to Queue (insert at the user-queue index) now mirrors into the repeat cycle at that index; the plain path is upstream's |
| | 3 | both: upstream's Repeat All replay of a cleared song (#175/#188) first, then Plus's #95 no-double-History condition |
| src/Noctis/Views/LibrarySongsView.axaml | 1 | both: columns `Auto,Auto,Auto,Auto,*` (Plus check mark in column 3, upstream's star column moves to 4) + upstream's `with-snippet` class |
| | 2 | both: Plus #93 check mark, then upstream's lyric snippet spanning columns 1-4 |
| src/Noctis/Views/LibrarySongsView.axaml.cs | 1, 2 | both: `TitleCellChildren(..., LyricSnippet, CheckMark)`; both found in the cell |
| src/Noctis/Views/PlaybackBarView.axaml | 1 | both: Plus's always-frosted island glass + upstream's `x:Name="IslandGlass"` (its contrast reads it) |
| | 2 | both: Plus #166 Don't recommend + upstream's Other Versions menu items |
| src/Noctis/Views/PlaybackBarView.axaml.cs | 1, 2, 5 | both (independent lines) |
| | 3 | both: every property from each side re-measures the island |
| | 4 | both: Plus #90 squeezable extras + the Add button, plus upstream's Quick Playlist and Trash buttons and the hidden Lyrics button's width |
| src/Noctis.UI/Localization/Strings.resx | 1 | per key: Plus's shorter Regenerate tip kept; upstream's shorter Local API warning taken |
| | 2 | theirs: upstream's new Lyrics Page, Other Versions and lyrics-search keys + the same phone keys as ours |
| Strings.{ar,de,es,fr,ja,ko,ru,tr,zh-Hans,zh-Hant}.resx | all | `resx_merge.py` per key: no key changed by both sides, none removed, no duplicate keys. "Yours Truly, Alfamin" (#292) kept |
| tests/Noctis.Tests/LocalizationTests.cs | 1 | theirs: the same Hidden test + upstream's Lyrics Page key test |
| tests/Noctis.Tests/MobileFixtures.cs | 1 | ours: Plus rig parameters (online access and search) are a superset |
| tests/Noctis.Tests/MobileSettingsTests.cs | 1, 2 | ours: 23 separators (1.6.9's 22 + the Plus Offline row, as since #289) |
| Phone: NowPlayingViewModel.cs (5), ShellViewModel.cs (3), SettingsPageViewModel.cs (3), PlaylistListPageViewModel.cs (2), SongListPageViewModel.cs, NowPlayingPage.axaml (2), PlaylistListPage.axaml, PlaylistTools.axaml, SearchPage.axaml, SongListPage.axaml | all | ours: each block already held 1.6.9's phone code (beta 3 picks, #289) plus the Plus additions |

## Brought in twice

`dup_added.py v1.6.9`: one: `NowPlayingViewModel.SyncPlayedSongs` (phone) appeared twice (the beta 3 pick + this merge);
the second, identical copy removed. No other block of 5+ lines occurs more often than on either side.
