# Review: rev-shuffle2 (#255 True Shuffle engine into Noctis.Core)

Scope: `git diff origin/android-plus...HEAD` (9 files: 6 pure renames src/Noctis -> src/Noctis.Core, ShuffleScope enum moved, InternalsVisibleTo Noctis.Mobile, doc). Time-boxed review; no build was run.

## Findings

1. (Medium, unverified) Build not run for any head after the move. The desktop project still owns `src/Noctis/Features/TrueShuffle/ShuffleQueue.cs` (uses `ShuffleScope`, now in `src/Noctis.Core/Features/TrueShuffle/RollingQueue.cs:7`). It resolves only via `InternalsVisibleTo Include="Noctis"` (`src/Noctis.Core/Noctis.Core.csproj:19`) and the same namespace `Noctis.Features.TrueShuffle`, which both hold. Risk: if `Noctis.Tests` or any other project compiled these files by path link, they are now double-defined. Run a full `dotnet build` of the solution plus the tests before merging.
2. (Low) `src/Noctis.Core/Features/TrueShuffle/FileIdentity.cs:39,42` keeps kernel32 `DllImport`s in Core, which now ships to Android. Safe: `FileIdentity.cs:20` returns null when not Windows, so the import is never resolved on the phone. On Android, file identity therefore falls back to path only; renamed/moved files lose shuffle history there. Document or add a POSIX inode path later.
3. (Low) `src/Noctis.Core/Noctis.Core.csproj:27` adds `InternalsVisibleTo Noctis.Mobile`; confirm the phone assembly is actually named `Noctis.Mobile` (the existing Android entry at line 25 is `Noctis.Android`). A wrong name fails silently until the phone project compiles against the internals.
4. (Info) `RollingQueue.cs:2` `using Noctis.Models` requires `Track`/`Song` to live in Core; they do (moved file compiles only if so); covered by finding 1.

No logic changes in the engine files (similarity 100%), so no behaviour regressions in the shuffle itself.