[CmdletBinding()]
param([string]$Version = "0.4.1")

$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "Publish-GameModule.ps1") `
    -GameDirectory "last-epoch" `
    -ProjectFile "GameValueEditor.Modules.LastEpoch.csproj" `
    -AssemblyFile "GameValueEditor.Modules.LastEpoch.dll" `
    -ReleaseSlug "LastEpoch" `
    -Version $Version
