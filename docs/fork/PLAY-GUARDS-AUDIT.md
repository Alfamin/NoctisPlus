# Play guards audit (streamed songs still downloading)

Scope: src/Noctis Views/*.axaml.cs, ViewModels/PlayerViewModel*.cs, Services.
Question: does any play path refuse a click because the file does not exist yet?

Result: 0 hard refusals found in play/enqueue paths (quick grep pass, deadline-limited).

## Existence checks that touch playback (all reviewed as safe or non-blocking)

- src/Noctis/ViewModels/PlayerViewModel.MissingTracks.cs:42 - missing flag skips IsPendingStream and refetchable tracks, so a downloading stream is not marked missing.
- src/Noctis/ViewModels/PlayerViewModel.MissingTracks.cs:289 - bulk scan excludes fetchable tracks before SourceExists.
- src/Noctis/ViewModels/PlayerViewModel.OnlinePlayback.cs:127 - File.Exists OR ProgressiveTransfer.IsAvailable; partial downloads count as present.
- src/Noctis/ViewModels/PlayerViewModel.cs:4316 - gapless pre-stage only (remote streams handled separately); unstaged tracks still open on TrackEnded, no refusal.
- src/Noctis/ViewModels/PlayerViewModel.cs:1844 - returns early when CurrentTrack file is missing; not a play entry point (side action on the current track). Worth a second look.
- src/Noctis/Views/MainWindow.axaml.cs:2133 - File.Exists guard returning false; context not verified in this pass. Worth a second look.

## Not checked

IsFileMissing is never used as an early-return guard in Views or PlayerViewModel*.cs (grep). Service-level File.Exists hits (artwork, lyrics, converter, analysis) are not play paths.
