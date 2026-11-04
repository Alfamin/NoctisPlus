# #256 Offline: one For you Play led to many download requests

Status: investigation only (docs), no code changed. Written under a short deadline; the cause below is the best reading of the logs + code, confirm with a live offline test.

## Timeline (c1.log, session 20:22-22:11)
- 22:04:05 For you page opened (TopSong/TopArtist binding warnings, page data null while offline).
- 22:04:07 `[Queue] For you Play: "Childs Play" at page position 1/25; replacing 2158 queued songs` - the whole 25-song page becomes the queue.
- 22:04:22-24 online-timing look-aheads time out (15-17 s, "request was canceled") - network down.
- 22:06:02 -> 22:08:07: 33x `[Finder] kept N% of a stopped download to go on later` (6-17% each), roughly every 3-10 s, interleaved with look-aheads for later page songs (Bruno Mars, Lil Tecca, Drake, ...). Each line = one download that was started, got a few %, then was stopped.
- 22:11:36 second For you Play ("replacing 6 queued songs") -> 3 more "kept" lines within 4 s (36 total in the log), i.e. the kept partials were picked up again at once.
- "download ahead ... failed, trying again shortly" lines in c1.log: 0.

## Cause
1. The For you Play replaces the queue with the page (log line above; source: src/Noctis/ViewModels/PlayerViewModel.QueueStreaming.cs:216). That alone is fine.
2. Offline, each stand-in fails or stalls; playback moves on, so the "next two" window in `StreamAhead` (src/Noctis/ViewModels/PlayerViewModel.QueueStreaming.cs:338) walks down the page. Every move starts new ahead downloads (`PrefetchAsync`, fast lanes) and stops the ones no longer in the window: `gone.Source.CancelQueued` at QueueStreaming.cs:385. Each stop saves a partial (src/Noctis/Features/FreeMusicFinder/PartialAudio.cs:85). Result: one request per page song, ~33 partials in two minutes.
3. (Risk, not seen in this log: 0 retry lines.) A failed ahead download is retried every 10 s (QueueStreaming.cs:408, `Task.Delay(10 s)` then `PostStreamAhead`), with no offline check, so the loop never backs off.
4. Nothing in StreamAhead / AskAhead (QueueStreaming.cs:268) / Downloads.PostAskAhead (Features/FreeMusicFinder/Downloads.cs:475) checks whether the network is up.
5. Partials: yes, offline stops leave partial files; when the queue is set again (22:11:36) the window's songs resume immediately and back-to-back (3 in 4 s). No cap on concurrent resumes beyond the two fast lanes, and every queue change re-triggers them.

## Proposed fix
- Offline gate: if the last bot/network attempt failed with a connection error (or `NetworkInterface.GetIsNetworkAvailable()` is false), StreamAhead and AskAhead do nothing ahead; only the song pressed is tried, once. Re-arm on `NetworkChange.NetworkAvailabilityChanged` or on the next successful request.
- Retry backoff: replace the fixed 10 s retry with 10 s / 30 s / 2 min, and don't retry at all while offline.
- Don't let a failed/offline song advance the ahead window: when the pressed song fails offline, stop playback with a short "You're offline" note (minimal-text rule) instead of skipping through the page.
- Partials: resume at most one kept partial at a time, and only for the song about to play; keep the rest on disk until reached.
- Test: unplug network, For you -> Play; expect at most 1 request and 0 "kept N%" lines; reconnect and expect the window to start normally.
