param(
    [Parameter(Mandatory)][ValidateSet('validate-probabilities-engine.cjs', 'validate-drops-engine.cjs', 'inspect-drops-names.cjs', 'inspect-material-state-contract.cjs', 'validate-all-materials-engine.cjs')][string]$Validation
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$currentMains = @(Get-CimInstance Win32_Process -Filter "Name='ZsebExpedition.exe'" | Where-Object { $_.CommandLine -notmatch '(?:^|\s)--type=' })
if ($currentMains.Count -ne 1) { throw 'A unique original runtime is required for the isolated helper; no game will be launched.' }
$runtime = $currentMains[0].ExecutablePath
$archive = Join-Path $taskRoot 'artifacts/play-again-expedition/20261007-cold-startup-trace/app-0.116.70.original.asar'
$fixture = Join-Path $taskRoot 'artifacts/play-again-expedition/20261007-213142-offline-recovery/restore-protection-scrolls-2.json'
if ((Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ne 'A79F66A6F236B237E25C00DD9F8BA763F9BAD13E16CD3269DE2348EA7FFEF396') { throw 'Native fixture runtime changed' }
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $runtime
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.Environment['ELECTRON_RUN_AS_NODE'] = '1'
$start.ArgumentList.Add('-e')
$start.ArgumentList.Add("try { require(process.argv[1]); } catch(e) { console.log(JSON.stringify({error:e.message,frames:e.stack.split('\n').filter(x=>x.trim().startsWith('at ')).slice(0,6)})); process.exitCode=1; }")
$start.ArgumentList.Add((Join-Path $PSScriptRoot $Validation))
$start.ArgumentList.Add($archive)
$start.ArgumentList.Add($fixture)
$helper = [Diagnostics.Process]::Start($start)
try {
    $stdout = $helper.StandardOutput.ReadToEndAsync()
    $stderr = $helper.StandardError.ReadToEndAsync()
    if (-not $helper.WaitForExit(30000)) { $helper.Kill($true); $helper.WaitForExit(); throw 'Owned native fixture helper timed out and was stopped' }
    $text = $stdout.GetAwaiter().GetResult()
    $errorText = $stderr.GetAwaiter().GetResult()
    $text
    if ($errorText) { $errorText.Substring([Math]::Max(0, $errorText.Length - 1200)) }
    if ($helper.ExitCode -ne 0) { throw 'Native fixture validation failed' }
} finally {
    if (-not $helper.HasExited) { $helper.Kill($true); $helper.WaitForExit() }
    $helper.Dispose()
}
