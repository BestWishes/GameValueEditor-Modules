[CmdletBinding()]
param(
    [string]$Version = "2.1.0",
    [switch]$SkipCatalog
)

& (Join-Path $PSScriptRoot "Publish-GameModule.ps1") -GameDirectory "fzzml" -ProjectFile "GameValueEditor.Modules.Fzzml.csproj" -AssemblyFile "GameValueEditor.Modules.Fzzml.dll" -ReleaseSlug "Fzzml" -Version $Version -SkipCatalog:$SkipCatalog
