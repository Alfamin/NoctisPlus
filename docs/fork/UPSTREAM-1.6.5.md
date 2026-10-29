# Upstream Noctis 1.6.5 merge (2026-10-29)

Merge `v1.6.5` (`6b9073fc`) into Plus `2d952538`, on `merge-165`. Plus remains 2.4.5;
this is not a release. `ProductIdentity.NoctisBaseVersion` and README credit 1.6.5.

## Resolution decisions

- Keep Plus product/profile/ports/installer identity, update feed and asset prefix, built-in
  features, settings actions, online artist/album metadata, imported playlist counts and menu actions.
- Keep new upstream smart-playlist counts, sources, chapters, accessibility, CJK rendering,
  protected artist names, platform fixes, dependency changes and tests.
- Combine headphone-surround initialization with Plus's profile-import-aware audio warmup.
- Retain Plus's first-run card and its setup actions; use the common Add Folder command and
  upstream scanning state, without drawing a second empty-library card over the welcome card.
- Owner #201 rejects upstream's lyrics redesign. Retain Plus's native word painter, original
  fallback text templates, font inheritance, 650 ms line motion, 35 ms cascade, blur and active-line
  scrolling. Keep original sweep geometry and held-word envelope in Plus-specific converters;
  the new upstream shared controls remain available to the phone. Do not turn on the shared
  lift/glow clock or early-scroll lead in the Plus desktop view model.
- Accept the duet correctness fix: overlapping sung lines remain lit and keep sweeping. This
  does not replace the painter or introduce early scrolling. Credits and source controls are new.
- Preserve the original fresh-install opt-ins (album colour, pause fade, ignored leading artist
  words stay off); preserve existing saved choices. Do not add a duplicate upstream word-motion
  switch alongside the existing native Lyric Motion controls.
- The new weighted Reshuffle action is available only in Noctis shuffle mode. It cannot reorder
  any native True Shuffle session, in either the main queue or mini player.
- Fix two measured correctness/performance issues without changing appearance: explicit queue
  saves drain the existing background save chain; a native word mutates its own gradient instead
  of forcing TextBlock remeasurement each frame. The original gradient values are preserved.
- Keep the existing Plus dispatcher-claim helper (the upstream helper solves the same test race).
- Upstream tests that hard-coded official installer names now use ProductIdentity. Mobile settings
  tests expect Plus's pre-existing Dark default. Empty Home tests exercise Plus's welcome instead
  of upstream-only control names; CJK tests accept the native WrapPanel subclass while retaining
  their measured text-width/spacing assertions.

## Validation

See `artifacts/plus/codex-165*-*.log` for build, full 10-shard suite, Finder checks and publish.
The initial run found 20 failures, including real template/style overlaps; the next had five.
The initial 21 focused checks passed, but the owner subsequently rejected the upstream appearance.
The #201 correction passes 54 native-painter, layout and original-gradient checks. Tests for the
intentionally unadopted upstream early-scroll painter now exercise Plus's actual three surfaces
instead; shared/mobile renderer unit tests remain. Full final gates and
TEST live verification are recorded in the owner's blueprint/handoff before promotion.

## Overlapping features reviewed (#201)

The branches changed 66 of the same paths (including tests and ten localization resources).

| Area | Decision |
|---|---|
| Product, installer, startup, update feed | Plus identity, folders, ports, update assets and version retained; upstream platform fixes additive |
| Core Album/Artist/Track models | Online/missing metadata retained; additive list statistics, genres, M4B and opt-in Inbox fields kept |
| Library and persistence | Keep Plus imports and filtering; accept scan-preservation and serialized settings-update fixes; Inbox remains off |
| Player and audio | Progressive/offline/queue paths retained; additive chapters/surround; platform pitch fix; native shuffle guarded |
| Home | Plus welcome/setup card and For you retained; no second empty-card overlay; cached portraits and scanning state fixes |
| Artist/album/song/playlist pages | Plus online catalog and playlist playback retained; additive artist list/sorts; accessibility and metadata display fixes |
| Lyrics page, panel, mini player | Plus rendering/motion restored; new sources/credits and duet correctness kept; no upstream animation takeover |
| Main window, sidebar and routing | Plus unified Search/Downloads remain; additive optional Inbox; shortcut selection fix and backdrop-stutter fixes |
| Settings | Plus Features/What's new and settings migrations retained; additive source/audio controls; no duplicate word-motion UI |
| Localization | Union of keys; Plus wording wins on collisions; duplicate Add Music Folder removed |
| Tests | Preserve Plus branding/defaults; retain new source/parser/platform tests; adapt UI tests to the owner-approved renderer |

Wall-clock frame samplers (MobileMotion, MetadataSearchPanelAnimation, ColorPickerFlyoutMotion)
run after the ten CPU-heavy shards, with unchanged timing assertions and results included in the
same total. Running their frame samplers against 40 competing test threads caused intermittent
missing-frame failures. No test is omitted by this scheduling change.

No WordLyrics backend/model downloaded, no installed profile/session read or copied, no files
under D:\music changed. `scripts/sonic/` is unrelated, pre-existing untracked work.
