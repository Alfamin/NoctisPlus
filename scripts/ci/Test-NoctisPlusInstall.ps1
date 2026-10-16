# CI only (a throwaway GitHub Windows runner, never a user's PC): installs the freshly built Noctis Plus Setup through
# scripts\install.ps1, checks it leaves an official Noctis install and profile alone, starts the installed Release app
# once over a synthetic official profile to prove the automatic import, reinstalls over itself, then uninstalls and
# checks the Plus profile is kept. Windows PowerShell 5.1 compatible.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Setup)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$failures = New-Object Collections.Generic.List[string]
function Check($ok, $what) {
    if ($ok) { Write-Host "  ok   $what" -ForegroundColor Green } else { Write-Host "  FAIL $what" -ForegroundColor Red; $failures.Add($what) }
}
function Fingerprint($root) {
    if (!(Test-Path -LiteralPath $root)) { return '' }
    (Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($root.Length) + '|' + $_.Length + '|' + $_.LastWriteTimeUtc.Ticks + '|' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n"
}
function Wait-For($condition, $seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ([DateTime]::UtcNow -lt $deadline) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 500 }
    return [bool](& $condition)
}

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Noctis Plus'
$exe = Join-Path $installDir 'Noctis.exe'
$plusKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{46F93B65-9A61-4601-8CF8-365E785E34A0}_is1'
$officialKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E8A3B5F1-7C2D-4A9E-B6F0-1D3E5A7C9B2F}_is1'
$officialDir = Join-Path $env:LOCALAPPDATA 'Programs\Noctis'
$officialProfile = Join-Path $env:APPDATA 'Noctis'
$plusProfile = Join-Path $env:APPDATA 'Noctis Plus'

Write-Host '== a stand-in official Noctis install and profile'
New-Item -ItemType Directory -Force -Path $officialDir | Out-Null
[IO.File]::WriteAllBytes((Join-Path $officialDir 'Noctis.exe'), [byte[]](1..64))
New-Item -Path $officialKey -Force | Out-Null
Set-ItemProperty -Path $officialKey -Name DisplayName -Value 'Noctis'
New-Item -ItemType Directory -Force -Path (Join-Path $officialProfile 'plugin-data\dev.moshi.freemusicfinder'), (Join-Path $officialProfile 'artwork') | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $officialProfile 'settings.json'), '{ "musicFolders": ["D:\\music-ci"], "webRemotePort": 9420, "localApiPort": 9421, "lastFmSessionKey": "ci-session", "profileName": "CI" }', $utf8)
[IO.File]::WriteAllText((Join-Path $officialProfile 'library.json'), '[]', $utf8)
[IO.File]::WriteAllText((Join-Path $officialProfile 'playlists.json'), '[ { "id": "p1", "name": "CI list", "trackIds": [] } ]', $utf8)
[IO.File]::WriteAllText((Join-Path $officialProfile 'play_history.json'), '[]', $utf8)
$secret = New-Object byte[] 4096; (New-Object Random 7).NextBytes($secret)
[IO.File]::WriteAllBytes((Join-Path $officialProfile 'plugin-data\dev.moshi.freemusicfinder\telegram.dat'), $secret)
[IO.File]::WriteAllBytes((Join-Path $officialProfile 'artwork\a.jpg'), $secret)
& python -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.execute('pragma journal_mode=wal'); c.execute('create table track_user_state(id text primary key, play_count int)'); c.executemany('insert into track_user_state values(?,?)', [('t%d' % i, i) for i in range(20)]); c.commit(); c.close()" (Join-Path $officialProfile 'library.db')
$officialBefore = (Fingerprint $officialDir) + (Fingerprint $officialProfile)

Write-Host '== install through scripts\install.ps1 (the verified local Setup)'
$env:NOCTISPLUS_SETUP = $Setup
$env:NOCTISPLUS_NO_START = '1'
Invoke-Expression (Get-Content -LiteralPath (Join-Path $repoRoot 'scripts\install.ps1') -Raw)
Check (Test-Path -LiteralPath $exe) 'Noctis.exe installed per user'
Check (Test-Path -LiteralPath $plusKey) 'own uninstall entry'
Check ((Get-ItemProperty -Path $plusKey).DisplayName -like 'Noctis Plus*') 'uninstall entry is named Noctis Plus'
Check (Test-Path -LiteralPath 'HKCU:\Software\Classes\NoctisPlus.AudioFile') 'own file-type registration'
Check (Test-Path -LiteralPath (Join-Path $installDir 'bundled-plugins\manifest.json')) 'the four features are bundled'
Check (!(Test-Path -LiteralPath $plusProfile)) 'installing alone does not create or import a profile'
Check (@(Get-Process -Name Noctis -ErrorAction SilentlyContinue).Count -eq 0) '/NOLAUNCH=1 left it closed'

Write-Host '== first start: the official profile comes over by itself'
$state = Join-Path $plusProfile '.noctis-import\state.json'
$app = Start-Process -FilePath $exe -PassThru
$imported = Wait-For { (Test-Path -LiteralPath $state) -and ((Get-Content -LiteralPath $state -Raw) -match '"Phase":\s*"Done"') } 120
Check $imported 'import finished (state Done)'
if ($imported) {
    $settings = Get-Content -LiteralPath (Join-Path $plusProfile 'settings.json') -Raw | ConvertFrom-Json
    Check ($settings.webRemotePort -eq 9430 -and $settings.localApiPort -eq 9431) 'ports moved to Plus defaults'
    Check ($settings.lastFmSessionKey -eq 'ci-session') 'Last.fm sign-in carried'
    Check ((Get-FileHash (Join-Path $plusProfile 'plugin-data\dev.moshi.freemusicfinder\telegram.dat')).Hash -eq (Get-FileHash (Join-Path $officialProfile 'plugin-data\dev.moshi.freemusicfinder\telegram.dat')).Hash) 'Telegram session carried'
    Check (Test-Path -LiteralPath (Join-Path $plusProfile 'library.db')) 'library database carried'
    Check ((Get-Content -LiteralPath (Join-Path $plusProfile 'playlists.json') -Raw) -match 'CI list') 'playlists carried'
}
Check (Wait-For { -not $app.HasExited } 5) 'the player stays open after the import'
Start-Sleep -Seconds 5
Get-Process -Name Noctis -ErrorAction SilentlyContinue | Stop-Process -Force
Wait-For { @(Get-Process -Name Noctis -ErrorAction SilentlyContinue).Count -eq 0 } 20 | Out-Null
Check (((Fingerprint $officialDir) + (Fingerprint $officialProfile)) -eq $officialBefore) 'official install and profile unchanged'

Write-Host '== reinstall over itself'
$plusBefore = Fingerprint $plusProfile
Invoke-Expression (Get-Content -LiteralPath (Join-Path $repoRoot 'scripts\install.ps1') -Raw)
Check (Test-Path -LiteralPath $exe) 'still installed after reinstall'
Check ((Fingerprint $plusProfile) -eq $plusBefore) 'reinstall leaves the Plus profile alone'

Write-Host '== uninstall'
$uninstaller = Join-Path $installDir 'unins000.exe'
$p = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru
Check ($p.ExitCode -eq 0) 'uninstaller exit code 0'
Check (Wait-For { -not (Test-Path -LiteralPath $exe) } 60) 'program removed'
Check (!(Test-Path -LiteralPath $plusKey)) 'uninstall entry removed'
Check (Test-Path -LiteralPath (Join-Path $plusProfile 'settings.json')) 'Plus profile kept after uninstall'
Check (((Fingerprint $officialDir) + (Fingerprint $officialProfile)) -eq $officialBefore) 'official install and profile still unchanged'
Check (Test-Path -LiteralPath $officialKey) 'official uninstall entry untouched'

if ($failures.Count) { throw "$($failures.Count) install check(s) failed: $($failures -join '; ')" }
Write-Host 'All install checks passed.' -ForegroundColor Green
