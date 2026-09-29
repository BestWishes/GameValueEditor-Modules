[CmdletBinding()]
param(
    [string]$Version = "2.0.1"
)

& (Join-Path $PSScriptRoot "Publish-GameModule.ps1") -GameDirectory "fzzml" -ProjectFile "GameValueEditor.Modules.Fzzml.csproj" -AssemblyFile "GameValueEditor.Modules.Fzzml.dll" -ReleaseSlug "Fzzml" -Version $Version
