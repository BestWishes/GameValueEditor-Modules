#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$NodeExecutable = 'node',
    [string]$HostReviewArchivePath = '',
    [string]$HostReviewArchiveSha256 = '',
    [switch]$ModuleOnly
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($HostReviewArchivePath) -ne [string]::IsNullOrWhiteSpace($HostReviewArchiveSha256)) { throw 'Local host review path and SHA-256 must be supplied together.' }
$moduleRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $moduleRoot '../..'))
$manifestPath = Join-Path $moduleRoot 'module.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$moduleVersion = [string]$manifest.version
if ($moduleVersion -notmatch '^(0|[1-9][0-9]*)\.[0-9]\.[0-9]$') { throw 'Invalid module version.' }
if (-not $ModuleOnly -and [version]$manifest.minimumHostVersion -gt [version]'0.5.1') {
    throw 'Use -ModuleOnly and the host build-complete-offline-bundle.ps1 entry point; the historical standalone template is host 0.5.1.'
}
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/play-again-expedition'))
$outputRoot = Join-Path $artifactRoot ('local-module-' + $moduleVersion + '-' + [datetime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
if (-not ($outputRoot + [IO.Path]::DirectorySeparatorChar).StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $outputRoot)) { throw 'Unsafe or existing local output directory.' }
$null = New-Item -ItemType Directory -Path $outputRoot
dotnet build (Join-Path $moduleRoot 'GameValueEditor.Modules.PlayAgainExpedition.csproj') -c Release "-p:NodeExecutable=$NodeExecutable" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Module build failed.' }
$dllName = 'GameValueEditor.Modules.PlayAgainExpedition.dll'
$dllPath = Join-Path $moduleRoot "bin/Release/net8.0-windows/$dllName"
$manifestPath = Join-Path $moduleRoot 'module.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.id -cne 'game.play-again-expedition' -or $manifest.version -cne $moduleVersion -or $manifest.assemblyFile -cne $dllName -or
    $manifest.hostApiVersion -ne 8 -or [version]$manifest.minimumHostVersion -lt [version]'0.5.1' -or
    -not (@($manifest.editors.displayName) -join ',' -ceq '材料,大转盘,掉落')) { throw 'Unexpected module manifest.' }
if ([Reflection.AssemblyName]::GetAssemblyName($dllPath).Version -ne [version]($moduleVersion + '.0')) { throw 'DLL version differs from manifest.' }
$archivePath = Join-Path $outputRoot "GameValueEditor.Module.PlayAgainExpedition-v$moduleVersion-local.zip"
$archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $dllPath, $dllName)
    $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $manifestPath, 'module.json')
} finally { $archive.Dispose() }
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    if ($archive.Entries.Count -ne 2 -or -not (@($archive.Entries.FullName | Sort-Object) -join ',' -ceq "$dllName,module.json")) { throw 'Module package must contain exactly DLL and manifest.' }
    foreach ($entry in $archive.Entries) {
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        $sourcePath = if ($entry.FullName -ceq $dllName) { $dllPath } else { $manifestPath }
        if ($hash -cne (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash) { throw 'Packaged bytes differ from verified source.' }
    }
} finally { $archive.Dispose() }

if ($ModuleOnly) {
    # Feed the existing host offline-bundle entry point; do not create a second
    # application package or overwrite any installation or official asset.
    [ordered]@{ LocalOnly = $true; Published = $false; ModuleId = $manifest.id; ModuleVersion = $manifest.version;
        ModuleArchive = $archivePath; ModuleSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash;
        DllSha256 = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash;
        ManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash } | ConvertTo-Json
    return
}

# A fresh LOCAL validation installation, not a new official complete-offline release.
# Never touch existing installations, libraries, the game, catalog or Git state.
$hostRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '../GameValueEditor'))
$basePath = Join-Path $hostRoot 'dist/GameValueEditor-v0.5.1-complete-offline-win-x64.zip'
$baseHash = (Get-FileHash -LiteralPath $basePath -Algorithm SHA256).Hash
$baseReceiptPath = Join-Path $hostRoot "artifacts/offline-bundle-receipts/$baseHash.json"
$baseReceipt = Get-Content -LiteralPath $baseReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable -Depth 60
if ($baseReceipt.SchemaVersion -ne 1 -or $baseReceipt.Sha256 -ine $baseHash) { throw 'Base host archive has no matching frozen receipt.' }
$appPath = Join-Path $outputRoot '应用'
$null = New-Item -ItemType Directory -Path $appPath
$appBoundary = [IO.Path]::GetFullPath($appPath) + [IO.Path]::DirectorySeparatorChar
$baseArchive = [IO.Compression.ZipFile]::OpenRead($basePath)
try {
    if ($baseArchive.Entries.Count -ne $baseReceipt.Files.Count) { throw 'Base archive entry count differs from receipt.' }
    foreach ($entry in $baseArchive.Entries) {
        if (-not $baseReceipt.Files.ContainsKey($entry.FullName)) { throw 'Unverified base file.' }
        $destination = [IO.Path]::GetFullPath((Join-Path $appPath $entry.FullName))
        if (-not $destination.StartsWith($appBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Base archive path escaped local installation.' }
        $stream = $entry.Open()
        try { $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($entryHash -ine $baseReceipt.Files[$entry.FullName]) { throw 'Base file differs from frozen evidence.' }
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
    }
} finally { $baseArchive.Dispose() }
# Optional LOCAL host fix, built and startup/updater-verified by the host's existing
# review command. Overlay only its two EXEs in our fresh directory, never a live
# installation or personal data. Formal host/module release packages stay intact.
if ($HostReviewArchivePath) {
    $hostReviewPath = [IO.Path]::GetFullPath($HostReviewArchivePath)
    $hostArtifactsBoundary = [IO.Path]::GetFullPath((Join-Path $hostRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (-not $hostReviewPath.StartsWith($hostArtifactsBoundary, [StringComparison]::OrdinalIgnoreCase) -or
        $HostReviewArchiveSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
        (Get-FileHash -LiteralPath $hostReviewPath -Algorithm SHA256).Hash -ine $HostReviewArchiveSha256) { throw 'Unverified local host review archive.' }
    $reviewNote = Join-Path ([IO.Path]::GetDirectoryName($hostReviewPath)) 'LOCAL-REVIEW.txt'
    $reviewText = Get-Content -LiteralPath $reviewNote -Raw -Encoding UTF8
    if (-not $reviewText.Contains('LOCAL ONLY / NOT A RELEASE.') -or -not $reviewText.Contains('Version 0.5.1,') -or
        $reviewText -notmatch [regex]::Escape($HostReviewArchiveSha256)) { throw 'Missing matching local review evidence.' }
    $reviewArchive = [IO.Compression.ZipFile]::OpenRead($hostReviewPath)
    try {
        $expectedEntries = @('GameValueEditor.exe','GameValueEditor.Updater.exe','LICENSE','README.md','SECURITY.md','release-compatibility.json') | Sort-Object
        if ($reviewArchive.Entries.Count -ne 6 -or (@($reviewArchive.Entries.FullName | Sort-Object) -join ',') -cne ($expectedEntries -join ',')) { throw 'Unexpected local host review file set.' }
        foreach ($file in @('GameValueEditor.exe', 'GameValueEditor.Updater.exe')) {
            $destination = Join-Path $appPath $file
            [IO.Compression.ZipFileExtensions]::ExtractToFile($reviewArchive.GetEntry($file), $destination, $true)
            if ([Diagnostics.FileVersionInfo]::GetVersionInfo($destination).FileVersion -cne '0.5.1.0') { throw 'Local review host version differs from the base/API-8 installation.' }
        }
    } finally { $reviewArchive.Dispose() }
    Copy-Item -LiteralPath $reviewNote -Destination (Join-Path $appPath '主程序-本地构建说明.txt')
    Copy-Item -LiteralPath (Join-Path $hostRoot 'docs/LAUNCHER_SPEED_LOCAL.md') -Destination (Join-Path $appPath 'LAUNCHER_SPEED_LOCAL.md')
}
$installRoot = Join-Path $appPath 'data/modules'
$packageRoot = Join-Path $installRoot 'packages/game.play-again-expedition/0.0.5'
$null = New-Item -ItemType Directory -Path $packageRoot
Copy-Item -LiteralPath $dllPath -Destination (Join-Path $packageRoot $dllName)
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $packageRoot 'module.json')
$installedPath = Join-Path $installRoot 'installed.json'
$installed = Get-Content -LiteralPath $installedPath -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable -Depth 20
if ($installed.SchemaVersion -ne 1 -or @($installed.Modules | Where-Object Id -EQ 'game.play-again-expedition').Count -ne 0) { throw 'Unexpected base installed state.' }
$installed.Modules = @($installed.Modules) + @{ Id = 'game.play-again-expedition'; Version = '0.0.5'; InstalledUtc = [datetime]::UtcNow.ToString('O') }
[IO.File]::WriteAllText($installedPath, ($installed | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $moduleRoot 'docs/LOCAL_USE.md') -Destination (Join-Path $appPath '再刷一把远征-本地模块说明.md')
foreach ($documentName in @('THREE_EDITORS_COMPLETION_LOCAL.md', 'DROPS_RESEARCH_AND_DESIGN.md', 'DIRECT_ATTACH_IMPLEMENTATION_LOCAL.md', 'UI_LAYOUT_LOCAL.md', 'MATERIAL_STATE_LOCAL.md')) {
    Copy-Item -LiteralPath (Join-Path $moduleRoot "docs/$documentName") -Destination (Join-Path $appPath $documentName)
}

# Use the host's real, already built API-8 module verifier. No implicit build or
# alternate packaging implementation; verify all preinstalled pages, including ours.
$verifier = Join-Path $hostRoot 'tests/GameValueEditor.SmokeTests/bin/Release/net8.0-windows/GameValueEditor.SmokeTests.exe'
if (-not (Test-Path -LiteralPath $verifier)) { throw 'Build the host Release module verifier first.' }
$verificationPath = Join-Path $outputRoot 'module-page-verification.json'
& $verifier "--verify-offline-directory=$appPath" "--module-verification-report=$verificationPath"
if ($LASTEXITCODE -ne 0) { throw 'Real host module/page verification failed.' }
$verified = Get-Content -LiteralPath $verificationPath -Raw -Encoding UTF8 | ConvertFrom-Json
$ours = @($verified.Modules | Where-Object Id -CEQ $manifest.id)
if ($verified.Verified -ne $true -or $verified.ApplicationVersion -cne '0.5.1' -or $verified.HostApiVersion -ne 8 -or
    $ours.Count -ne 1 -or $ours[0].Version -cne $manifest.version -or
    (@($ours[0].EditorIds) -join ',') -cne (@($manifest.editors.id) -join ',')) { throw 'Module verification report does not match local package.' }
$startup = Start-Process -FilePath (Join-Path $appPath 'GameValueEditor.exe') -WindowStyle Hidden -PassThru
try {
    if ($startup.WaitForExit(5000)) { throw 'Local packaged application exited during startup.' }
} finally {
    if (-not $startup.HasExited) { Stop-Process -Id $startup.Id }
    $startup.Dispose()
}
$record = [ordered]@{ LocalOnly = $true; ModuleId = $manifest.id; ModuleVersion = '0.0.5'; HostVersion = '0.5.1';
    ModuleArchive = $archivePath; ModuleSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash;
    DllSha256 = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash; ManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash;
    ApplicationDirectory = $appPath; BaseArchiveSha256 = $baseHash; BaseReceiptVerified = $true; ModuleVerified = $true; StartupVerified = $true;
    HostReviewUsed = [bool]$HostReviewArchivePath; HostReviewArchive = $HostReviewArchivePath; HostReviewSha256 = $HostReviewArchiveSha256;
    HostExeSha256 = (Get-FileHash -LiteralPath (Join-Path $appPath 'GameValueEditor.exe') -Algorithm SHA256).Hash;
    ModuleVerificationReport = $verificationPath;
    OriginalInstallationTouched = $false; OriginalLibraryCopied = $false; GameSaveTouched = $false; Published = $false }
[IO.File]::WriteAllText((Join-Path $outputRoot 'local-package-receipt.json'), ($record | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$record | ConvertTo-Json -Depth 6
