# Review: rev-finder1 (diff origin/android-plus...HEAD)

Scope: 4 files. OnlineMusicSearch.cs moved src/Noctis -> src/Noctis.Core (100% identical), one InternalsVisibleTo added, two docs.

## Findings

1. (Medium, unverified build) src/Noctis.Core/Services/Search/OnlineMusicSearch.cs:1 keeps `namespace Noctis.Services.Search;` and has no `using` lines. It now compiles inside Noctis.Core, so every type it touches must live in Noctis.Core (or be BCL). Any reference to a type that is only in the Noctis app project breaks the Core build. A full `dotnet build` of Noctis.Core was NOT run in this review (deadline); run it before merging.
2. (Low) src/Noctis.Core/Noctis.Core.csproj:21-22 adds `InternalsVisibleTo FreeMusicFinder.Tests`. The assembly name must match exactly (check the test project's AssemblyName); a mismatch only shows as compile errors on `FileSaved` in that test project. If Noctis.Core is strong-named, a public key is also needed.
3. (Low) Namespace `Noctis.Services.Search` now lives in two assemblies (Core and app). Legal, but a duplicate type name in the same namespace in both would be ambiguous; confirm no other OnlineMusicSearch copy remains in src/Noctis.

No logic changes in the diff; the move itself is byte-identical.
