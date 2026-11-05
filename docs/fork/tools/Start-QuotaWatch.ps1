$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$output = Join-Path $repo 'artifacts\plus\tools'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$script = Join-Path $PSScriptRoot 'QuotaWatch.py'
$pidFile = Join-Path $output 'quota-watch.pid'
if (Test-Path -LiteralPath $pidFile) {
    $watchPid = 0
    if ([int]::TryParse((Get-Content -LiteralPath $pidFile -Raw).Trim(), [ref]$watchPid)) {
        $running = Get-CimInstance Win32_Process -Filter "ProcessId=$watchPid"
        if ($running -and $running.CommandLine.Contains($script)) { 'Quota watch already running'; return }
    }
}
$python = (Get-Command python -ErrorAction Stop).Source
$argsList = @('"' + $script + '"', '--output', '"' + $output + '"')
$watch = Start-Process -FilePath $python -ArgumentList $argsList -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $output 'quota-watch.log') `
    -RedirectStandardError (Join-Path $output 'quota-watch-error.log')
Set-Content -LiteralPath $pidFile -Value $watch.Id
'Quota watch started. Check quota-status.json before each work block; 90% means wrap-up only.'
