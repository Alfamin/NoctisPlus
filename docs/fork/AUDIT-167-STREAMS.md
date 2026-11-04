# Audit: streamed songs vs. file-exists checks (after 2.7.1)

2.7.1 fixed `VlcAudioPlayer.SourceExists` (src/Noctis/Services/VlcAudioPlayer.cs:3189) so it also accepts
`ProgressiveTransfer.IsAvailable`. This audit greps `src/` for other places that use a bare `File.Exists`
(or a missing-file mark) on a track that may still be streaming (PendingStream, progressive transfer,
StreamCache, BotLibrary). Static read only, not live-checked. Time-boxed: lines below were found by grep and
spot-read; each needs a confirm step before fixing.

Common fix: replace `File.Exists(track.FilePath)` with one helper, e.g.
`track.IsPendingStream || ProgressiveTransfer.IsDownloading(path) || _audioPlayer.SourceExists(path)`
for "can this be played" checks, and `!IsDownloading(path) && File.Exists(path)` for "is the file complete"
checks (analysis, tags, timing).

## Already safe
- PlayerViewModel.MissingTracks.cs:42 and :289 skip `IsPendingStream` / refetchable, then use `SourceExists` (fixed in 2.7.1).
- WordLyricsService.cs:251 guards with `ProgressiveTransfer.IsDownloading` first.
- ProgressiveTransfer.cs:122/133 is the source of truth itself.

## Suspects (highest risk first)

1. **True Shuffle drops downloading songs** - src/Noctis/Features/TrueShuffle/TrueShuffleService.cs:245 and :290.
   Scenario: a song is being streamed/downloaded into the library; the shuffle build filters `s.Remote || File.Exists(s.Path)` and the queue top-up filters `NoFile(t) || File.Exists(t.FilePath)`. While the final file is not there yet the song is left out of the shuffle and the up-next fill. Risk: medium (silent, matches the 2.7.1 symptom: "song skipped"). Fix: use the playable helper above.

2. **Play / context actions do nothing for a downloading song** - `if (track == null || !File.Exists(track.FilePath)) return;` in
   AlbumDetailViewModel.cs:1127, :1145, :1256; FavoritesViewModel.cs:333; HomeViewModel.cs:1032; InboxViewModel.cs:200;
   LibraryAlbumsViewModel.cs:1245; LibraryFoldersViewModel.cs:472.
   Scenario: user clicks a just-added streamed song (or its menu action) before the file is complete; the handler returns silently. Risk: medium (user-visible dead click). Fix: `_audioPlayer.SourceExists` / pending-stream check for play actions; keep `File.Exists` only for file actions (show in folder, edit tags) but then show a short "still downloading" line.

3. **Artist / album catalog treats downloading songs as not owned** - ArtistDetailViewModel.Catalog.cs:54, :63, :119, :149, :172-174; AlbumDetailViewModel.Catalog.cs:248.
   Scenario: a song is mid-download; the catalog builds "local" sets with `File.Exists`, so the row shows as an online row (download offer again) and Play all leaves it out; it flips once the file lands. Risk: medium-low (duplicate download offer, row flicker). Fix: count `IsPendingStream`/`IsDownloading` as local.

4. **Analysis / timing reading a partial file (inverse case)** - AudioAnalysisCoordinator.cs:115; ReplayGainAutoScanService.cs:203, :273; LyricsStudioEngine.cs:188; OnlineTimingLane.cs:568; LyricsBulkService.cs:324.
   Scenario: if the progressive transfer writes to the final path, `File.Exists` is already true while bytes are still arriving, so loudness/BPM/ReplayGain/word timing can run on a cut-off file and store wrong values. Risk: medium if the transfer writes in place (confirm in ProgressiveTransfer), none if it writes to a temp name. Fix: `!ProgressiveTransfer.IsDownloading(path) && File.Exists(path)`, like WordLyricsService.cs:251.

5. **Finder library helpers** - src/Noctis/Features/FreeMusicFinder/PlaylistLibrary.cs:55 (`songs.Where(s => File.Exists(s.Path))`), FinderOnlineSearch.cs:396, :437, :478, :536.
   Scenario: playlist matching / "already have it" / Streamed state read while a bot or stream download is running; the song is reported missing and offered again, or a playlist row shows missing. Risk: low-medium. Fix: include `ProgressiveTransfer.IsAvailable(path)`.

6. **Not found by grep, still to check by hand**: AutoMix PrepareNext, gapless pre-open, Next/Previous skip loops, scrobble submit, library reconcile/remove-missing. None of them hit a bare `File.Exists` on a track path in this grep, which suggests they go through `SourceExists`/`IsFileMissing` (now fixed), but they were not read line by line.
