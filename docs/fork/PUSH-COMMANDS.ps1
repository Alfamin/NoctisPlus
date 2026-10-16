# Run this yourself after creating an EMPTY Alfamin/NoctisPlus GitHub repository.
# Commits and pushes only the reviewed preview files. No release ZIP is put in Git.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location -LiteralPath $repoRoot
if ((git branch --show-current) -ne 'main') { throw 'Switch to the reviewed main branch first.' }
$authorName = git config --get user.name
$authorEmail = git config --get user.email
if ([string]::IsNullOrWhiteSpace($authorName) -or [string]::IsNullOrWhiteSpace($authorEmail)) {
    throw 'Commit author is missing. In this repository, set git config --local user.name and git config --local user.email, then rerun. No files were staged or pushed by this run.'
}
$files = @(
    '.gitattributes',
    '.github/workflows/deploy-website.yml',
    '.github/workflows/dotnet.yml',
    '.github/workflows/noctis-plus.yml',
    '.github/workflows/package-managers.yml',
    'README.md',
    'bundled-plugins/README.md',
    'bundled-plugins/dev.moshi.freemusicfinder/FreeMusicFinder.deps.json',
    'bundled-plugins/dev.moshi.freemusicfinder/FreeMusicFinder.dll',
    'bundled-plugins/dev.moshi.freemusicfinder/WTelegramClient.dll',
    'bundled-plugins/dev.moshi.freemusicfinder/plugin.json',
    'bundled-plugins/dev.moshi.lyricmotion/LyricMotion.dll',
    'bundled-plugins/dev.moshi.lyricmotion/plugin.json',
    'bundled-plugins/dev.moshi.trueshuffle/TrueShuffle.dll',
    'bundled-plugins/dev.moshi.trueshuffle/plugin.json',
    'bundled-plugins/dev.moshi.wordlyrics/WordLyricsForNoctis.dll',
    'bundled-plugins/dev.moshi.wordlyrics/plugin.json',
    'bundled-plugins/manifest.json',
    'docs/fork/LOCAL-PREVIEW-2026-10-16.md',
    'docs/fork/PROVENANCE.md',
    'docs/fork/PUSH-COMMANDS.ps1',
    'docs/fork/QUICKSTART.md',
    'installer.iss',
    'scripts/Build-NoctisPlus.ps1',
    'src/Noctis.Core/Helpers/AppPaths.cs',
    'src/Noctis.Core/Helpers/ProductIdentity.cs',
    'src/Noctis.Core/Models/AppSettings.cs',
    'src/Noctis.Core/Services/PersistenceService.cs',
    'src/Noctis/Helpers/SingleInstanceGuard.cs',
    'src/Noctis/Helpers/StartupHelper.cs',
    'src/Noctis/Helpers/WindowsFileAssociations.cs',
    'src/Noctis/Noctis.csproj',
    'src/Noctis/Program.cs',
    'src/Noctis/Properties/launchSettings.json',
    'src/Noctis/Services/ArtistInfoService.cs',
    'src/Noctis/Services/AutoUpdatePolicy.cs',
    'src/Noctis/Services/LastFmService.cs',
    'src/Noctis/Services/LrcLibService.cs',
    'src/Noctis/Services/MetadataSearch/ProviderHttp.cs',
    'src/Noctis/Services/Plugins/PluginHost.cs',
    'src/Noctis/Services/UpdateService.cs',
    'src/Noctis/Services/VlcAudioPlayer.cs',
    'src/Noctis/ViewModels/MainWindowViewModel.cs',
    'src/Noctis/ViewModels/SettingsViewModel.cs',
    'src/Noctis/Views/MainWindow.axaml',
    'src/Noctis/Views/MainWindow.axaml.cs',
    'src/Noctis/Views/SettingsView.axaml',
    'src/Noctis/app.manifest',
    'tests/Noctis.Tests/AutoUpdatePolicyTests.cs',
    'tests/Noctis.Tests/AutoUpdateServiceTests.cs',
    'tests/Noctis.Tests/AutoUpdateViewModelTests.cs',
    'tests/Noctis.Tests/BundledPluginTests.cs',
    'tests/Noctis.Tests/ForkUpdateFeedTests.cs',
    'tests/Noctis.Tests/LastFmSessionRetentionTests.cs',
    'tests/Noctis.Tests/MetadataFinderServiceTests.cs',
    'tests/Noctis.Tests/MetadataSearchEngineTests.cs',
    'tests/Noctis.Tests/PersistenceServiceTests.cs',
    'tests/Noctis.Tests/ProductIdentityTests.cs',
    'tests/Noctis.Tests/SettingsViewModelPersistenceTests.cs',
    'tests/Noctis.Tests/TestAssemblyInit.cs',
    'tests/Noctis.Tests/UpdateDownloadStallTests.cs'
)
$unrelated = @(git diff --cached --name-only | Where-Object { $_ -notin $files })
if ($unrelated.Count) { throw 'Other files are staged. Commit or unstage those separately first; nothing was changed.' }
$origin = $null
if (@(git remote) -contains 'origin') {
    $origin = git remote get-url origin
    if ($LASTEXITCODE -ne 0) { throw 'Could not read origin.' }
}
if (!$origin) {
    git remote add origin https://github.com/Alfamin/NoctisPlus.git
    if ($LASTEXITCODE -ne 0) { throw 'Could not add origin.' }
} elseif ($origin -notin @('https://github.com/Alfamin/NoctisPlus.git','https://github.com/Alfamin/NoctisPlus','git@github.com:Alfamin/NoctisPlus.git')) {
    throw 'Origin points somewhere else. Review it before pushing.'
}
git add -- $files
if ($LASTEXITCODE -ne 0) { throw 'Staging failed; nothing was committed or pushed.' }
# Reapply current attributes even to files staged by an earlier run.
git add --renormalize -- $files
if ($LASTEXITCODE -ne 0) { throw 'Line-ending normalization failed; nothing was committed or pushed.' }
git --no-pager diff --cached --stat
git diff --cached --quiet
if ($LASTEXITCODE -eq 1) {
    git commit -m "Build isolated Noctis Plus preview with bundled features and Last.fm recovery"
    if ($LASTEXITCODE -ne 0) { throw 'Commit failed; nothing was pushed.' }
}
git push -u origin main
if ($LASTEXITCODE -ne 0) { throw 'Push failed. The local commit is kept. Check repository/access and retry git push -u origin main.' }
