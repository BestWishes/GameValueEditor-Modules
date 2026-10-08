#Requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$checks = 0
function Assert-BuildSchemaTest([bool]$Result, [string]$Message) {
    if (-not $Result) { throw "Package build schema regression: $Message" }
    $script:checks++
}
$native = @{ executableSha256 = ('A' * 64); gameAssemblySha256 = ('B' * 64); metadataSha256 = ('C' * 64) }
$web = @{ executableSha256 = ('A' * 64); gameAssemblySha256 = ''; metadataSha256 = ''; packageSha256 = ('D' * 64) }
foreach ($file in @('module.schema.json','catalog.schema.json')) {
    $schema = Get-Content -LiteralPath (Join-Path $root "schemas/$file") -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
    $build = if ($file -eq 'module.schema.json') { $schema.properties.compatibleBuilds.items } else { $schema.'$defs'.build }
    $definitions = @{} + $schema.'$defs'; $definitions.build = $build
    $buildSchema = @{ '$schema' = $schema.'$schema'; '$defs' = $definitions; '$ref' = '#/$defs/build' } | ConvertTo-Json -Depth 50
    foreach ($valid in @($native,$web)) {
        Assert-BuildSchemaTest (Test-Json -Json ($valid | ConvertTo-Json) -Schema $buildSchema) "$file rejected an exact native/package build"
    }
    foreach ($case in @('missing-package','empty-package','invalid-package','partial-il2cpp','missing-exe','unknown-component')) {
        $invalid = @{} + $web
        switch ($case) {
            'missing-package' { $invalid.Remove('packageSha256') }
            'empty-package' { $invalid.packageSha256 = '' }
            'invalid-package' { $invalid.packageSha256 = 'unknown' }
            'partial-il2cpp' { $invalid.gameAssemblySha256 = 'B' * 64 }
            'missing-exe' { $invalid.Remove('executableSha256') }
            'unknown-component' { $invalid.guessedSha256 = 'E' * 64 }
        }
        Assert-BuildSchemaTest (-not (Test-Json -Json ($invalid | ConvertTo-Json) -Schema $buildSchema -ErrorAction SilentlyContinue)) "$file accepted $case"
    }
}
$catalogSchema = Get-Content -LiteralPath (Join-Path $root 'schemas/catalog.schema.json') -Raw -Encoding UTF8
$catalog = Get-Content -LiteralPath (Join-Path $root 'catalog.json') -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
Assert-BuildSchemaTest (Test-Json -Json ($catalog | ConvertTo-Json -Depth 50) -Schema $catalogSchema) 'Existing published catalog no longer validates'
$manifestJson = Get-Content -LiteralPath (Join-Path $root 'games/play-again-expedition/module.json') -Raw -Encoding UTF8
$manifestSchema = Get-Content -LiteralPath (Join-Path $root 'schemas/module.schema.json') -Raw -Encoding UTF8
Assert-BuildSchemaTest (Test-Json -Json $manifestJson -Schema $manifestSchema) 'Current local package manifest no longer validates'
$manifest = $manifestJson | ConvertFrom-Json -AsHashtable
$entry = @{} + $manifest; $entry.Remove('assemblyFile'); $entry.contributors = @()
$entry.downloadUrl = 'https://github.com/BestWishes/GameValueEditor-Modules/releases/download/fixture-v0.0.5/Fixture.zip'
$entry.sha256 = 'F' * 64; $entry.sizeBytes = 1
$release = @{}
foreach ($key in @('version','hostApiVersion','minimumHostVersion','compatibleBuilds','editors','downloadUrl','sha256','sizeBytes')) { $release[$key] = $entry[$key] }
$entry.releases = @($release)
$fixture = @{ schemaVersion = $catalog.schemaVersion; hostApiVersion = $catalog.hostApiVersion; modules = @($entry) }
Assert-BuildSchemaTest (Test-Json -Json ($fixture | ConvertTo-Json -Depth 50) -Schema $catalogSchema) 'Full package build catalog fixture rejected'
Write-Host "Package build schema regressions passed: $checks checks; read-only fixtures, no catalog/publication writes."
