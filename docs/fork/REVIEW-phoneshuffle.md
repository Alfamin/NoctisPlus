# Review: rev-phoneshuffle vs origin/android-plus-shuffle2

Scope: src/Noctis.Mobile/Services/PhoneShuffle.cs (new, 47 lines). Time-boxed review; ShuffleEngine itself not re-read.

## Findings

1. **Duplicate-key items are silently dropped** - PhoneShuffle.cs:39-43. `byKey.TryAdd` keeps only the first item per `Song.Key`; any later item with the same key (same song twice in a playlist/queue) never reaches `songs` and is missing from the returned list. The result can be shorter than the input. Caller must either accept this or re-append the dropped items.
2. **Null vs empty contract** - PhoneShuffle.cs:46. If `ShuffleEngine.Build` returns an empty or partial plan (e.g. NeverPlayed when everything was seen), `Order` returns an empty/short list, not null. A caller that only checks `null` to fall back to today's order may end up with an empty queue. Document or fall back when the plan is empty.
3. **Plan songs outside the input** - PhoneShuffle.cs:46. The `Where(byKey.ContainsKey)` filter is correct, but `byKey[s.Key]` repeats the lookup; use `TryGetValue` (minor). If the plan repeats a key, the item is emitted twice (no de-dup on output).
4. **`current` / `last` not checked against `items`** - PhoneShuffle.cs:33,45. Passed through unchanged; fine if the engine tolerates unknown keys - worth a test.
5. `Visible` (line 18) duplicates the full enum order; low risk, but adding a mode later needs two edits.

No compile issues spotted (enum, switch, generic signature look correct). Not wired into UI yet (#255 step 2), so no runtime impact today.
