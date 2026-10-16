# Noctis Plus installer. Paste this one line into PowerShell:
#
#     irm https://raw.githubusercontent.com/Alfamin/NoctisPlus/main/scripts/install.ps1 | iex
#
# It downloads the newest Noctis Plus Setup from github.com/Alfamin/NoctisPlus through your VPN/system proxy,
# checks it against the release's SHA256SUMS, installs it for your Windows user (no administrator needed) and
# starts it. On its first start Noctis Plus brings over your whole Noctis profile; the official Noctis stays
# installed and unchanged. An interrupted download continues where it stopped when you run the line again.
#
# Optional, set before running:
#   $env:NOCTISPLUS_SETUP = 'C:\path\NoctisPlus-v1.6.3-Setup.exe'   install a Setup you already downloaded
#   $env:NOCTISPLUS_NO_START = '1'                                  install only, do not start it
#   $env:NOCTISPLUS_RELEASE_API = 'https://...'                      another release feed (testing)
#   $env:NOCTISPLUS_DOWNLOAD_ONLY = '1'                             download and verify only (testing)

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue' # PowerShell 5.1's own progress bar slows downloads a lot
    $Api = if ($env:NOCTISPLUS_RELEASE_API) { $env:NOCTISPLUS_RELEASE_API } else { 'https://api.github.com/repos/Alfamin/NoctisPlus/releases/latest' }
    $Work = Join-Path $env:LOCALAPPDATA 'Noctis Plus Setup'
    $InstalledExe = Join-Path $env:LOCALAPPDATA 'Programs\Noctis Plus\Noctis.exe'

    function Show-Step($text) { Write-Host ''; Write-Host (' NOCTIS PLUS | ' + $text) -ForegroundColor Cyan }

    function Stop-WithError($text) {
        Write-Host ''
        Write-Host (' ' + $text) -ForegroundColor Red
        Write-Host ' Nothing was installed or changed.' -ForegroundColor Red
        throw 'Noctis Plus was not installed.'
    }

    function Explain-Download($exception) {
        while ($exception.InnerException -and -not ($exception -is [Net.WebException])) { $exception = $exception.InnerException }
        $status = $null
        try { $status = [int]$exception.Response.StatusCode } catch { }
        if ($status -eq 404) { return 'NOT_FOUND (HTTP 404): no Noctis Plus release has been published yet.' }
        if ($status -in 401,403) { return "ACCESS_REFUSED (HTTP $status): GitHub refused the request; this connection may be blocked or rate limited. Check your VPN/proxy, or try again later." }
        if ($status -eq 429) { return 'RATE_LIMIT (HTTP 429): too many requests. Try again later.' }
        if ($status -ge 500) { return "SERVER_ERROR (HTTP $status): GitHub is unavailable right now. Try again later." }
        if ($status -ge 400) { return "HTTP_$status`: the file could not be downloaded." }
        $message = [string]$exception.Message
        if ($message -match 'timed out|timeout|canceled|aborted') { return 'TIMEOUT: the connection stopped answering. Check your internet or VPN/proxy, then run the line again; the download continues.' }
        if ($message -match 'name could not be resolved|resolve|No such host') { return 'DNS_FAILED: github.com could not be found. Check DNS, internet access or your VPN.' }
        if ($message -match 'SSL|TLS|certificate|secure channel|trust relationship') { return 'TLS_FAILED: a secure connection to GitHub could not be verified. Check your clock and connection.' }
        if ($message -match 'proxy') { return 'PROXY_FAILED: the system proxy did not answer. Check that your VPN/proxy app is running.' }
        return ('DOWNLOAD_FAILED (' + $exception.GetType().Name + '): check internet access or your VPN/proxy, then run the line again.')
    }

    function New-Request($uri, $method) {
        $request = [Net.HttpWebRequest][Net.WebRequest]::Create($uri)
        $request.Method = $method
        $request.UserAgent = 'NoctisPlus-installer'
        $request.Timeout = 30000
        $request.ReadWriteTimeout = 60000
        $request.AllowAutoRedirect = $true
        # The system proxy (what a VPN app sets) is the default; pass the user's sign-in if it asks for one.
        if ($request.Proxy) { $request.Proxy.Credentials = [Net.CredentialCache]::DefaultNetworkCredentials }
        return $request
    }

    function Get-Text($uri) {
        $request = New-Request $uri 'GET'
        $request.Accept = 'application/vnd.github+json'
        $response = $request.GetResponse()
        try { $reader = New-Object IO.StreamReader($response.GetResponseStream()); return $reader.ReadToEnd() }
        finally { $response.Close() }
    }

    # Downloads into "<file>.partial", continuing a previous partial copy; renames it only when complete.
    function Save-File($uri, $file, $expectedLength) {
        $partial = $file + '.partial'
        for ($attempt = 1; $attempt -le 6; $attempt++) {
            $have = 0
            if (Test-Path -LiteralPath $partial) { $have = (Get-Item -LiteralPath $partial).Length }
            if ($expectedLength -gt 0 -and $have -eq $expectedLength) { break }
            if ($expectedLength -gt 0 -and $have -gt $expectedLength) { $have = 0 }
            try {
                $request = New-Request $uri 'GET'
                if ($have -gt 0) { $request.AddRange([long]$have) }
                $response = [Net.HttpWebResponse]$request.GetResponse()
                try {
                    $append = $have -gt 0 -and [int]$response.StatusCode -eq 206
                    if (-not $append) { $have = 0 }
                    $mode = if ($append) { [IO.FileMode]::Append } else { [IO.FileMode]::Create }
                    $total = if ($expectedLength -gt 0) { $expectedLength } else { $have + $response.ContentLength }
                    $output = New-Object IO.FileStream($partial, $mode, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try {
                        $stream = $response.GetResponseStream()
                        $buffer = New-Object byte[] (1MB)
                        $done = $have
                        $shown = [DateTime]::MinValue
                        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                            $output.Write($buffer, 0, $read)
                            $done += $read
                            if (([DateTime]::UtcNow - $shown).TotalMilliseconds -ge 500 -and $total -gt 0) {
                                $shown = [DateTime]::UtcNow
                                Write-Host ("`r Downloading... {0:N0} of {1:N0} MB ({2}%)   " -f ($done / 1MB), ($total / 1MB), [int](100 * $done / $total)) -NoNewline
                            }
                        }
                    } finally { $output.Close() }
                } finally { $response.Close() }
                Write-Host ''
                # A dropped connection often ends the stream quietly instead of failing: continue from here.
                if ($total -gt 0 -and $done -lt $total) {
                    if ($attempt -eq 6) { break }
                    Write-Host " The connection dropped at $([Math]::Round($done / 1MB)) MB; continuing..." -ForegroundColor Yellow
                    Start-Sleep -Seconds ([Math]::Min(30, 2 * $attempt))
                    continue
                }
                break
            } catch {
                Write-Host ''
                if ($attempt -eq 6) { Stop-WithError (Explain-Download $_.Exception) }
                Write-Host (' ' + (Explain-Download $_.Exception) + " Retrying ($attempt of 5)...") -ForegroundColor Yellow
                Start-Sleep -Seconds ([Math]::Min(30, 3 * $attempt))
            }
        }
        if ($expectedLength -gt 0 -and (Get-Item -LiteralPath $partial).Length -ne $expectedLength) {
            Stop-WithError 'DOWNLOAD_INCOMPLETE: the download ended early. Run the line again; it continues where it stopped.'
        }
        Move-Item -LiteralPath $partial -Destination $file -Force
    }

    function Get-ExpectedHash($sums, $name) {
        foreach ($line in ($sums -split "`r?`n")) {
            if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $name) { return $Matches[1].ToLowerInvariant() }
        }
        return $null
    }

    function Test-Hash($file, $expected) {
        return (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected
    }

    Show-Step 'SETUP'
    if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version -lt [Version]'10.0.17763') {
        Stop-WithError 'Noctis Plus needs 64-bit Windows 10 (version 1809) or Windows 11.'
    }
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    New-Item -ItemType Directory -Force -Path $Work | Out-Null

    if ($env:NOCTISPLUS_SETUP) {
        $setup = (Resolve-Path -LiteralPath $env:NOCTISPLUS_SETUP).Path
        $sumsFile = Join-Path (Split-Path -Parent $setup) 'SHA256SUMS'
        if (Test-Path -LiteralPath $sumsFile) {
            $expected = Get-ExpectedHash (Get-Content -LiteralPath $sumsFile -Raw) (Split-Path -Leaf $setup)
            if (-not $expected -or -not (Test-Hash $setup $expected)) { Stop-WithError 'CHECKSUM_MISMATCH: this Setup does not match the SHA256SUMS next to it.' }
            Write-Host ' Using the Setup you downloaded (checksum OK).'
        } else {
            Write-Host ' Using the Setup you downloaded (no SHA256SUMS next to it to check).' -ForegroundColor Yellow
        }
        $downloaded = $false
    } else {
        Write-Host ' Looking for the newest release...'
        try { $release = Get-Text $Api | ConvertFrom-Json }
        catch { Stop-WithError (Explain-Download $_.Exception) }
        $asset = @($release.assets | Where-Object { $_.name -like 'NoctisPlus-*-Setup.exe' }) | Select-Object -First 1
        $sumsAsset = @($release.assets | Where-Object { $_.name -eq 'SHA256SUMS' }) | Select-Object -First 1
        if (-not $asset) { Stop-WithError "NO_INSTALLER: release $($release.tag_name) has no Noctis Plus Setup." }
        if (-not $sumsAsset) { Stop-WithError "NO_CHECKSUMS: release $($release.tag_name) has no SHA256SUMS, so its Setup cannot be verified." }
        try { $expected = Get-ExpectedHash (Get-Text $sumsAsset.browser_download_url) $asset.name }
        catch { Stop-WithError (Explain-Download $_.Exception) }
        if (-not $expected) { Stop-WithError "NO_CHECKSUMS: SHA256SUMS does not list $($asset.name)." }

        Write-Host " Noctis Plus $($release.tag_name) ($([Math]::Round($asset.size / 1MB)) MB)"
        $setup = Join-Path $Work $asset.name
        if ((Test-Path -LiteralPath $setup) -and (Test-Hash $setup $expected)) {
            Write-Host ' Already downloaded (checksum OK).'
        } else {
            Save-File $asset.browser_download_url $setup ([long]$asset.size)
            if (-not (Test-Hash $setup $expected)) {
                # A damaged copy is set aside (never run) and downloaded once more from the start.
                Move-Item -LiteralPath $setup -Destination ($setup + '.bad') -Force
                Write-Host ' The download did not match its checksum; downloading it again...' -ForegroundColor Yellow
                Save-File $asset.browser_download_url $setup ([long]$asset.size)
                if (-not (Test-Hash $setup $expected)) {
                    Move-Item -LiteralPath $setup -Destination ($setup + '.bad') -Force
                    Stop-WithError 'CHECKSUM_MISMATCH: the download was damaged twice. Check your connection or VPN, then try again.'
                }
            }
            Write-Host ' Download verified.'
        }
        $downloaded = $true
    }

    if ($env:NOCTISPLUS_DOWNLOAD_ONLY -eq '1') { Write-Host " Verified Setup: $setup"; return }

    Show-Step 'INSTALLING'
    Write-Host ' Installing for your user. If Noctis Plus is open, Setup closes it first.'
    $arguments = @('/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-')
    if ($env:NOCTISPLUS_NO_START -eq '1') { $arguments += '/NOLAUNCH=1' }
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        $why = switch ($process.ExitCode) {
            2 { 'Setup was cancelled before it started.' }
            5 { 'Setup was stopped before it finished.' }
            7 { 'Setup could not continue, maybe because Noctis Plus is still open. Close it and run the line again.' }
            default { "Setup stopped with code $($process.ExitCode). Run the line again; the verified download is kept." }
        }
        Write-Host (' ' + $why) -ForegroundColor Red
        throw 'Noctis Plus was not installed.'
    }
    if (-not (Test-Path -LiteralPath $InstalledExe)) { Write-Host " Setup finished, but $InstalledExe was not found." -ForegroundColor Yellow }
    # The verified Setup this script downloaded is not needed any more (one you supplied is left alone).
    if ($downloaded) { Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue }

    Show-Step 'DONE'
    Write-Host ' Noctis Plus is installed: Start menu > Noctis Plus.' -ForegroundColor Green
    if ($env:NOCTISPLUS_NO_START -ne '1') {
        Write-Host ' It is starting now. The first start brings over your Noctis library, settings, stats and sign-ins.'
        Write-Host ' Your official Noctis stays as it is. Close it if the copy asks you to.'
    }
}
