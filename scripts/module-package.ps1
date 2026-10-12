# Build a candidate beside its destination, verify its exact two-file graph, then
# atomically promote without replacing any existing archive (including race winners).
function Test-ModuleArchiveCandidate {
    param([string]$Candidate, [string]$AssemblyPath, [string]$ManifestPath)
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $assemblyName = [IO.Path]::GetFileName($AssemblyPath)
    if ([string]$manifest.assemblyFile -cne $assemblyName -or [string]::IsNullOrWhiteSpace([string]$manifest.version)) {
        throw 'Candidate manifest does not identify its source assembly/version.'
    }
    $expected = @{ $assemblyName = $AssemblyPath; 'module.json' = $ManifestPath }
    $archive = [IO.Compression.ZipFile]::OpenRead($Candidate)
    try {
        if ($archive.Entries.Count -ne 2 -or
            (Compare-Object @($archive.Entries.FullName | Sort-Object) @($expected.Keys | Sort-Object))) {
            throw 'Module candidate must contain exactly DLL and module.json.'
        }
        foreach ($entry in $archive.Entries) {
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            if ($hash -cne (Get-FileHash -LiteralPath $expected[$entry.FullName] -Algorithm SHA256).Hash) {
                throw 'Module candidate differs from its verified source files.'
            }
        }
    }
    finally { $archive.Dispose() }
}

function Invoke-ModuleArchivePackage {
    param([string]$AssemblyPath, [string]$ManifestPath, [string]$OutputPath)
    $output = [IO.Path]::GetFullPath($OutputPath)
    if ([IO.File]::Exists($output)) { throw 'An existing module archive is immutable; choose a new version or output directory.' }
    foreach ($source in @($AssemblyPath, $ManifestPath)) {
        if (-not [IO.File]::Exists($source)) { throw "Module package source is missing: $source" }
    }
    $directory = [IO.Path]::GetDirectoryName($output)
    $null = [IO.Directory]::CreateDirectory($directory)
    $temporary = Join-Path $directory ('.module-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $AssemblyPath, [IO.Path]::GetFileName($AssemblyPath))
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $ManifestPath, 'module.json')
        }
        finally { $archive.Dispose() }
        Test-ModuleArchiveCandidate $temporary $AssemblyPath $ManifestPath
        [IO.File]::Move($temporary, $output, $false)
    }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}
