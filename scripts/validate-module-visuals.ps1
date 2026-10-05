$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$targets = @((Join-Path $repoRoot 'games'), (Join-Path $repoRoot 'shared'))
$patterns = @(
    '#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?',
    'Application\.Current\.Resources',
    'MergedDictionaries',
    'new\s+Window\s*\{',
    'Color\.From(Rgb|Argb|ScRgb)'
)
$arguments = @('-n', '--glob', '*.cs', '--glob', '*.xaml')
foreach ($pattern in $patterns) { $arguments += @('-e', $pattern) }
$arguments += $targets
$matches = @(& rg @arguments)
if ($LASTEXITCODE -notin @(0, 1)) { throw "Unable to validate module visual sources (rg code $LASTEXITCODE)." }
if ($matches.Count -gt 0) {
    throw "Official modules contain host-owned visual definitions:`n$($matches -join "`n")"
}
Write-Host 'Validated module visual ownership: no custom palette, global theme dictionary, or window shell.'
