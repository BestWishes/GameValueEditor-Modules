#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'module-package.ps1')
$checks = 0
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message }; $script:checks++ }
function Reject([scriptblock]$Action) { try { & $Action; } catch { $script:checks++; return }; throw 'Expected package failure.' }
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$fixture = Join-Path $artifacts ('module-package-tests-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($fixture)
try {
    $assembly = Join-Path $fixture 'Fixture.dll'; $manifest = Join-Path $fixture 'module.json'
    [IO.File]::WriteAllText($assembly, 'isolated fixture binary')
    [IO.File]::WriteAllText($manifest, '{"assemblyFile":"Fixture.dll","version":"0.1.2"}')
    $old = Join-Path $fixture 'GameValueEditor.Module.Fixture-v0.0.7.zip'
    $other = Join-Path $fixture 'GameValueEditor.Module.Other-v1.0.0.zip'
    foreach ($path in @($old,$other)) { [IO.File]::WriteAllText($path, 'old immutable artifact') }
    $oldHash = (Get-FileHash -LiteralPath $old).Hash; $otherHash = (Get-FileHash -LiteralPath $other).Hash
    $output = Join-Path $fixture 'GameValueEditor.Module.Fixture-v0.1.2.zip'
    Reject { Invoke-ModuleArchivePackage (Join-Path $fixture 'missing.dll') $manifest $output }
    Check (-not [IO.File]::Exists($output)) 'A failed build/source check promoted a package.'
    [IO.File]::WriteAllText($manifest, '{"assemblyFile":"Wrong.dll","version":"0.1.2"}')
    Reject { Invoke-ModuleArchivePackage $assembly $manifest $output }
    Check (-not [IO.File]::Exists($output)) 'A failed candidate validation promoted a package.'
    [IO.File]::WriteAllText($manifest, '{"assemblyFile":"Fixture.dll","version":"0.1.2"}')
    Invoke-ModuleArchivePackage $assembly $manifest $output
    Test-ModuleArchiveCandidate $output $assembly $manifest; $checks++
    $outputHash = (Get-FileHash -LiteralPath $output).Hash
    Reject { Invoke-ModuleArchivePackage $assembly $manifest $output }
    Check ((Get-FileHash -LiteralPath $output).Hash -ceq $outputHash) 'A repeated build replaced its existing package.'
    Check ((Get-FileHash -LiteralPath $old).Hash -ceq $oldHash -and (Get-FileHash -LiteralPath $other).Hash -ceq $otherHash) 'Packaging deleted/replaced old or other-game packages.'
    Check (@(Get-ChildItem -LiteralPath $fixture -File -Filter '.module-*.zip').Count -eq 0) 'An owned candidate leaked.'

    # Inject verification failure and a destination collision into the real promotion path.
    $validator = ${function:Test-ModuleArchiveCandidate}
    function Test-ModuleArchiveCandidate { throw 'Injected validation failure.' }
    $failed = Join-Path $fixture 'failed.zip'
    Reject { Invoke-ModuleArchivePackage $assembly $manifest $failed }
    Check (-not [IO.File]::Exists($failed)) 'A verifier failure was promoted.'
    function Test-ModuleArchiveCandidate { [IO.File]::WriteAllText($script:collision, 'race winner') }
    $script:collision = Join-Path $fixture 'collision.zip'
    Reject { Invoke-ModuleArchivePackage $assembly $manifest $script:collision }
    Check ([IO.File]::ReadAllText($script:collision) -ceq 'race winner') 'Promotion overwrote a concurrent package.'
    ${function:Test-ModuleArchiveCandidate} = $validator
    Check ((Get-FileHash -LiteralPath $old).Hash -ceq $oldHash -and (Get-FileHash -LiteralPath $other).Hash -ceq $otherHash) 'Failed promotion changed historical packages.'
    Write-Host "Module packaging: $checks checks passed; old/other-game archives preserved, failed candidates not promoted."
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^module-package-tests-[a-f0-9]{32}$') { throw 'Unsafe fixture cleanup.' }
    if ([IO.Directory]::Exists($resolved)) { [IO.Directory]::Delete($resolved, $true) }
}
