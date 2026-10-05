[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9-]+$')] [string]$Game,
    [string]$ProxyUrl = 'http://127.0.0.1:7897'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'versioning.ps1')
. (Join-Path $PSScriptRoot 'release-retention.ps1')
$manifestPath = Join-Path $repoRoot "games\$Game\module.json"
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Unknown game module: $Game" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$slug = [IO.Path]::GetFileNameWithoutExtension([string]$manifest.assemblyFile) -replace '^GameValueEditor\.Modules\.', ''
$remoteUrl = (& git -C $repoRoot remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or $remoteUrl -notmatch 'github\.com[/:]BestWishes/GameValueEditor-Modules(?:\.git)?$') {
    throw 'origin is not the expected BestWishes/GameValueEditor-Modules repository.'
}
$credentialLines = "protocol=https`nhost=github.com`n`n" | git credential fill
if ($LASTEXITCODE -ne 0) { throw 'Unable to read the GitHub credential.' }
$credential = @{}
foreach ($line in $credentialLines) { if ($line -match '^([^=]+)=(.*)$') { $credential[$matches[1]] = $matches[2] } }
if (-not $credential.ContainsKey('password')) { throw 'GitHub credential is unavailable.' }
$headers = @{
    Authorization = "Bearer $($credential.password)"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'GameValueEditor-module-retention'
}
$request = @{ Headers = $headers; Proxy = $ProxyUrl; ErrorAction = 'Stop' }
$releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/BestWishes/GameValueEditor-Modules/releases?per_page=100' @request
$plan = Get-ReleaseRetentionPlan -Releases $releases -TagPrefix "$Game-v" -ExpectedAssetName {
    param($version) "GameValueEditor.Module.$slug-v$version.zip"
}
foreach ($item in $plan.Delete) {
    $tag = [string]$item.Release.tag_name
    if ($PSCmdlet.ShouldProcess("GitHub Release $tag", 'Delete release and assets; preserve Git tag')) {
        Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/BestWishes/GameValueEditor-Modules/releases/$($item.Release.id)" @request | Out-Null
    }
}
[pscustomobject]@{
    RetainedTags = @($plan.Keep | ForEach-Object { [string]$_.Release.tag_name })
    DeletedTags = @($plan.Delete | ForEach-Object { [string]$_.Release.tag_name })
    GitTagsPreserved = $true
}
