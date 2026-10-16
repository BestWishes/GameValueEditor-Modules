# Current official source manifests only. Never rewrite immutable historical releases.
function Assert-SmallUpdateContract {
    param([Parameter(Mandatory)]$Manifest)
    if ($Manifest.PSObject.Properties.Name -notcontains 'supportsUnlistedBuildValidation' -or
        $Manifest.supportsUnlistedBuildValidation -isnot [bool] -or
        -not $Manifest.supportsUnlistedBuildValidation -or [int]$Manifest.hostApiVersion -lt 5) {
        throw "$($Manifest.id): automatic small-update runtime validation is mandatory (supportsUnlistedBuildValidation=true, Host API >= 5). See docs/MODULE_EXTENSION_GUIDE.md."
    }
}
