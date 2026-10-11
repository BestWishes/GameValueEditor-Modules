param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$Script,
    [Parameter(Mandatory)][string]$Archive, [Parameter(Mandatory)][string]$Fixture)
$ErrorActionPreference = 'Stop'
$taskProbe = [Diagnostics.ProcessStartInfo]::new()
$taskProbe.FileName = $Executable
$taskProbe.UseShellExecute = $false
$taskProbe.CreateNoWindow = $true
$taskProbe.RedirectStandardOutput = $true
$taskProbe.RedirectStandardError = $true
$taskProbe.Environment['ELECTRON_RUN_AS_NODE'] = '1'
foreach ($taskArg in @($Script, $Archive, $Fixture)) { $taskProbe.ArgumentList.Add($taskArg) }
$taskProcess = [Diagnostics.Process]::Start($taskProbe)
try {
    $taskOut = $taskProcess.StandardOutput.ReadToEndAsync()
    $taskErr = $taskProcess.StandardError.ReadToEndAsync()
    if (!$taskProcess.WaitForExit(30000)) { throw 'Owned probe timeout' }
    $taskOut.GetAwaiter().GetResult()
    $taskErr.GetAwaiter().GetResult()
    if ($taskProcess.ExitCode -ne 0) { throw 'Owned native probe failed' }
} finally {
    if (!$taskProcess.HasExited) { $taskProcess.Kill($true); $taskProcess.WaitForExit() }
    $taskProcess.Dispose()
}
