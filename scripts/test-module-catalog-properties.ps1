#Requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$errors = $null
$tokens = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Publish-GameModule.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Module publisher syntax errors.' }
$checks = 0
foreach ($property in @('releases', 'version')) {
    $marker = '$entry.PSObject.Properties[' + "'$property']"
    $condition = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text.Contains($marker)
    }, $true))
    if ($condition.Count -ne 1) { throw "Expected one actual $property guard." }
    $guard = [scriptblock]::Create($condition[0].Clauses[0].Item1.Extent.Text)
    foreach ($case in @('first-module', 'legacy-module', 'retained-releases', 'same-version')) {
        $entry = switch ($case) {
            'first-module' { [pscustomobject]@{} }
            'legacy-module' { [pscustomobject]@{ version = '0.0.5' } }
            'retained-releases' { [pscustomobject]@{ version = '0.0.5'; releases = @([pscustomobject]@{ version = '0.0.4' }) } }
            'same-version' { [pscustomobject]@{ version = '0.0.6'; releases = @([pscustomobject]@{ version = '0.0.6' }) } }
        }
        $Version = '0.0.6'
        $existingSnapshots = if ($case -in @('retained-releases', 'same-version')) { @($entry.releases) } else { @() }
        $actual = [bool](& $guard)
        $expected = if ($property -eq 'releases') { $case -in @('retained-releases', 'same-version') }
                    else { $case -in @('legacy-module', 'retained-releases') }
        if ($actual -ne $expected) { throw "Catalog guard $property failed for $case." }
        $checks++
    }
}
Write-Host "Actual module catalog guards passed: $checks checks under StrictMode; no archive, catalog or Git writes."
