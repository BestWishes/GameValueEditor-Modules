$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'small-update-contract.ps1')
Assert-SmallUpdateContract ([pscustomobject]@{ id='fixture'; hostApiVersion=8; supportsUnlistedBuildValidation=$true })
foreach ($fixture in @(
    [pscustomobject]@{ id='missing'; hostApiVersion=8 },
    [pscustomobject]@{ id='disabled'; hostApiVersion=8; supportsUnlistedBuildValidation=$false },
    [pscustomobject]@{ id='string'; hostApiVersion=8; supportsUnlistedBuildValidation='true' },
    [pscustomobject]@{ id='old-api'; hostApiVersion=4; supportsUnlistedBuildValidation=$true }
)) {
    $rejected = $false
    try { Assert-SmallUpdateContract $fixture } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid contract was accepted: $($fixture.id)" }
}
$root = Split-Path $PSScriptRoot -Parent
Assert-SmallUpdateContract (Get-Content -LiteralPath (Join-Path $root 'templates/game-module/module.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
foreach ($game in Get-ChildItem -LiteralPath (Join-Path $root 'games') -Directory) {
    Assert-SmallUpdateContract (Get-Content -LiteralPath (Join-Path $game.FullName 'module.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
}
Write-Host 'Mandatory small-update contract passed: current modules, scaffold, missing/false/string/old-API rejection.'
