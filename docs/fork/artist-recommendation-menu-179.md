# Owner #179: artist recommendation menu

Local fix on `native`; no push, tag or release authorized.

The card dots button calls `ContextMenu.Open(tile)`, which does not raise the old `Opening` handler. On other opening paths,
that handler could also run before the popup had its song DataContext. The label was visible but had no command or artist
choices. The fix binds the item when it attaches and when its DataContext changes. Detached card-menu commands now refer
to the pick's explicit shelf owner rather than looking for a UserControl through popup ancestors. The unobtrusive heading
unblock list uses the same lifecycle.

Single-artist cards block immediately. Multiple credits produce one choice per artist. Song/player/artist/Search menus
share a native confirmation pill with eight-second Undo. The shelf retains its inline Undo and restores removed cards
when an external-menu Undo unblocks their artist. Undo never restores a still-blocked artist. Reset opens a folded shelf,
shows `For you was reset` immediately, rebuilds picks, and preserves explicit artist blocks. Count-only block/unblock and
Reset log lines provide click evidence without artist names.

Entry points to check after future menu refactors:

- For You card dots and right-click (one artist and several credits).
- For You heading right-click: Recommend again, Reset.
- TrackContextMenuBuilder: Songs, albums, playlists, queue and local Search song menus; MenuV2 Tools and classic layouts.
- Playback bar options (both anchors share the same flyout).
- Native artist page options.
- Online Search row download-button context menu.

Headless regression tests open the actual card ContextMenu on its first opening and after late/recycled DataContext,
checking commands and exact artist choices. Native Finder checks cover cross-menu card restoration, overlapping blocks
and Undo, collapsed Reset feedback and preservation of blocked artists. The existing storage tests continue to guard
`BlockedArtists` versus the incompatible legacy `Artists` field.

Validation also exposed two startup-update checks depending on the host's Windows fullscreen state. An instance-level,
internal predicate now lets tests explicitly choose the scenario; the production default still calls GamingBudget.
A separate check confirms fullscreen suppresses the prompt and the same update is offered after fullscreen ends.

Live results and final gate totals are recorded in the handoff and the blueprint. TEST is used in place: never reset its
settings, restore old backups or copy its Telegram session. Temporary test artist blocks must be cleared at the end.

## Verified 2026-10-26

FastGates `codex-179c`, ten processes: 6,805 total / 0 failed / 43 skipped; Finder 1,658 ok / 0 wrong; publish succeeded.
The final build is already staged in `C:\Users\Alfamine\NoctisPlus-Test\app` from `publish-codex-179c`.
Real TEST clicks covered single-artist right-click and dots, a three-artist dots submenu, card Undo, library song Tools
with multiple credits, playback options, artist-page options, online Search download-button context menu, their shared
Undo notices, Reset, restart persistence and Recommend again. After Reset the saved page contained zero credited blocked
artists while related artists still appeared. The final build also verified Reset on a folded shelf: it opened, visibly
confirmed and rebuilt. All temporary blocks were removed through the app; the final count was zero. The Last.fm key
fingerprint was unchanged and required TEST settings remained non-null. No music playback/download, installed profile
access, music-file writes, push, tag or release. Computer-use runtime reset immediately after the final check.

Initial gate attempts found the host-dependent update checks described above and one known DrawerGlide timing flake;
the final complete gate is clean. Audio/animation code was not changed. The overlapping-Undo safeguard is covered by the
Finder regression: undoing one artist never restores a different artist that was blocked afterwards.
