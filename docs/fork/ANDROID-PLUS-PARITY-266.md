# Plus → Android parity map (#266)

Baseline: local upstream tag `v1.6.7` against `codex-android-262` (initial checkpoint473a197a).
Inventory comes from the changed Core models/services, desktop Features/ViewModels/Services/Views,
the extracted Finder/TelegramSync projects, and current phone host/page wiring. A shared class
existing is not evidence that its behavior is exposed on Android. Refine each row as it is ported.

Priority: P0 playback/data safety, P1 everyday Plus features, P2 enhancements. Upstream-only beta
complaints remain behind useful Plus parity. Do not rewrite desktop behavior merely to share it.

| Priority / Plus difference | Source of Plus behavior | Android state / remaining work |
|---|---|---|
| P0 Play while receiving MP3/FLAC | Finder ProgressiveTransfer/ProgressiveAudioBuffer; desktop ProgressivePlayback | Media3 ProgressiveDataSource wired; real synthetic decode/start/buffer/resume proof. MP3/FLAC real synthetic decoder checks now also prove collision-renamed reopen after Close without a retained reader, exact byte offset, buffering/resume and completed seek. Real phone bot delivery and ahead-seek cancellation still need proof. |
| P0 Latest tap wins, current song first | PlayerViewModel.OnlinePlayback/QueueStreaming; Finder Downloads | Native pending queue/current preparation now wired to Search/ForYou. Rapid1→3 taps live start3 only; queue Next/Previous resolve entries, pause while loading holds intent, stale answers/errors cannot replace current. Exact next4 positions / next2 audio with10s settle; deterministic cancellation/offline tests. Real phone bot delivery still unproven. Explicit saves independent. |
| P0 Network loss and recovery / explicit offline mode | Core OnlineAccess; desktop SettingsViewModel.Offline; Finder retry/partial cache | Native Offline mode persists and gates startup/internet Finder/metadata/lyrics/scrobbling; cached playback continues, cached lyrics work, pending selection/pause intent resumes on return. Live toggle/restart/wait/resume passed on synthetic audio. Corrected recovery trace has no duplicate asks; full272c9576/0failed/83skipped, Finder1760/0. LAN account Media3 transport/global sync recovery still separate, real phone bot transport unproven. |
| P0 Durable download queue, cancel/remove/retry, Download now vs Add to downloads | Finder Downloads/FinderDownloadQueue; desktop DownloadsViewModel | Native phone search/download page exists, stable progress rows fixed. Audit all management actions, failed/busy provider statuses and foreground priorities live. |
| P0 Personal cloud library across devices | TelegramSync + Finder/Sync/WTelegramDrive | 71 fake-transport tests pass; controller, durable scheduled jobs and native opt-in controls NOT wired. Off until explicit opt-in. Preserve LAN account path. Own session on each device. |
| P1 Useful default Home | desktop HomeViewModel/HomeView (New in Library), ForYouShelfViewModel | Native Home shows real playlists/New in Library plus recents/OnRepeat; empty pins hidden. Live audit opened AuditMix with both songs. First-run actions retained, no fabricated history/downloads. Full272c green9576/0failed/83skipped, Finder1760/0. |
| P1 Personalized For You, saved pages, feedback, undo, block song/artist | Finder ForYou/ForYouCache/DiscoverFeedback + desktop ForYouShelfViewModel | Typed IOnlineDiscovery + FinderDiscovery expose existing planner/cache/feedback through native shelf/page. Live catalog, song/artist dismissal+8sUndo and offline restart passed. Proxy/canonical-path fixes preserve choices on Android. Qualified-listen/Last.fm controls, full queue context and real phone bot playback remain partial. |
| P1 Rich unified online search | Finder CatalogSearch/FinderOnlineSearch; desktop SearchViewModel | Phone only exposes song results (10 cap), local search remains fast. Add artists/albums/playlists/top result and native navigation using existing ranked results, not a second search engine. |
| P1 Fill existing artist/album pages with online catalog | CatalogPages, ArtistIdentity/Popularity/Editions/Portraits; desktop *.Catalog/.Releases | Phone pages remain local. Merge owned/unowned rows, correct artist IDs, dedup editions, sharp progressive covers, skeletons and navigable similar artists; preserve mobile templates. |
| P1 Spotify/Deezer multi-playlist import and missing rows | Finder SpotifyPlaylists/PlaylistCatalog/PlaylistTransfers; desktop StreamingPlaylistImport/PlaylistViewModel.Catalog | Phone SAF library matching fixed; native import UI/remote ordered rows still missing. Keep full playlist order and all songs; downloads only on explicit choice; overlap dedup. |
| P1 Stream complete album/playlist context | desktop PlayerViewModel.QueueStreaming; Core PendingStream/OnlineStandIn | Native Search/ForYou list context now built, persistent provider-only metadata restored paused without requests; live native7-song queue, next/clear and restart checked with isolated synthetic audio. Album/playlist remote catalog/import pages still missing; do not call that context complete. No play-only import. |
| P1 True Shuffle modes + scopes + honest active UI | Core Features/TrueShuffle; desktop TrueShuffleService | Native6modes/3scopes/menu at player/mini/queue built and basic live checked. Full source/list/history/restore cases in tests. Qualified-listen learning, bounded planning and ForYou mode still missing. |
| P1 Automatic online word timing and fit/fallback/source selection | desktop OnlineTimingLane/OnlineWordTiming + LrcRed/Unison/Kugou services | Phone currently LRCLIB only. Extract portable source/fit/credit/cache logic; lrc.red→Unison→Kugou→LRCLIB, no AI/model download without consent. Queue lookahead/current priority and cached timing sidecars also required. |
| P1 Lyric Motion exact Plus feel | desktop Features/LyricMotion, MotionWordPresenter; phone plan LYRIC-MOTION-PHONE-255 | Phone uses stock KaraokeWordText native path. Follow measured phone-effect approach or a proven painter, retain font/curves and stable baselines; no desktop renderer replacement. |
| P1 WordLyrics controls/repair/ignore/per-song state | desktop WordLyricsService/Worker/SongRules/WordLyricsViewModel | No native phone worker/UI yet. Online timing is first useful phone path; evaluate model/runtime/size/heat before local AI. Support custom lyrics/source choice/ignore/10day retry and completed-file gating, never pretend desktop CLI runs on Android. |
| P1 Source quality/provider/proxy preferences | Finder BotQuality/BotSelections/MusicSources/Proxy; desktop settings | Phone Telegram pane has quality/provider/proxy wiring. Live account/provider negotiation not proven. Honor user MP3/FLAC on both bots; fallback on busy/limit without repeating searches. |
| P1 Add streamed song, short status and correct menu actions | FinderOnlineSearch.AddStreamedAsync + desktop player/lyrics menus | Native phone Add action consistency/import/status still needs all-entry-point audit. Saving must wait for complete audio before timing/import and never hide ongoing processing. |
| P1 Lyrics identity, explicit text and punctuation | Core parser/Timeline changes; OnlineTimingUncensor/Aside/Fit | Shared parser fixes inherited. Phone online path bypasses richer fit/explicit/source rules. Carry these with source extraction; preserve background vocals/translations already in base. |
| P1 Last.fm login survives offline/VPN failures | desktop LastFmService | Phone has its own scrobbler/settings implementation; inspect equivalent failure handling and verify invalid-session-only deletion. Never copy desktop tokens. |
| P2 Correct stream listening stats/taste | PlayHistoryEvent.Seconds, PlayHistoryService; desktop ListeningStats/Wrap | Core records duration; phone first-moving-tick history exists. Audit skips/audible duration/qualified plays for ForYou, preserve favorites/rating/playlist feedback. |
| P2 What's New with links/reopen | desktop Features/WhatsNew + Settings/About | Phone has upstream WhatsNew pane; Plus payload and deep links not integrated. Explain new settings succinctly; reopen from About. |
| P2 Lyrics Reels/share video | desktop LyricsVideo* and LyricsVideoViewModel | No phone implementation. Useful on phone, NOT inherently desktop-only; evaluate native media encoding/share and performance after playback/lyrics parity. Do not pull Windows ffmpeg assumptions across. |
| P2 Plus defaults/identity/update channels/licenses | Core ProductIdentity/AppSettings, Android AppVariant, desktop update policy | Android Plus identity/side-by-side package exists. Stable signing/release pipeline still requires owner release authorization/key. Only apply meaningful phone defaults; retain license/provenance. |
| Desktop implementation only | PowerShell installs/UAC/desktop hotkeys/VLC/Windows tray/DWM/CLI editing | Do not port these OS mechanisms. Port their useful user outcomes through Android equivalents where needed, including file grants, controls and sharing. |

Existing upstream phone functionality (local library, album/artist pages, LAN account, EQ, scrobbling,
plain/synced lyrics, themes) is reused, not counted as a newly completed Plus port. #263 additions
already committed remain, but further upstream-only features are deferred.

## Execution and evidence

1. Green264c:9549 tests/0failed/83skipped; Finder1760/0; sync71/0 fake.
2. #265 local Home foundation: real library/playlists, empty pins hidden, first-run actions retained.
3. Typed phone ForYou adapter + Home shelf; preserve cached pages and negative feedback/undo.
4. Catalog/search/playlist context and recovery/queue integration; phone timing and Lyric Motion.
5. Sync host/opt-in/durable jobs, remaining settings/share/export. Reorder to unblock real dependencies.

Each slice gets focused meaningful checks, full gates at integration boundaries, Android x64/ARM64
compile, and real synthetic-profile emulator checks where possible. No DONE on headless checks alone.
Real account/physical-phone constraints stay explicit. No releases or main/native merges.
