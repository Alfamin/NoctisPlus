# A local, offline build using an already-restored .NET 10 SDK/cache. No installation or profile migration.
[CmdletBinding()]
param(
    [string]$DotNet = 'dotnet',
    [switch]$Installer,
    [string]$InnoCompiler = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$moduleRoot = Join-Path $repoRoot 'bundled-plugins'
$manifest = Get-Content -LiteralPath (Join-Path $moduleRoot 'manifest.json') -Raw | ConvertFrom-Json
foreach ($module in $manifest.modules) {
    $folder = Join-Path $moduleRoot $module.id
    foreach ($file in $module.files.PSObject.Properties) {
        $path = Join-Path $folder $file.Name
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Value) {
            throw "Bundled file is missing or changed: $($module.id)/$($file.Name). Refresh the pinned package before building."
        }
    }
}
if (!(Get-Command $DotNet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK was not found. Pass -DotNet with the full path to dotnet.exe.'
}
$publishRoot = Join-Path $repoRoot ('publish\plus-win-x64-' + [Guid]::NewGuid().ToString('N'))
$outputRoot = Join-Path $repoRoot 'release-artifacts'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
& $DotNet publish (Join-Path $repoRoot 'src\Noctis\Noctis.csproj') -c Release -r win-x64 --self-contained true --no-restore '-p:PublishReadyToRun=false' -o $publishRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed. No package was produced. This offline command never restores missing SDK packages automatically.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\fork\PROVENANCE.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\fork\QUICKSTART.md') -Destination $publishRoot
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $outputRoot 'NoctisPlus-windows-x64.zip'
# Build to a new file first, so an interrupted build leaves the previous package intact.
$stagedZip = Join-Path $outputRoot ('NoctisPlus-' + [Guid]::NewGuid().ToString('N') + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($publishRoot, $stagedZip, [IO.Compression.CompressionLevel]::Optimal, $false)
Move-Item -LiteralPath $stagedZip -Destination $zip -Force
$assets = @($zip)
if ($Installer) {
    if (!$InnoCompiler) { $InnoCompiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
    if (!(Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
        throw "Portable ZIP is ready at $zip. Inno Setup is not installed; pass -InnoCompiler to build the optional installer."
    }
    & $InnoCompiler "/DPublishDir=$publishRoot" (Join-Path $repoRoot 'installer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed; the portable ZIP is still available.' }
    [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'src\Noctis\Noctis.csproj') -Raw
    $version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $setup = Join-Path $repoRoot "installer-output\NoctisPlus-v$version-Setup.exe"
    Copy-Item -LiteralPath $setup -Destination $outputRoot -Force
    $assets += Join-Path $outputRoot (Split-Path -Leaf $setup)
}
$lines = foreach ($asset in $assets) {
    (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path -Leaf $asset)
}
[IO.File]::WriteAllLines((Join-Path $outputRoot 'SHA256SUMS'), [string[]]$lines, (New-Object Text.UTF8Encoding($false)))
Write-Host "Noctis Plus package ready: $zip" -ForegroundColor Green
Write-Host "Build folder: $publishRoot"
