# #255 True Shuffle on the phone (stage 4 piece)

Goal: Noctis.Mobile offers the same shuffle modes as desktop (Off, Noctis shuffle, Equal chance,
No repeats, Discovery, Never played, Source). "For you" stays desktop-only until Free Music Finder moves.

## Done in this commit (2026-11-04)

Pure engine files moved, unchanged (namespace still `Noctis.Features.TrueShuffle`, still `internal`),
from `src/Noctis/Features/TrueShuffle/` to `src/Noctis.Core/Features/TrueShuffle/`:

| File | What it is |
|---|---|
| ShuffleEngine.cs | `ShuffleMode`, `Song`, `ShufflePlan`, `ShuffleEngine.Build/StillWanted/Eligible` |
| ForYou.cs | `TasteModel`, `ForYouPlanner` (pure maths; ShuffleEngine calls it for the ForYou mode, so it moves with it; the Finder-backed part is not in this file) |
| CycleStore.cs | per-scope "already heard" cycle file (System.Text.Json only) |
| ListeningLog.cs | `PlayTracker`, `ListenStore`, `NoctisHistory` |
| FileIdentity.cs | stable file identity (Windows interop guarded at runtime; CA1416 already suppressed in Core) |

`Noctis.Core.csproj` gains `<InternalsVisibleTo Include="Noctis.Mobile" />`. Noctis and Noctis.Tests
already had internals access, so desktop code and tests needed no edits. Desktop behaviour is identical
(same source, same namespace, only the assembly changed).

Stays desktop (`src/Noctis/Features/TrueShuffle/`):
- TrueShuffleService.cs (Avalonia Dispatcher, desktop view models, settings UI hooks)
- ShuffleQueue.cs (uses `Noctis.ViewModels` / `Noctis.Services` of the desktop app; defines `ShuffleScope`)
- RollingQueue.cs (needs `ShuffleScope` from ShuffleQueue.cs; tried moving, moved back)
- TrueShuffleFeature.cs (desktop plugin entry)

## Results

- `dotnet build src/Noctis/Noctis.csproj -c Release -v q`: 0 errors
- `dotnet build src/Noctis.Mobile/Noctis.Mobile.csproj -c Release -v q`: 0 errors
- `dotnet build tests/Noctis.Tests -c Release`: 0 errors
- Shuffle test classes (run one by one, not the full suite):
  NativeTrueShuffleTests 16/16, ShuffleHelperTests 5/5, ShuffleIssue110Tests 13/13,
  TrueShuffleDomainTests 2/2, TrueShufflePluginTests 21/21, TrueShuffleQueueTests 9/9,
  QueueReshuffleTests 10/10. (TrueShuffleForYouChecks is a helper class, no tests.)

## What the phone has today

`NowPlayingViewModel` owns a `PlaybackQueue` (Noctis.Core). Every Shuffle button (album, artist,
song list, search featured) calls `Shell.Player.PlayShuffled(tracks, source:)`, which does
`_queue.SetShuffle(true, rng)`; the Now Playing toggle calls `_queue.SetShuffle(!on)`. That is the
plain "Noctis shuffle". No modes, no cycle memory, no listening log.

## What's left (next steps, smallest first)

1. Move `ShuffleScope` (one enum line) from ShuffleQueue.cs into Core (e.g. top of RollingQueue.cs),
   then move RollingQueue.cs too. Pure; same namespace.
2. Phone mode service (new, `src/Noctis.Mobile/Services/PhoneShuffle.cs`): map `Track` -> `Song`
   (Id, Path, Title, Artist, PlayCount, skip/explicit/disliked/snooze flags), call
   `ShuffleEngine.Build(songs, mode, cycle.Seen, ...)`, feed the result into `PlaybackQueue` in plan
   order (shuffle flag off so the order is kept), and use `RollingQueue` + `StillWanted` for long lists.
   Cycle and listen files under the phone app-data folder via `CycleStore` / `ListenStore`.
3. `PlayTracker` hook in NowPlayingViewModel (track start / position / skip) so No repeats, Discovery
   and Never played see what was heard.
4. UI: the Now Playing shuffle button opens a small mode picker (icons + one short line, native Noctis
   look); "Off" and "Noctis shuffle" keep today's behaviour exactly. Hide "For you" on the phone.
5. FileIdentity on Android: it falls back to the path key off Windows; check that the phone's content
   paths stay stable across rescans before relying on No repeats cycles.
6. "For you": after Free Music Finder moves (later stage).
