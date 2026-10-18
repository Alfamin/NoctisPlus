# Standalone launcher: copy beside app\ and profile\ in the isolated test folder.
[CmdletBinding()]
param(
    [string] $TestRoot,
    [switch] $FreshProfile,
    [ValidateRange(1, 60)] [int] $StartupTimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$launchLog = $null

function Write-LaunchStatus([string] $Message) {
    Write-Host $Message
    if ($launchLog) {
        try { [IO.File]::AppendAllText($launchLog, "$(Get-Date -Format o) $Message`r`n", $utf8) }
        catch { Write-Host "Could not write launch log: $($_.Exception.Message)" }
    }
}

try {
    # Windows PowerShell 5.1 can bind parameter defaults before PSScriptRoot is
    # populated for a -File launch. Resolve it in the body, never in param().
    if ([string]::IsNullOrWhiteSpace($TestRoot)) { $TestRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
    $root = [IO.Path]::GetFullPath($TestRoot)
    $logs = Join-Path $root 'launch-logs'
    [IO.Directory]::CreateDirectory($logs) | Out-Null
    $run = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + $PID
    $launchLog = Join-Path $logs "$run.log"
    $exe = Join-Path $root 'app\Noctis.exe'
    if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Test app missing: $exe. Run Update-TestBuild.ps1."
    }

    # Never run two profiles from this app folder at once: a repeat launch should
    # use Noctis's activation pipe rather than starting another Telegram owner.
    $existing = @(Get-Process -Name Noctis -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and [string]::Equals($_.Path, $exe, [StringComparison]::OrdinalIgnoreCase) })
    if ($FreshProfile -and $existing.Count -gt 0) {
        throw 'Close the test app before opening a fresh profile.'
    }

    $profileDir = Join-Path $root 'profile'
    if ($FreshProfile) { $profileDir = Join-Path $root "fresh-profiles\$run" }
    [IO.Directory]::CreateDirectory($profileDir) | Out-Null
    Write-LaunchStatus 'Opening Noctis Plus test...'
    Write-LaunchStatus "Profile: $profileDir"

    # Process-scoped only. Clear an inherited profile-import request and restore
    # the calling terminal's environment even if CreateProcess fails.
    $oldProfile = [Environment]::GetEnvironmentVariable('NOCTIS_PLUS_DATA_DIR', 'Process')
    $oldImport = [Environment]::GetEnvironmentVariable('NOCTIS_PLUS_IMPORT_FROM', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('NOCTIS_PLUS_DATA_DIR', $profileDir, 'Process')
        [Environment]::SetEnvironmentVariable('NOCTIS_PLUS_IMPORT_FROM', $null, 'Process')
        # Redirection uses CreateProcess rather than file associations / shell
        # activation, and retains native host errors that precede session.log.
        $child = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru `
            -RedirectStandardOutput (Join-Path $logs "$run.stdout.log") `
            -RedirectStandardError (Join-Path $logs "$run.stderr.log")
        # Retain the handle before a very short-lived native host exits (PS 5.1).
        $null = $child.Handle
    }
    finally {
        [Environment]::SetEnvironmentVariable('NOCTIS_PLUS_DATA_DIR', $oldProfile, 'Process')
        [Environment]::SetEnvironmentVariable('NOCTIS_PLUS_IMPORT_FROM', $oldImport, 'Process')
    }

    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $child.Refresh()
        if (!$child.HasExited -and $child.MainWindowHandle -ne [IntPtr]::Zero) {
            Write-LaunchStatus "Opened (PID $($child.Id))."
            exit 0
        }
        if ($child.HasExited) {
            # A second launch exits after asking the first instance to surface.
            $live = @(Get-Process -Name Noctis -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -and [string]::Equals($_.Path, $exe, [StringComparison]::OrdinalIgnoreCase) -and $_.MainWindowHandle -ne 0 })
            if ($child.ExitCode -eq 0 -and $existing.Count -gt 0 -and $live.Count -gt 0) {
                Write-LaunchStatus "Already open (PID $($live[0].Id))."
                exit 0
            }
            throw "Noctis exited before opening a window (exit $($child.ExitCode))."
        }
        Start-Sleep -Milliseconds 200
    } while ($clock.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds)

    # Do not kill a slow startup or launch again: its session may be connecting.
    Write-LaunchStatus "Still starting (PID $($child.Id)); no window after $StartupTimeoutSeconds seconds."
    Write-LaunchStatus "Details: $launchLog"
    exit 2
}
catch {
    Write-LaunchStatus "Could not open: $($_.Exception.Message)"
    if ($launchLog) { Write-Host "Details: $launchLog" }
    exit 1
}
