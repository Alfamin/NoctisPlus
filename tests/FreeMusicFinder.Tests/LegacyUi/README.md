# Legacy Finder UI regression fixtures

These controls are compiled only into the test executable. Noctis Plus no longer ships
Finder's window, theme, discovery panel, playlist panel or queue panel. Their existing
checks remain here while native replacements gain equivalent integration coverage.

Do not instantiate these from production code or restore them as a fallback window.
New user-facing behavior belongs in Noctis's own views and view models.
