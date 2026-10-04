[CmdletBinding()]
param(
    [string]$Version = "1.2.0"
)

& (Join-Path $PSScriptRoot "Publish-GameModule.ps1") -GameDirectory "worldapart" -ProjectFile "GameValueEditor.Modules.WorldApart.csproj" -AssemblyFile "GameValueEditor.Modules.WorldApart.dll" -ReleaseSlug "WorldApart" -Version $Version
