[CmdletBinding(DefaultParameterSetName = "PullRequest")]
param(
    [Parameter(ParameterSetName = "PullRequest", Mandatory)] [int]$PullRequestNumber,
    [Parameter(ParameterSetName = "Refresh", Mandatory)] [switch]$RefreshProfiles,
    [Parameter(Mandatory)] [string]$Repository,
    [Parameter(Mandatory)] [string]$Token
)

$ErrorActionPreference = "Stop"
$headers = @{ Authorization = "Bearer $Token"; Accept = "application/vnd.github+json"; "X-GitHub-Api-Version" = "2022-11-28" }
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Get-GitHubUser([string]$Login) {
    Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/users/$Login"
}
function Save-Contributors([string]$GameDirectory, [object]$Document) {
    $path = Join-Path $repoRoot "games\$GameDirectory\contributors.generated.json"
    $Document.contributors = @($Document.contributors | Sort-Object displayName, githubLogin)
    $Document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
}

if ($RefreshProfiles) {
    foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $repoRoot "games") -Directory) {
        $path = Join-Path $directory.FullName "contributors.generated.json"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $document = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($contributor in $document.contributors) {
            $user = Get-GitHubUser $contributor.githubLogin
            $contributor.githubId = $user.id
            $contributor.displayName = if ([string]::IsNullOrWhiteSpace($user.name)) { $user.login } else { $user.name }
            $contributor.profileUrl = $user.html_url
        }
        Save-Contributors $directory.Name $document
    }
    exit 0
}

$pull = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repository/pulls/$PullRequestNumber"
if (-not $pull.merged_at) { throw "Pull request #$PullRequestNumber is not merged." }
if ($pull.user.type -eq "Bot" -or $pull.user.login -match '\[bot\]$') {
    Write-Host "Bot pull request author is not recorded as a game module contributor."
    exit 0
}
$files = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repository/pulls/$PullRequestNumber/files?per_page=100"
$gameDirectories = @($files.filename | ForEach-Object { if ($_ -match '^games/([^/]+)/' -and $_ -notmatch '/contributors\.generated\.json$') { $Matches[1] } } | Sort-Object -Unique)
if ($gameDirectories.Count -eq 0) { Write-Host "No game module source changed."; exit 0 }

$user = Get-GitHubUser $pull.user.login
$date = ([DateTimeOffset]$pull.merged_at).UtcDateTime.ToString("yyyy-MM-dd")
foreach ($gameDirectory in $gameDirectories) {
    $path = Join-Path $repoRoot "games\$gameDirectory\contributors.generated.json"
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing generated contributor document for $gameDirectory." }
    $document = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $existing = @($document.contributors | Where-Object githubLogin -eq $user.login | Select-Object -First 1)
    if ($existing.Count -eq 0) {
        $document.contributors += [pscustomobject]@{
            githubId = $user.id
            githubLogin = $user.login
            displayName = if ([string]::IsNullOrWhiteSpace($user.name)) { $user.login } else { $user.name }
            profileUrl = $user.html_url
            firstContributionDate = $date
            latestContributionDate = $date
        }
    } else {
        $existing[0].githubId = $user.id
        $existing[0].displayName = if ([string]::IsNullOrWhiteSpace($user.name)) { $user.login } else { $user.name }
        $existing[0].profileUrl = $user.html_url
        if ($date -lt $existing[0].firstContributionDate) { $existing[0].firstContributionDate = $date }
        if ($date -gt $existing[0].latestContributionDate) { $existing[0].latestContributionDate = $date }
    }
    Save-Contributors $gameDirectory $document
}
