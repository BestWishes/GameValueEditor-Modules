[CmdletBinding()]
param(
    [string]$Version = "0.4.2",
    [switch]$SkipCatalog
)

$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "Publish-GameModule.ps1") `
    -GameDirectory "last-epoch" `
    -ProjectFile "GameValueEditor.Modules.LastEpoch.csproj" `
    -AssemblyFile "GameValueEditor.Modules.LastEpoch.dll" `
    -ReleaseSlug "LastEpoch" `
    -Version $Version `
    -SkipCatalog:$SkipCatalog
