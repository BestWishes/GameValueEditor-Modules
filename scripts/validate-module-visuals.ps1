$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$paths = @(& git -C $repoRoot ls-files --cached --others --exclude-standard -- games shared)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate module source paths using Git.' }
$targets = @($paths | Where-Object { [IO.Path]::GetExtension($_) -in @('.cs', '.xaml') } |
    Sort-Object -Unique | ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
$patterns = @(
    '#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?',
    'Application\.Current\.Resources',
    'MergedDictionaries',
    'new\s+Window\s*\{',
    'Color\.From(Rgb|Argb|ScRgb)'
)
$arguments = @('-n')
foreach ($pattern in $patterns) { $arguments += @('-e', $pattern) }
$arguments += @('--') + $targets
if ($targets.Count -eq 0) { throw 'No module visual source files were found.' }
$matches = @(& rg @arguments)
if ($LASTEXITCODE -notin @(0, 1)) { throw "Unable to validate module visual sources (rg code $LASTEXITCODE)." }
if ($matches.Count -gt 0) {
    throw "Official modules contain host-owned visual definitions:`n$($matches -join "`n")"
}
Write-Host 'Validated module visual ownership: no custom palette, global theme dictionary, or window shell.'
