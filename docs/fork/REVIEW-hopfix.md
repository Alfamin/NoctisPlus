# Review: hopfix (KaraokeWordText posed path)

Diff: `git diff origin/android-plus-lyrics...HEAD`, file `src/Noctis.UI/Controls/KaraokeWordText.cs`.

## Findings

1. **Blank frame when the effect throws (KaraokeWordText.cs:394).** The old loop did `break` after
   drawing the pieces posed so far; the new pre-pass does `return` before anything is pushed or
   drawn, so the word vanishes for that frame, and `_lastPoses`/`_lastPoseCount` are not updated.
   Suggest: on `_effect is null`, fall through to Noctis's own drawing for this frame (or at least
   draw unposed) instead of returning.

2. **Half-pixel snap at the moment a word stops (KaraokeWordText.cs:396 + :409).** `moving` is
   true only while the pose differs from the previous frame. The first still frame switches from
   unsnapped, unrounded offsets to `RestOnPixels` rounding, so a word that comes to rest at a
   fractional offset jumps up to 0.5 device px once. Same in reverse on the first moving frame.
   Smaller than the old ~0.6 px hop, but it is still a visible hop on settle. Consider rounding only
   when the rest offset is already within ~0.05 px of a whole pixel, or easing the last frame.

3. **First frame after a piece-count change is treated as still (KaraokeWordText.cs:396).**
   `moving |= _lastPoseCount == count` means a re-layout (count change) draws snapped/rounded even if
   the effect is mid-animation; next frame flips to unsnapped. One-frame shimmer on re-layout.

4. **Only offsets are rounded (KaraokeWordText.cs:461).** `Scale` is left as is, and `origin` is not
   snapped, so a rest pose with Scale != 1 or a fractional origin still lands off-grid; the
   "sits on whole device pixels" comment overstates it.

5. **Exact equality for "moving" (KaraokeWordText.cs:396).** `!=` on `LetterPose` compares doubles
   exactly; an effect that returns time-based values with tiny jitter (e.g. 1e-12 drift) is always
   "moving" and never rests. An epsilon compare would be safer.

## Per-frame allocations

- None found in steady state. `LetterPose` is a `readonly record struct` (LyricsWordEffect.cs:32), so
  `Pose(i)`, `with`, and `!=` are allocation-free. The local function `Round` in `RestOnPixels`
  captures into a struct closure (not a delegate), so no heap allocation.
- `_poses`/`_lastPoses` only grow when the piece count exceeds capacity (amortised, not per frame).
- `VisualRoot is TopLevel` lookup per frame is cheap (no allocation).
