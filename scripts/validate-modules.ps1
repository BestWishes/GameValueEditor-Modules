[CmdletBinding()]
param([switch]$SkipCatalog)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$gamesRoot = Join-Path $repoRoot "games"
$moduleIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$manifests = @{}

foreach ($directory in Get-ChildItem -LiteralPath $gamesRoot -Directory) {
    $manifestPath = Join-Path $directory.FullName "module.json"
    $contributorsPath = Join-Path $directory.FullName "contributors.generated.json"
    if (-not (Test-Path -LiteralPath $manifestPath) -or -not (Test-Path -LiteralPath $contributorsPath)) {
        throw "$($directory.Name) must contain module.json and contributors.generated.json."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($name in @("id", "version", "displayName", "gameDisplayName", "description", "assemblyFile", "hostApiVersion", "processNames", "compatibleBuilds", "editors")) {
        if (-not $manifest.PSObject.Properties.Name.Contains($name)) { throw "$manifestPath is missing $name." }
    }
    if ($manifest.id -notmatch '^game\.[a-z0-9.-]+$' -or -not $moduleIds.Add($manifest.id)) { throw "Invalid or duplicate module id: $($manifest.id)" }
    if ($manifest.version -notmatch '^\d+\.\d+\.\d+$' -or $manifest.hostApiVersion -notin @(2, 3, 4)) { throw "$($manifest.id) has an invalid version or Host API." }
    if (@($manifest.processNames).Count -eq 0 -or @($manifest.compatibleBuilds).Count -eq 0 -or @($manifest.editors).Count -eq 0) { throw "$($manifest.id) has an empty required collection." }
    foreach ($build in $manifest.compatibleBuilds) {
        foreach ($hashName in @("executableSha256", "gameAssemblySha256", "metadataSha256")) {
            if ($build.$hashName -notmatch '^[A-Fa-f0-9]{64}$') { throw "$($manifest.id) has an invalid $hashName." }
        }
    }
    $editorIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($editor in $manifest.editors) {
        if (-not $editor.id.StartsWith("$($manifest.id).", [System.StringComparison]::OrdinalIgnoreCase) -or -not $editorIds.Add($editor.id)) { throw "$($manifest.id) has an invalid or duplicate editor id." }
        if ($editor.kind -notin @("collection", "master-detail", "property-grid") -or $null -eq $editor.sessionOnly) { throw "$($editor.id) has an invalid editor contract." }
    }
    $contributors = Get-Content -LiteralPath $contributorsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($contributors.schemaVersion -ne 1 -or $contributors.moduleId -ne $manifest.id) { throw "$contributorsPath does not match $($manifest.id)." }
    foreach ($contributor in $contributors.contributors) {
        if ($contributor.githubId -le 0 -or [string]::IsNullOrWhiteSpace($contributor.githubLogin) -or [string]::IsNullOrWhiteSpace($contributor.displayName) -or $contributor.profileUrl -notmatch '^https://github\.com/[^/]+/?$') { throw "$contributorsPath contains an invalid contributor." }
        $first = [DateOnly]::ParseExact($contributor.firstContributionDate, "yyyy-MM-dd")
        $latest = [DateOnly]::ParseExact($contributor.latestContributionDate, "yyyy-MM-dd")
        if ($latest -lt $first) { throw "$contributorsPath contains an inverted date range." }
    }
    $manifests[$manifest.id] = $manifest
}

if (-not $SkipCatalog) {
    $catalog = Get-Content -LiteralPath (Join-Path $repoRoot "catalog.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($catalog.schemaVersion -ne 3 -or $catalog.hostApiVersion -ne 4) { throw "catalog.json must use schemaVersion 3 and Host API 4." }
    foreach ($entry in $catalog.modules) {
        if (-not $manifests.ContainsKey($entry.id)) { throw "Catalog entry $($entry.id) has no source module." }
        $source = $manifests[$entry.id]
        if ($entry.version -ne $source.version -or $entry.gameDisplayName -ne $source.gameDisplayName -or $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw "Catalog entry $($entry.id) is stale or incomplete." }
    }
    if (@($catalog.modules).Count -ne $manifests.Count) { throw "Every source module must appear exactly once in catalog.json." }
}

Write-Host "Validated $($manifests.Count) game modules."
