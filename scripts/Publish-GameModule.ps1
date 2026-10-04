[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$GameDirectory,
    [Parameter(Mandatory)] [string]$ProjectFile,
    [Parameter(Mandatory)] [string]$AssemblyFile,
    [Parameter(Mandatory)] [string]$ReleaseSlug,
    [Parameter(Mandatory)] [string]$Version,
    [switch]$SkipCatalog
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$gameRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "games\$GameDirectory"))
$buildDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\$GameDirectory\build"))
$packageDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\$GameDirectory\package"))
$distDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "dist"))
$archiveName = "GameValueEditor.Module.$ReleaseSlug-v$Version.zip"
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $distDir $archiveName))

foreach ($path in @($gameRoot, $buildDir, $packageDir, $distDir, $archivePath)) {
    if (-not $path.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a path outside the repository: $path"
    }
}
foreach ($path in @($buildDir, $packageDir)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Path $buildDir, $packageDir, $distDir -Force | Out-Null

$retainedArchiveNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($sourceGameDirectory in Get-ChildItem -LiteralPath (Join-Path $repoRoot "games") -Directory) {
    $sourceManifestPath = Join-Path $sourceGameDirectory.FullName "module.json"
    if (-not (Test-Path -LiteralPath $sourceManifestPath)) { continue }
    $sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $sourceSlug = [System.IO.Path]::GetFileNameWithoutExtension([string]$sourceManifest.assemblyFile) `
        -replace '^GameValueEditor\.Modules\.', ''
    $null = $retainedArchiveNames.Add("GameValueEditor.Module.$sourceSlug-v$($sourceManifest.version).zip")
}
foreach ($oldArchive in Get-ChildItem -LiteralPath $distDir -File -Filter "GameValueEditor.Module.*.zip") {
    if (-not $retainedArchiveNames.Contains($oldArchive.Name)) {
        Remove-Item -LiteralPath $oldArchive.FullName -Force
    }
}

$manifestPath = Join-Path $gameRoot "module.json"
$contributorsPath = Join-Path $gameRoot "contributors.generated.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$contributorsDocument = Get-Content -LiteralPath $contributorsPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.version -ne $Version -or $manifest.hostApiVersion -notin @(2, 3)) {
    throw "module.json version or Host API does not match the requested package."
}
if ($contributorsDocument.moduleId -ne $manifest.id) {
    throw "contributors.generated.json belongs to another module."
}

dotnet build (Join-Path $gameRoot $ProjectFile) -c Release -p:ModuleVersion=$Version -o $buildDir
if ($LASTEXITCODE -ne 0) { throw "$($manifest.id) build failed." }

Copy-Item -LiteralPath (Join-Path $buildDir $AssemblyFile) -Destination $packageDir
$packagedManifest = $manifest | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$packagedManifest | Add-Member -NotePropertyName contributors -NotePropertyValue @($contributorsDocument.contributors) -Force
$packagedManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $packageDir "module.json") -Encoding utf8NoBOM
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $archivePath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if (-not $SkipCatalog) {
    $catalogPath = Join-Path $repoRoot "catalog.json"
    $catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $catalog.schemaVersion = 3
    $catalog.hostApiVersion = 3
    $entry = $catalog.modules | Where-Object id -eq $manifest.id | Select-Object -First 1
    if ($null -eq $entry) {
        $entry = [pscustomobject]@{}
        $catalog.modules += $entry
    }
    foreach ($property in @("id", "legacyIds", "version", "displayName", "gameDisplayName", "description", "hostApiVersion", "processNames", "compatibleBuilds", "editors")) {
        if ($manifest.PSObject.Properties.Name -contains $property) {
            $entry | Add-Member -NotePropertyName $property -NotePropertyValue $manifest.$property -Force
        }
    }
    $entry | Add-Member -NotePropertyName contributors -NotePropertyValue @($contributorsDocument.contributors) -Force
    $entry | Add-Member -NotePropertyName downloadUrl -NotePropertyValue "https://github.com/BestWishes/GameValueEditor-Modules/releases/download/$GameDirectory-v$Version/$archiveName" -Force
    $entry | Add-Member -NotePropertyName sha256 -NotePropertyValue $hash -Force
    $catalog | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $catalogPath -Encoding utf8NoBOM
    Write-Host "Updated: $catalogPath"
}

Write-Host "Created: $archivePath"
Write-Host "SHA256: $hash"
