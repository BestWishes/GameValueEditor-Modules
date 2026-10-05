[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9-]+$')] [string]$Game,
    [ValidatePattern('^$|^(0|[1-9][0-9]*)\.[0-9]\.[0-9]$')] [string]$Version = "",
    [string]$OutputDirectory = "",
    [switch]$SkipCatalog,
    [switch]$CatalogOnly,
    [switch]$VerifyReleasedVersion
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot 'versioning.ps1')
if ($CatalogOnly -and $SkipCatalog) { throw 'CatalogOnly and SkipCatalog cannot be used together.' }
if (-not $CatalogOnly -and -not $SkipCatalog) {
    throw 'Build packages with -SkipCatalog. Update catalog.json only after remote verification by using -CatalogOnly.'
}
$repoBoundary = $repoRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$gamesRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "games")) + [System.IO.Path]::DirectorySeparatorChar
$gameRoot = [System.IO.Path]::GetFullPath((Join-Path $gamesRoot $Game))
$buildDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\$Game\build"))
$packageDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\$Game\package"))
$distDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot "dist"))
} else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
$manifestPath = Join-Path $gameRoot "module.json"
if (-not ($gameRoot + [System.IO.Path]::DirectorySeparatorChar).StartsWith(
        $gamesRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $manifestPath)) {
    throw "Unknown game module directory: $Game"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$projectFiles = @(Get-ChildItem -LiteralPath $gameRoot -File -Filter "GameValueEditor.Modules.*.csproj")
if ($projectFiles.Count -ne 1) { throw "$Game must contain exactly one root module project." }
$projectFile = $projectFiles[0].FullName
$assemblyFile = [string]$manifest.assemblyFile
$releaseSlug = [System.IO.Path]::GetFileNameWithoutExtension($assemblyFile) -replace '^GameValueEditor\.Modules\.', ''
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = [string]$manifest.version }
Assert-NextReleaseVersion -RepositoryRoot $repoRoot -Version $Version `
    -TagPattern "$Game-v*" -TagPrefix "$Game-v" `
    -AllowExistingTag:($CatalogOnly -or $VerifyReleasedVersion)
$archiveName = "GameValueEditor.Module.$ReleaseSlug-v$Version.zip"
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $distDir $archiveName))

foreach ($path in @($gameRoot, $buildDir, $packageDir, $distDir, $archivePath)) {
    if (-not [string]::Equals($path, $repoRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $path.StartsWith($repoBoundary, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a path outside the repository: $path"
    }
}

& (Join-Path $PSScriptRoot "validate-modules.ps1") -SkipCatalog

$contributorsPath = Join-Path $gameRoot "contributors.generated.json"
$contributorsDocument = Get-Content -LiteralPath $contributorsPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.version -ne $Version -or $manifest.hostApiVersion -notin @(2, 3, 4, 5, 6, 7) -or
    [string]::IsNullOrWhiteSpace([string]$manifest.minimumHostVersion)) {
    throw "module.json version or Host API does not match the requested package."
}
if ($contributorsDocument.moduleId -ne $manifest.id) {
    throw "contributors.generated.json belongs to another module."
}

if (-not $CatalogOnly) {
    $workingTree = @(& git -C $repoRoot status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $workingTree.Count -ne 0) {
        throw 'Formal module packages must be rebuilt from a clean committed working tree.'
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

    dotnet build $projectFile -c Release -p:ModuleVersion=$Version -o $buildDir
    if ($LASTEXITCODE -ne 0) { throw "$($manifest.id) build failed." }

    $headCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    $productVersion = (Get-Item -LiteralPath (Join-Path $buildDir $assemblyFile)).VersionInfo.ProductVersion
    if ($productVersion -notmatch "\+$([regex]::Escape($headCommit))$") {
        throw "Module ProductVersion is not traceable to HEAD $headCommit`: $productVersion"
    }

    Copy-Item -LiteralPath (Join-Path $buildDir $assemblyFile) -Destination $packageDir
    $packagedManifest = $manifest | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $packagedManifest | Add-Member -NotePropertyName contributors -NotePropertyValue @($contributorsDocument.contributors) -Force
    $packagedManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $packageDir "module.json") -Encoding utf8NoBOM
    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $archivePath -CompressionLevel Optimal
}
elseif (-not (Test-Path -LiteralPath $archivePath)) {
    throw "CatalogOnly requires the already verified release archive: $archivePath"
}

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$sizeBytes = (Get-Item -LiteralPath $archivePath).Length
if (-not $SkipCatalog) {
    $catalogPath = Join-Path $repoRoot "catalog.json"
    $catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $catalog.schemaVersion = 5
    $catalog.hostApiVersion = 6
    $entry = $catalog.modules | Where-Object id -eq $manifest.id | Select-Object -First 1
    if ($null -eq $entry) {
        $entry = [pscustomobject]@{}
        $catalog.modules += $entry
    }
    $existingSnapshots = @()
    if ($null -ne $entry -and $entry.PSObject.Properties.Name -contains 'releases') {
        $existingSnapshots += @($entry.releases)
    }
    if ($entry.PSObject.Properties.Name -contains 'version' -and
        -not [string]::IsNullOrWhiteSpace([string]$entry.version) -and
        [string]$entry.version -ne $Version -and
        -not ($existingSnapshots | Where-Object { [string]$_.version -eq [string]$entry.version })) {
        $legacyMinimumHostVersion = switch ([int]$entry.hostApiVersion) {
            4 { '0.4.1' }
            6 { '0.4.3' }
            7 { '0.4.4' }
            default { throw "Cannot derive minimumHostVersion for published Host API $($entry.hostApiVersion)." }
        }
        $legacyMaximumHostVersion = if ($entry.PSObject.Properties.Name -contains 'maximumHostVersion') {
            $entry.maximumHostVersion
        } else { $null }
        $legacySize = if ($entry.PSObject.Properties.Name -contains 'sizeBytes') { [long]$entry.sizeBytes } else { 0L }
        if ($legacySize -le 0) {
            throw "Published catalog version $($entry.version) is missing sizeBytes; migrate it from the verified GitHub asset first."
        }
        $existingSnapshots += [pscustomobject]@{
            version = [string]$entry.version
            hostApiVersion = [int]$entry.hostApiVersion
            supportsUnlistedBuildValidation = $entry.PSObject.Properties.Name -contains 'supportsUnlistedBuildValidation' -and [bool]$entry.supportsUnlistedBuildValidation
            minimumHostVersion = $legacyMinimumHostVersion
            maximumHostVersion = $legacyMaximumHostVersion
            compatibleBuilds = @($entry.compatibleBuilds)
            editors = @($entry.editors)
            downloadUrl = [string]$entry.downloadUrl
            sizeBytes = $legacySize
            sha256 = [string]$entry.sha256
        }
    }

    foreach ($property in @("id", "legacyIds", "version", "displayName", "gameDisplayName", "description", "hostApiVersion", "minimumHostVersion", "maximumHostVersion", "processNames", "compatibleBuilds", "editors")) {
        if ($manifest.PSObject.Properties.Name -contains $property) {
            $entry | Add-Member -NotePropertyName $property -NotePropertyValue $manifest.$property -Force
        }
    }
    $supportsUnlistedBuildValidation = $manifest.PSObject.Properties.Name -contains 'supportsUnlistedBuildValidation' -and [bool]$manifest.supportsUnlistedBuildValidation
    $entry | Add-Member -NotePropertyName supportsUnlistedBuildValidation -NotePropertyValue $supportsUnlistedBuildValidation -Force
    $entry | Add-Member -NotePropertyName contributors -NotePropertyValue @($contributorsDocument.contributors) -Force
    $downloadUrl = "https://github.com/BestWishes/GameValueEditor-Modules/releases/download/$Game-v$Version/$archiveName"
    $entry | Add-Member -NotePropertyName downloadUrl -NotePropertyValue $downloadUrl -Force
    $entry | Add-Member -NotePropertyName sizeBytes -NotePropertyValue $sizeBytes -Force
    $entry | Add-Member -NotePropertyName sha256 -NotePropertyValue $hash -Force
    $newSnapshot = [pscustomobject]@{
        version = $Version
        hostApiVersion = [int]$manifest.hostApiVersion
        supportsUnlistedBuildValidation = $supportsUnlistedBuildValidation
        minimumHostVersion = [string]$manifest.minimumHostVersion
        maximumHostVersion = if ($manifest.PSObject.Properties.Name -contains 'maximumHostVersion') { $manifest.maximumHostVersion } else { $null }
        compatibleBuilds = @($manifest.compatibleBuilds)
        editors = @($manifest.editors)
        downloadUrl = $downloadUrl
        sizeBytes = $sizeBytes
        sha256 = $hash
    }
    $snapshots = @($newSnapshot) + @($existingSnapshots | Where-Object { [string]$_.version -ne $Version })
    $snapshots = @($snapshots | Sort-Object { [version]$_.version } -Descending | Select-Object -First 3)
    $entry | Add-Member -NotePropertyName releases -NotePropertyValue $snapshots -Force
    $catalog | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $catalogPath -Encoding utf8NoBOM
    Write-Host "Updated: $catalogPath"
}

Write-Host "Created: $archivePath"
Write-Host "SHA256: $hash"
