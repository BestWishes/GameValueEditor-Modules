[CmdletBinding()]
param([switch]$SkipCatalog)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot 'versioning.ps1')
Assert-ReleaseVersionContract
$gamesRoot = Join-Path $repoRoot "games"
$schemasRoot = Join-Path $repoRoot "schemas"
$moduleIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$manifests = @{}
$contributorsByModule = @{}
$directoriesByModule = @{}

function Assert-JsonSchema {
    param(
        [Parameter(Mandatory)] [string]$JsonPath,
        [Parameter(Mandatory)] [string]$SchemaPath
    )
    $json = Get-Content -LiteralPath $JsonPath -Raw -Encoding UTF8
    if (-not ($json | Test-Json -SchemaFile $SchemaPath -ErrorAction Stop)) {
        throw "$JsonPath does not satisfy $SchemaPath."
    }
}

function ConvertTo-ComparableJson {
    param($Value)
    return ($Value | ConvertTo-Json -Depth 30 -Compress)
}

foreach ($directory in Get-ChildItem -LiteralPath $gamesRoot -Directory | Sort-Object Name) {
    $manifestPath = Join-Path $directory.FullName "module.json"
    $contributorsPath = Join-Path $directory.FullName "contributors.generated.json"
    if (-not (Test-Path -LiteralPath $manifestPath) -or -not (Test-Path -LiteralPath $contributorsPath)) {
        throw "$($directory.Name) must contain module.json and contributors.generated.json."
    }
    Assert-JsonSchema -JsonPath $manifestPath -SchemaPath (Join-Path $schemasRoot "module.schema.json")
    Assert-JsonSchema -JsonPath $contributorsPath -SchemaPath (Join-Path $schemasRoot "contributors.schema.json")

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $gameName = $directory.Name
    $null = ConvertTo-ReleaseVersion ([string]$manifest.version)
    Assert-NextReleaseVersion -RepositoryRoot $repoRoot -Version ([string]$manifest.version) `
        -TagPattern "$gameName-v*" -TagPrefix "$gameName-v" -AllowExistingTag
    foreach ($editor in @($manifest.editors)) {
        $hasKind = $editor.PSObject.Properties.Name -contains 'kind'
        if ([int]$manifest.hostApiVersion -ge 6 -and $hasKind) {
            throw "$($manifest.id) Host API 6 editor metadata must not contain kind."
        }
        if ([int]$manifest.hostApiVersion -le 5 -and -not $hasKind) {
            throw "$($manifest.id) legacy editor metadata must contain kind."
        }
    }
    if (-not $moduleIds.Add($manifest.id)) { throw "Duplicate module id: $($manifest.id)" }
    $projectFiles = @(Get-ChildItem -LiteralPath $directory.FullName -File -Filter "GameValueEditor.Modules.*.csproj")
    if ($projectFiles.Count -ne 1) {
        throw "$($directory.Name) must contain exactly one root GameValueEditor.Modules.*.csproj."
    }
    [xml]$project = Get-Content -LiteralPath $projectFiles[0].FullName -Raw -Encoding UTF8
    $assemblyNameNode = @($project.Project.PropertyGroup.AssemblyName) | Select-Object -First 1
    $moduleVersionNode = @($project.Project.PropertyGroup.ModuleVersion) | Select-Object -First 1
    $assemblyName = if ($assemblyNameNode -is [System.Xml.XmlElement]) {
        [string]$assemblyNameNode.InnerText
    } else {
        [string]$assemblyNameNode
    }
    $moduleVersion = if ($moduleVersionNode -is [System.Xml.XmlElement]) {
        [string]$moduleVersionNode.InnerText
    } else {
        [string]$moduleVersionNode
    }
    if ($manifest.assemblyFile -ne "$assemblyName.dll" -or $manifest.version -ne $moduleVersion) {
        throw "$($manifest.id) project assembly or default version does not match module.json."
    }

    $editorIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($editor in $manifest.editors) {
        if (-not $editor.id.StartsWith("$($manifest.id).", [System.StringComparison]::Ordinal) -or
            -not $editorIds.Add($editor.id)) {
            throw "$($manifest.id) has an invalid or duplicate editor id: $($editor.id)"
        }
    }

    $contributors = Get-Content -LiteralPath $contributorsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($contributors.moduleId -ne $manifest.id) { throw "$contributorsPath belongs to another module." }
    foreach ($contributor in $contributors.contributors) {
        $first = [DateOnly]::ParseExact($contributor.firstContributionDate, "yyyy-MM-dd")
        $latest = [DateOnly]::ParseExact($contributor.latestContributionDate, "yyyy-MM-dd")
        if ($latest -lt $first) { throw "$contributorsPath contains an inverted date range." }
    }
    $manifests[$manifest.id] = $manifest
    $contributorsByModule[$manifest.id] = @($contributors.contributors)
    $directoriesByModule[$manifest.id] = $directory.Name
}

if (-not $SkipCatalog) {
    $catalogPath = Join-Path $repoRoot "catalog.json"
    Assert-JsonSchema -JsonPath $catalogPath -SchemaPath (Join-Path $schemasRoot "catalog.schema.json")
    $catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $catalogIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $catalog.modules) {
        if (-not $catalogIds.Add($entry.id)) { throw "Catalog contains duplicate module id: $($entry.id)" }
        if (-not $manifests.ContainsKey($entry.id)) { throw "Catalog entry $($entry.id) has no source module." }
        $source = $manifests[$entry.id]
        foreach ($property in @("version", "displayName", "gameDisplayName", "description", "hostApiVersion", "processNames", "compatibleBuilds", "editors")) {
            if ((ConvertTo-ComparableJson $entry.$property) -ne (ConvertTo-ComparableJson $source.$property)) {
                throw "Catalog entry $($entry.id) has stale $property metadata."
            }
        }
        $sourceLegacyIds = if ($source.PSObject.Properties.Name -contains "legacyIds") { @($source.legacyIds) } else { @() }
        $entryLegacyIds = if ($entry.PSObject.Properties.Name -contains "legacyIds") { @($entry.legacyIds) } else { @() }
        if ((ConvertTo-ComparableJson $entryLegacyIds) -ne (ConvertTo-ComparableJson $sourceLegacyIds)) {
            throw "Catalog entry $($entry.id) has stale legacyIds metadata."
        }
        if ((ConvertTo-ComparableJson @($entry.contributors)) -ne
            (ConvertTo-ComparableJson $contributorsByModule[$entry.id])) {
            throw "Catalog entry $($entry.id) has stale contributor metadata."
        }
        $releaseSlug = [System.IO.Path]::GetFileNameWithoutExtension([string]$source.assemblyFile) `
            -replace '^GameValueEditor\.Modules\.', ''
        $assetName = "GameValueEditor.Module.$releaseSlug-v$($source.version).zip"
        $expectedUrl = "https://github.com/BestWishes/GameValueEditor-Modules/releases/download/$($directoriesByModule[$entry.id])-v$($source.version)/$assetName"
        if ($entry.downloadUrl -ne $expectedUrl) {
            throw "Catalog entry $($entry.id) has an unexpected download URL."
        }
    }
    if ($catalog.modules.Count -ne $manifests.Count) {
        throw "Every source module must appear exactly once in catalog.json."
    }
}

Write-Host "Validated $($manifests.Count) game modules against repository schemas."
