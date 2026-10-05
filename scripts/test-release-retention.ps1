$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'versioning.ps1')
. (Join-Path $PSScriptRoot 'release-retention.ps1')

function New-TestRelease([string]$Game, [string]$Version) {
    [pscustomobject]@{
        id = "$Game-$Version"
        tag_name = "$Game-v$Version"
        draft = $false
        prerelease = $false
        assets = @([pscustomobject]@{
            name = "GameValueEditor.Module.Test-v$Version.zip"
            state = 'uploaded'
        })
    }
}

foreach ($count in 0..5) {
    $releases = @()
    foreach ($index in 0..($count - 1)) {
        if ($count -eq 0) { break }
        $releases += New-TestRelease 'fzzml' "2.1.$index"
    }
    $releases += New-TestRelease 'worldapart' '9.9.9'
    $plan = Get-ReleaseRetentionPlan -Releases $releases -TagPrefix 'fzzml-v' -ExpectedAssetName {
        param($version) "GameValueEditor.Module.Test-v$version.zip"
    }
    if ($plan.Keep.Count -ne [Math]::Min(3, $count) -or $plan.Delete.Count -ne [Math]::Max(0, $count - 3)) {
        throw "Module retention plan failed for $count releases."
    }
    if (@($plan.Delete | ForEach-Object { $_.Release.tag_name }) -contains 'worldapart-v9.9.9') {
        throw 'Module retention plan crossed the game release group.'
    }
}
Write-Host 'Module release retention simulation passed for 0 through 5 releases.'
