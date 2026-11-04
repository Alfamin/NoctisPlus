# Lyric Motion on the phone (owner #255, stage 4)

Goal (ANDROID-PLUS-PLAN.md feature map, "Lyric Motion letter rise, Plus lyrics look"):
the phone lyrics page shows the Noctis Plus look. Words light up with a sweep, letters
rise one after another, subtle and in sync. No shake, no jitter, and the whole sentence
must never move when one word moves.

Status: plan only. Nothing here is built yet.

## 1. How the phone page draws words today

- `src/Noctis.Mobile/Views/LyricsPage.axaml`: `LyricsList` (ItemsControl over
  `Lyrics.Lines`). Each line is a `Button.lyric-line` that holds two word layers
  (`Words`, `BackgroundWords`) through the `WordCell` DataTemplate.
- `WordCell` = Viewbox (DownOnly) > Panel > two `Noctis.UI` `KaraokeWordText` controls:
  a glow copy (`word-glow`, BaseOpacity 0, blur Effect, only on emphasis words) and the
  sweep copy (`word-sweep`). Both bind Progress, LeadIn, TrailOut, IsEmphasis, EmphasisMs,
  HeldDurationMs, IsLastInLine and Lift from `WordTiming`.
- `src/Noctis.Mobile/ViewModels/LyricsPageViewModel.cs` drives the Core `LyricsTimeline`
  (shared with desktop), which writes Progress, Lift and the held-note clock every frame.
- Line motion: `Button.lyric-line` scale 0.96 -> 1 with a 1 s
  `TransformOperationsTransition` using the shared/mobile `LineMotionEase`
  (`src/Noctis.UI/Helpers/LineMotion.cs`), not the desktop Plus 650 ms curve
  (`PlusLineMotion.cs`). UPSTREAM-1.6.7.md decisions table: shared LineMotion may evolve on
  its own; desktop references PlusLineMotion explicitly.
- `KaraokeWordText` (`src/Noctis.UI/Controls/KaraokeWordText.cs`) has two drawing paths:
  - Native: whole word (or per letter for held notes via `EmphasisMotion`), gradient
    brush stops moved each frame (no new brush), float up by `LiftPixels()` (full lift
    rounded to device pixels), drawn under the `Unsnapped` shear only while `IsMoving`.
  - Posed: when `LyricsWordEffect.Current` (an `IWordEffect`, plugin API 1.5/1.6.7
    `RegisterLyricsEffect`) is set and its `Mode` is Whole/Letters, each piece is drawn
    with `PoseTransform` (scale about centre, em offsets) and glow from `LetterPose`.
    This is the path the stock Lyric Motion plugin (`plugins/Noctis.Plugins.LyricMotion`,
    `Motion/LetterMotion.cs`, `Motion/Tuning.cs`) uses.
- The phone page sets no `IWordEffect` today, so it always draws the Native path.
- Fonts: the phone uses the Noctis.UI font resources, not the Plus Inter setup.

### Known stock issue (the "sentence shakes" bug)

In the Posed path (`RenderTextLayout`), the word is drawn under `Unsnapped * at` only
while `Progress` is between `KaraokeSweep.InertFuture` and `InertPast` or `Lift` is
between 0 and 1. A word that is posed but still (not sung yet, or finished) is drawn on
the pixel grid, and the moment it enters or leaves that window it switches between
snapped and unsnapped drawing. Skia rounds the baseline in one case and not in the other,
so a word that does not move hops by up to ~0.6 px. With several words on a line crossing
the window at different times, the still words hop one after another and the whole line
looks like it shakes. The pose offsets themselves (`OffsetYEm * FontSize`) are also not
rounded to device pixels at rest, so a "finished" letter can sit at 0.3 px and render
blurred or snapped depending on the path.

## 2. What the desktop Plus look is made of

- `src/Noctis/Features/LyricMotion/LetterLayer.cs` (1008 lines): per word, live text until
  the fill reaches a letter, then that letter becomes a picture cut from one
  `RenderTargetBitmap` sheet per word (drawn at screen scale), moved and scaled as a
  picture (even sub-pixel motion), halos are blurred `TextBlock`s with `BitmapCache`,
  one `Painter` control paints the word; `Align` puts a resting word on whole pixels via a
  `Follower` (`_toRow`).
- `Tuning.cs` (192, depends on `Noctis.Plugins`), `Curves.cs`, `Followers.cs` (springs),
  `FillReader.cs` (reads the sweep fill), `LyricMotionScope.cs` (one controller per lyrics
  surface, rides the VM word frame clock; depends on `Noctis.ViewModels`, `Noctis.Services`).
- `src/Noctis/Controls/MotionWordPresenter.cs` / `MotionWordCell.cs`: native text layers
  (BaseText, SweepText) that host the LetterLayer; lift reach rounded to device pixels.
- `PlusLineMotion` 650 ms line curve, Inter font, `PlusSweepForegroundConverter`.

All of this lives in the desktop `Noctis` project. `Noctis.Mobile` references only
`Noctis.Core` and `Noctis.UI`.

## 3. Options

### A. Move LetterLayer + Tuning + MotionWordPresenter into Noctis.UI, use them on the phone

- Work: untangle `LyricMotionScope` from desktop `LyricsViewModel`/Services, split
  `Tuning` from `Noctis.Plugins`, move ~1.8k lines, give the mobile VM a scope hook, swap
  the WordCell template to MotionWordCell. Desktop files move too, so desktop must be
  re-verified (breaks "feature isolation": desktop owner checks would be needed).
- Effort: about 1.5 to 2 five-hour windows.
- Risk: high. Desktop regression risk; picture sheets (`RenderTargetBitmap` per word) on
  Android GPU (Vulkan/GL) are a new, untested surface; `BitmapCache` halos per letter.
- Phone performance: allocates a bitmap sheet and TextBlocks per word at takeover (GC
  spikes on mid-range phones), offscreen render targets cost GPU memory and battery. Per
  frame it is mostly transforms (fine), but takeover moments land exactly when a word starts.
- Look: exact desktop parity.

### B. Drive KaraokeWordText's posed path with the Plus tuning (recommended)

- Write `PlusWordEffect : IWordEffect` in `Noctis.UI`: `Mode` = Letters for splittable
  words, Whole for joined scripts / RTL / very long words (KaraokeWordText already forces
  one piece for those); `Pose(word, letter)` returns the Plus letter rise, grow and glow as
  closed-form curves of `Progress`, `DurationMs`, `LetterCount` (no springs: Pose is a
  pure function of the input, so it stays in sync and has no state to drift).
- Bring the numbers from desktop `Tuning.cs`/`Curves.cs` into a small pure-math class in
  Noctis.UI (copy, desktop untouched). Followers are not needed if the curves are eased
  functions of fill position.
- Fix the hop in KaraokeWordText's posed path (section 1): decide snapping per word from
  "is any pose non-rest", not from the Progress window, and round the resting pose offset
  to device pixels, so a still word is drawn the same way on every frame.
- Phone page: `LyricsWordEffect.Set(new PlusWordEffect())` at mobile startup (only when
  no plugin effect; the phone has no plugins), `LyricsWordEffect.Surface = Page` on the
  page, Plus line ease and Inter font on the phone lyrics resources only.
- Effort: about 0.5 to 0.75 of a five-hour window.
- Risk: low to medium. Desktop draws lyric words with MotionWordPresenter (decisions
  table), so the KaraokeWordText fix only reaches places that still use it; grep every
  `KaraokeWordText` use before changing it and keep the fix behind the posed path only.
  Remaining look risk: live text scaled per frame may step slightly (the reason desktop
  uses pictures). On Android, Skia text is unhinted with subpixel positioning, so this is
  expected to be smaller than on Windows; measure it (section 6). Keep scale changes tiny
  (Plus grow is a few percent) and rely on rise (translation), which is smooth under the
  shear.
- Phone performance: `IWordEffect` calls must not allocate (contract in
  `LyricsWordEffect.cs`); poses are structs; brushes and layouts are reused (stops are
  edited in place). One TextLayout per letter is built once per word in `EnsurePieces`,
  not per frame. Only the active line's words change per frame. Fits 60 fps and keeps the
  GPU idle between lines (good for battery).

### C. Other: a mobile-only letter painter for the active line

- One custom control per active line that caches glyph pictures for that line only and
  paints them (a slim LetterLayer). Use only if B's measured scale stepping is visible.
- Effort: about 1 window. Risk: medium. Performance: one sheet per line instead of per
  word, cheaper than A.

## 4. Recommendation

Option B. It reuses the effect path the plugin API already proves on desktop, needs no
desktop file moves, and is allocation-free per frame by contract. Fix the snapped/unsnapped
switch in the posed path as part of it, since that is what makes the sentence shake.
Keep C as the fallback if the frame measurement shows visible stepping from scaling.

## 5. Files and order (option B)

1. `src/Noctis.UI/Controls/KaraokeWordText.cs`: posed path snap fix (one rule for moving
   vs still, resting offsets on device pixels). Add internal test hooks if needed.
2. `src/Noctis.UI/Helpers/PlusMotionCurves.cs` (new): pure numbers and eases copied from
   `src/Noctis/Features/LyricMotion/Tuning.cs` and `Curves.cs` (no `Noctis.Plugins`).
3. `src/Noctis.UI/Controls/PlusWordEffect.cs` (new): the `IWordEffect` using step 2.
4. `src/Noctis.Mobile/App.axaml.cs` (or the mobile startup class): set the effect once.
5. `src/Noctis.Mobile/Views/LyricsPage.axaml`: `LyricsWordEffect.Surface="Page"` on the
   list, Plus line ease (650 ms `PlusLineMotionEase`) on `Button.lyric-line` for the phone,
   Inter font on the word cells (font asset must be in the Android package).
6. `src/Noctis.Mobile/Views/LyricsPage.axaml.cs` / `ViewModels/LyricsPageViewModel.cs`:
   only if Lift/Progress update rate needs a frame clock change (check first; likely none).
7. Tests (section 6), then the blueprint OWNER PRIORITY LIST entry and handoff.

Desktop `src/Noctis/Features/LyricMotion/*` and `Controls/MotionWord*.cs` stay untouched.

## 6. Proof: tests and live checks

Unit tests (Noctis.UI tests, headless Avalonia):
- `PlusWordEffect.Pose` is pure: same input, same output; letters rise in order (letter i
  starts no earlier than i-1); rest pose for Progress <= 0 and exactly rest (0 offset,
  scale 1) once done.
- No allocation: run `Pose`/`Mode` 10 000 times, `GC.GetAllocatedBytesForCurrentThread`
  delta is 0.
- KaraokeWordText: a posed still word renders the same transform on consecutive frames
  while a neighbour's Progress crosses the inert window (check the pushed matrix through
  a recording DrawingContext or the internal hooks).

No-jitter measurement (frame by frame vertical motion):
- Headless render the lyrics page (or one line) at phone scale (2.75x) to bitmaps while
  stepping the clock in 16.7 ms frames through a whole line.
- For each word that is not being sung, compute its ink centroid Y (alpha-weighted mean
  row) per frame. Pass: max frame-to-frame change below 0.05 px, and no single step of
  about 0.3 to 0.7 px (the hop signature).
- For the sung word, per letter: the rise curve is monotone (no back-steps beyond
  0.05 px) and the step sizes are even (no 1 px stair pattern), which also catches scale
  stepping from live text.
- Whole line: centroid Y of the line's still words moves by 0 over the line. That is the
  "sentence never shakes" check.

Live checks (owner's rule: DONE only after a live check):
- Android emulator and a real phone: a Latin song, a Persian song (whole-word path, RTL
  fill), a song with held notes. Watch for shake, check sync against the desktop.
- Frame timing: Android GPU profiling bars or `adb shell dumpsys gfxinfo` on the app
  during a line: 60 fps, no janky frames when a new line starts.
- Battery sanity: 10 minutes of lyrics on screen, compare drain with the current page.
