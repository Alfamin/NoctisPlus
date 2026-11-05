param(
    [string] $Device = 'emulator-5580',
    [string] $Adb = "$env:USERPROFILE\android-tools\sdk\platform-tools\adb.exe",
    [switch] $ForceOffline
)
$ErrorActionPreference = 'Stop'
# Deliberately restricted to our isolated emulator/package, never an installed or real phone profile.
if ($Device -notmatch '^emulator-\d+$') { throw 'This check only operates on an Android emulator.' }
$package = 'com.alfamin.noctisplus.audit262'
function Invoke-AuditAdb([string[]] $Arguments) {
    $priorErrors = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue' # Windows PowerShell treats adb's harmless stderr as ErrorRecords
        $answer = & $Adb -s $Device @Arguments 2>&1
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $priorErrors }
    if ($code -ne 0) { throw 'The isolated Android check could not complete an adb operation.' }
    return ($answer -join "`n")
}
$cachePath = 'no_backup/finder/for-you-last.json'
$before = (Invoke-AuditAdb @('exec-out','run-as',$package,'cat',$cachePath)) | ConvertFrom-Json
if ($before.Result.Songs.Count -lt 1) { throw 'No persisted picks: generate a page in the audit app first.' }
$proxy = (Invoke-AuditAdb @('shell','settings','get','global','http_proxy')).Trim()
if ($proxy -notmatch '^(null|:0|[a-zA-Z0-9.\[\]:-]+)$') { throw 'Unrecognised emulator proxy; no setting changed.' }
$started = [Diagnostics.Stopwatch]::StartNew()
try {
    if ($ForceOffline) { $null = Invoke-AuditAdb @('shell','settings','put','global','http_proxy','10.0.2.2:1') }
    $null = Invoke-AuditAdb @('shell','am','force-stop',$package)
    $null = Invoke-AuditAdb @('shell','monkey','-p',$package,'1')
    $loaded = ''
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 500
        $appPid = (Invoke-AuditAdb @('shell','pidof',$package)).Trim()
        if ($appPid -notmatch '^\d+$') { continue }
        $logs = Invoke-AuditAdb @('logcat','-d',"--pid=$appPid",'-s','Noctis')
        $loaded = ($logs -split "`n" | Where-Object { $_ -match 'Phone picks loaded: [1-9]\d* songs' } | Select-Object -Last 1)
        if ($loaded) { break }
    }
    if (-not $loaded) { throw 'Persisted picks did not load after a fresh process start.' }
    $after = (Invoke-AuditAdb @('exec-out','run-as',$package,'cat',$cachePath)) | ConvertFrom-Json
    if ($before.Result.Batch -ne $after.Result.Batch) { throw 'The restart replaced the saved page.' }
    $result = [pscustomobject]@{ Passed=$true; ForcedOffline=$ForceOffline.IsPresent; Songs=$after.Result.Songs.Count; WallMs=$started.ElapsedMilliseconds; CheckedUtc=[DateTime]::UtcNow.ToString('o') }
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo 'artifacts\plus\phone-discovery-persistence.json') -Encoding utf8
    $result
} finally {
    if ($ForceOffline) {
        if ($proxy -eq 'null') { $null = Invoke-AuditAdb @('shell','settings','delete','global','http_proxy') }
        else { $null = Invoke-AuditAdb @('shell','settings','put','global','http_proxy',$proxy) }
    }
}
