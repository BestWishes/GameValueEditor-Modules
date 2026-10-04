[CmdletBinding()]
param(
    [switch]$SkipCatalog
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

dotnet build (Join-Path $repoRoot "GameValueEditor.Modules.slnx") -c Release
if ($LASTEXITCODE -ne 0) { throw "Module release build failed with code $LASTEXITCODE." }

& (Join-Path $repoRoot "scripts\validate-modules.ps1") -SkipCatalog:$SkipCatalog
if ($LASTEXITCODE -ne 0) { throw "Module validation failed with code $LASTEXITCODE." }

Write-Host "Module release verification passed."
