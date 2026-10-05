[CmdletBinding()]
param(
    [switch]$SkipCatalog,
    [switch]$VerifyReleasedVersions,
    [string]$HostRepository = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

& (Join-Path $repoRoot "scripts\test-release-retention.ps1")
if ($LASTEXITCODE -ne 0) { throw "Release retention simulation failed with code $LASTEXITCODE." }
if ([string]::IsNullOrWhiteSpace($HostRepository)) {
    $HostRepository = Join-Path (Split-Path $repoRoot -Parent) "GameValueEditor"
}
$hostRoot = [System.IO.Path]::GetFullPath($HostRepository)
$hostContracts = Join-Path $hostRoot "src\GameValueEditor.ModuleSdk\ModuleContracts.cs"
$moduleContracts = Join-Path $repoRoot "sdk\GameValueEditor.ModuleSdk\ModuleContracts.cs"
$integrationDirectory = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\integration-packages"))
if (-not (Test-Path -LiteralPath $hostContracts)) {
    throw "The current host repository is required for package compatibility verification: $hostRoot"
}
if ((Get-FileHash -LiteralPath $hostContracts -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $moduleContracts -Algorithm SHA256).Hash) {
    throw "Host and module repository SDK contracts are different."
}

& (Join-Path $repoRoot "scripts\validate-modules.ps1") -SkipCatalog:$SkipCatalog

dotnet build (Join-Path $repoRoot "sdk\GameValueEditor.ModuleSdk\GameValueEditor.ModuleSdk.csproj") -c Release
if ($LASTEXITCODE -ne 0) { throw "Module SDK build failed with code $LASTEXITCODE." }

if (-not $integrationDirectory.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Integration package directory escaped the repository."
}
if (Test-Path -LiteralPath $integrationDirectory) {
    Remove-Item -LiteralPath $integrationDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $integrationDirectory -Force | Out-Null

$archives = @()
foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $repoRoot "games") -Directory | Sort-Object Name) {
    $manifestPath = Join-Path $directory.FullName "module.json"
    if (-not (Test-Path -LiteralPath $manifestPath)) { continue }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $projectFiles = @(Get-ChildItem -LiteralPath $directory.FullName -File -Filter "GameValueEditor.Modules.*.csproj")
    if ($projectFiles.Count -ne 1) { throw "$($directory.Name) does not have exactly one module project." }
    dotnet build $projectFiles[0].FullName -c Release
    if ($LASTEXITCODE -ne 0) { throw "$($manifest.id) release build failed with code $LASTEXITCODE." }

    & (Join-Path $repoRoot "scripts\Publish-GameModule.ps1") -Game $directory.Name `
        -OutputDirectory $integrationDirectory -SkipCatalog `
        -VerifyReleasedVersion:$VerifyReleasedVersions
    $releaseSlug = [System.IO.Path]::GetFileNameWithoutExtension([string]$manifest.assemblyFile) `
        -replace '^GameValueEditor\.Modules\.', ''
    $archivePath = Join-Path $integrationDirectory "GameValueEditor.Module.$releaseSlug-v$($manifest.version).zip"
    if (-not (Test-Path -LiteralPath $archivePath)) { throw "Module archive was not created: $archivePath" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) } | ForEach-Object FullName)
        $expectedEntries = @([string]$manifest.assemblyFile, "module.json")
        if ($entries.Count -ne 2 -or
            (Compare-Object ($entries | Sort-Object) ($expectedEntries | Sort-Object))) {
            throw "$($manifest.id) archive must contain only its DLL and module.json."
        }
    } finally {
        $archive.Dispose()
    }
    $archives += [System.IO.Path]::GetFullPath($archivePath)
}

$liveTestProject = Join-Path $repoRoot "tests\GameValueEditor.Modules.LiveTests\GameValueEditor.Modules.LiveTests.csproj"
dotnet build $liveTestProject -c Release
if ($LASTEXITCODE -ne 0) { throw "Module-owned live test projects failed to build with code $LASTEXITCODE." }

$smokeProject = Join-Path $hostRoot "tests\GameValueEditor.SmokeTests\GameValueEditor.SmokeTests.csproj"
$packageArguments = @($archives | ForEach-Object { "--verify-module-package=$_" })
dotnet run --project $smokeProject -c Release -- @packageArguments
if ($LASTEXITCODE -ne 0) { throw "Real module package compatibility verification failed with code $LASTEXITCODE." }

Write-Host "Module release verification passed for $($archives.Count) real packages."
