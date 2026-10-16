# Read-only research helper. Input is bounded Cpp2IL ISIL output for two analyzed builds.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviousDump, [Parameter(Mandatory)][string]$CurrentDump)
$ErrorActionPreference = 'Stop'
function Read-Methods([string]$directory, [string]$assembly) {
    $typeDirectory = if ($assembly -eq 'ACTk.Runtime') { Join-Path $directory 'ACTk.Runtime/CodeStage/AntiCheat/ObscuredTypes' } else { Join-Path $directory $assembly }
    foreach ($file in Get-ChildItem -LiteralPath $typeDirectory -File -Filter '*.txt') {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        foreach ($match in [regex]::Matches($text, '(?ms)^Method: (.*?)\r?\n\r?\nDisassembly:\r?\n(.*?)\r?\nISIL:')) {
            $signature = $match.Groups[1].Value
            $lines = @($match.Groups[2].Value.Trim() -split '\r?\n' | ForEach-Object {
                [regex]::Replace($_.Trim(), '(?<![A-Za-z0-9_])[0-9A-Fa-f]{8,}h\b', 'ADDR')
            })
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))
            [pscustomobject]@{Assembly=$assembly;Class=$file.BaseName;Signature=$signature;Hash=$hash;Instructions=$lines.Count;Canonical=($lines -join "`n")}
        }
    }
}
$old = @(Read-Methods $PreviousDump 'Assembly-CSharp') + @(Read-Methods $PreviousDump 'ACTk.Runtime')
$new = @(Read-Methods $CurrentDump 'Assembly-CSharp') + @(Read-Methods $CurrentDump 'ACTk.Runtime')
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/DeskrawlRuntime.cs') -Raw -Encoding UTF8
$roles = @([regex]::Matches($source, 'Method\("([^"\r\n]+)", "([^"\r\n]+)"') | ForEach-Object {
    "$($_.Groups[1].Value)|$($_.Groups[2].Value)"
}) + @('ObscuredInt|kff','ObscuredLong|kfy','ObscuredFloat|kdo')
$contracts = foreach ($role in $roles | Sort-Object -Unique) {
    $class, $name = $role.Split('|')
    if ($class.EndsWith('.dll')) { continue }
    $baseline = @($old | Where-Object { $_.Class -eq $class -and $_.Signature -match (' ' + [regex]::Escape($name) + '\(') })
    if ($baseline.Count -ne 1) { throw "Baseline role is not unique: $role" }
    $candidates = @($new | Where-Object { $_.Hash -eq $baseline[0].Hash -and $_.Assembly -eq $baseline[0].Assembly })
    # Manually reviewed full-method differences: only the listed backing member
    # displacements changed. These are evidence for a new semantic contract, not
    # permission to generalize arbitrary displacements at runtime.
    $reviewed = @{'PlayerData|fmg'=@('PlayerData','fok'); 'PlayerTalentBook|fot'=@('PlayerTalentBook','fqx');
        'Player|fkt'=@('Player','fmx'); 'Player|bjy'=@('Player','bkj'); 'hh|dzd'=@('hk','eal')}
    if ($candidates.Count -eq 0 -and $reviewed.ContainsKey($role)) {
        $mapping = $reviewed[$role]
        $candidates = @($new | Where-Object { $_.Class -eq $mapping[0] -and $_.Signature -match (' '+$mapping[1]+'\(') })
        $members = @{
            'PlayerData|fmg'=@('mov rcx,[rax+0D8h]','mov rcx,[rax+0E0h]');
            'PlayerTalentBook|fot'=@('mov rcx,[rax+30h]','mov rcx,[rax+38h]');
            'Player|fkt'=@('mov byte ptr [rcx+158h],1','mov byte ptr [rcx+160h],1');
            'Player|bjy'=@('mov r8,[rdi+328h]','mov r8,[rdi+350h]');
            'hh|dzd'=@('mov rcx,[rax+40h]','mov rcx,[rax+48h]')
        }
        $before, $after = $members[$role]
        $expected = $baseline[0].Canonical.Replace($before, $after)
        $candidates = @($candidates | Where-Object { $_.Canonical -ceq $expected })
    }
    # Fully named types remain part of the identity; opaque type aliases may move.
    if ($class.Length -gt 3) { $candidates = @($candidates | Where-Object Class -eq $class) }
    [pscustomobject]@{Role=$role;Assembly=$baseline[0].Assembly;Hash=$baseline[0].Hash;Instructions=$baseline[0].Instructions;
        Matches=@($candidates | Select-Object Class,Signature)}
}
$contracts | ConvertTo-Json -Depth 6
