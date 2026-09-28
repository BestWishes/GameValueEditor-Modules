[CmdletBinding()]
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$buildDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\worldapart\build"))
$packageDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\worldapart\package"))
$distDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "dist"))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $distDir "GameValueEditor.Module.WorldApart-v$Version.zip"))

foreach ($path in @($buildDir, $packageDir)) {
    if (-not $path.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the repository: $path"
    }
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

dotnet build (Join-Path $repoRoot "games\worldapart\GameValueEditor.Modules.WorldApart.csproj") `
    -c Release `
    -p:ModuleVersion=$Version `
    -o $buildDir
if ($LASTEXITCODE -ne 0) { throw "WorldApart module build failed." }

$manifestPath = Join-Path $repoRoot "games\worldapart\module.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.id -ne "game.worldapart" -or $manifest.version -ne $Version -or $manifest.hostApiVersion -ne 2) {
    throw "module.json does not match game.worldapart v$Version / Host API v2."
}

Copy-Item -LiteralPath (Join-Path $buildDir "GameValueEditor.Modules.WorldApart.dll") -Destination $packageDir
Copy-Item -LiteralPath $manifestPath -Destination $packageDir
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $archivePath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
Write-Host "Created: $archivePath"
Write-Host "SHA256: $hash"
