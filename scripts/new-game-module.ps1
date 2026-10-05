[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9-]+$')] [string]$Game,
    [Parameter(Mandatory)] [ValidatePattern('^game\.[a-z0-9.-]+$')] [string]$ModuleId,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z][A-Za-z0-9]*$')] [string]$ClassName,
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string]$GameDisplayName,
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string]$ModuleDisplayName,
    [Parameter(Mandatory)] [ValidateNotNullOrEmpty()] [string]$ProcessName,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Fa-f0-9]{64}$')] [string]$ExecutableSha256,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Fa-f0-9]{64}$')] [string]$GameAssemblySha256,
    [Parameter(Mandatory)] [ValidatePattern('^[A-Fa-f0-9]{64}$')] [string]$MetadataSha256
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$gamesRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "games")) + [System.IO.Path]::DirectorySeparatorChar
$templateRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "templates\game-module"))
$destination = [System.IO.Path]::GetFullPath((Join-Path $gamesRoot $Game))
if (-not ($destination + [System.IO.Path]::DirectorySeparatorChar).StartsWith(
        $gamesRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Game module destination escaped the games directory."
}
if (Test-Path -LiteralPath $destination) { throw "Game module directory already exists: $destination" }
foreach ($hash in @($ExecutableSha256, $GameAssemblySha256, $MetadataSha256)) {
    if ($hash -match '^0{64}$') { throw "Build fingerprints cannot use an all-zero placeholder." }
}

Copy-Item -LiteralPath $templateRoot -Destination $destination -Recurse
$replacements = [ordered]@{
    '__MODULE_ID__' = $ModuleId
    '__CLASS__' = $ClassName
    '__GAME_DISPLAY_NAME__' = $GameDisplayName
    '__MODULE_DISPLAY_NAME__' = $ModuleDisplayName
    '__PROCESS_NAME__' = $ProcessName
    '__EXE_SHA256__' = $ExecutableSha256.ToUpperInvariant()
    '__ASSEMBLY_SHA256__' = $GameAssemblySha256.ToUpperInvariant()
    '__METADATA_SHA256__' = $MetadataSha256.ToUpperInvariant()
}
foreach ($file in Get-ChildItem -LiteralPath $destination -Recurse -File) {
    $content = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    foreach ($replacement in $replacements.GetEnumerator()) {
        $content = $content.Replace($replacement.Key, $replacement.Value)
    }
    Set-Content -LiteralPath $file.FullName -Value $content -Encoding utf8NoBOM
}
$templateProject = Join-Path $destination "GameValueEditor.Modules.Template.csproj"
$targetProject = Join-Path $destination "GameValueEditor.Modules.$ClassName.csproj"
Move-Item -LiteralPath $templateProject -Destination $targetProject

& (Join-Path $PSScriptRoot "validate-modules.ps1") -SkipCatalog
Write-Host "Created safe module scaffold: $destination"
