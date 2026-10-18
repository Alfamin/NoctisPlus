# Copy beside app/ and profile/ in the isolated test folder. Never copies profile data.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PublishDirectory,
    [string] $TestRoot
)

$ErrorActionPreference = 'Stop'
try {
    if ([string]::IsNullOrWhiteSpace($TestRoot)) { $TestRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
    $root = [IO.Path]::GetFullPath($TestRoot).TrimEnd('\')
    $source = (Resolve-Path -LiteralPath $PublishDirectory).ProviderPath.TrimEnd('\')
    $app = [IO.Path]::GetFullPath((Join-Path $root 'app'))
    if (!(Test-Path -LiteralPath (Join-Path $source 'Noctis.exe') -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $source 'Noctis.dll') -PathType Leaf)) {
        throw 'Publish folder is incomplete.'
    }
    if ($source.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($source, $root, [StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Keep the publish folder separate from the test folder.'
    }
    if (!(Test-Path -LiteralPath (Join-Path $root 'Start-NoctisPlus-Test.ps1') -PathType Leaf)) {
        throw 'Choose the isolated test folder containing Start-NoctisPlus-Test.ps1.'
    }
    if (Test-Path -LiteralPath $app) {
        if (((Get-Item -LiteralPath $app).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The test app folder must not be a link.'
        }
    }
    function Assert-AppClosed {
        $running = @(Get-Process Noctis -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -and [string]::Equals($_.Path, (Join-Path $app 'Noctis.exe'), [StringComparison]::OrdinalIgnoreCase)
        })
        if ($running.Count -gt 0) { throw 'Close the Noctis Plus test app, then try again.' }
    }
    Assert-AppClosed
    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + $PID
    $next = [IO.Path]::GetFullPath((Join-Path $root "app-next-$stamp"))
    $previous = [IO.Path]::GetFullPath((Join-Path $root "app-previous-$stamp"))
    # Verify absolute move targets before touching an existing app. No recursive deletion.
    foreach ($target in @($app, $next, $previous)) {
        if (![string]::Equals((Split-Path -Parent $target), $root, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'App swap target escaped the test folder.'
        }
    }
    Copy-Item -LiteralPath $source -Destination $next -Recurse
    foreach ($file in @('Noctis.exe', 'Noctis.dll')) {
        if ((Get-FileHash -LiteralPath (Join-Path $source $file)).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $next $file)).Hash) { throw "Copy verification failed: $file" }
    }
    Assert-AppClosed
    $movedOld = $false
    try {
        if (Test-Path -LiteralPath $app) {
            Move-Item -LiteralPath $app -Destination $previous
            $movedOld = $true
        }
        Move-Item -LiteralPath $next -Destination $app
    }
    catch {
        if ($movedOld -and !(Test-Path -LiteralPath $app)) {
            Move-Item -LiteralPath $previous -Destination $app
        }
        throw
    }
    Write-Host 'Test app updated. Profile kept.'
    if ($movedOld) { Write-Host "Previous app: $previous" }
}
catch { Write-Host "Could not update: $($_.Exception.Message)"; exit 1 }
