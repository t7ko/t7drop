<#
.SYNOPSIS
Publishes the version in android/app/build.gradle.kts as a GitHub release with its APK.

.DESCRIPTION
Run it on main with everything committed. It builds dist/t7drop-<version>.apk
(android/scripts/build.ps1), tags the last commit v<version>, pushes main and the tag
(scripts/push.ps1, which first scans the history for private data), creates the GitHub
release and uploads the APK. Obtainium on the phone finds new releases at
https://github.com/t7ko/t7drop and takes the APK. Each step is skipped when already done,
so the script can be run again after a failure. The Windows app (M4) will be attached to
the same releases.

The token is read from the file that github.tokenFile in the git-ignored local.properties
names. It is never printed.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = 't7ko/t7drop'

# git writes progress to stderr, which Windows PowerShell would turn into errors.
function Invoke-Git {
    $ErrorActionPreference = 'Continue'
    & git -C $root @args
    if ($LASTEXITCODE) { throw "git $($args -join ' ') failed." }
}

$gradleFile = Get-Content (Join-Path $root 'android/app/build.gradle.kts') -Raw
if ($gradleFile -notmatch 'versionName = "([^"]+)"') { throw 'versionName not found in android/app/build.gradle.kts.' }
$version = $Matches[1]
$tag = "v$version"
$assetName = "t7drop-$version.apk"

if ((Invoke-Git rev-parse --abbrev-ref HEAD) -ne 'main') { throw 'Release from main.' }
if (Invoke-Git status --porcelain) { throw 'Commit your changes first: a release is built from a commit.' }

$head = Invoke-Git rev-parse HEAD
if (Invoke-Git tag --list $tag) {
    if ((Invoke-Git rev-parse "$tag^{commit}") -ne $head) { throw "$tag already tags another commit; raise versionName." }
} else {
    Invoke-Git tag -a $tag -m "t7drop $version"
}

& (Join-Path $root 'android/scripts/build.ps1')
$apk = Join-Path $root "dist/$assetName"

& (Join-Path $PSScriptRoot 'push.ps1') main $tag

$properties = Join-Path $root 'local.properties'
$tokenFile = Get-Content $properties | Where-Object { $_ -match '^github\.tokenFile=(.*)$' } |
    ForEach-Object { $Matches[1].Trim().Replace('\:', ':') } | Select-Object -First 1
$token = (Get-Content $tokenFile -TotalCount 1).Trim()

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
}
try {
    $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/tags/$tag" -Headers $headers
} catch {
    if ($_.Exception.Response.StatusCode -ne 'NotFound') { throw }
    $previous = Invoke-Git tag --list 'v*' --sort=-version:refname | Where-Object { $_ -ne $tag } | Select-Object -First 1
    $range = if ($previous) { "$previous..$tag" } else { $tag }
    $notes = (Invoke-Git log --format='- %s' $range) -join "`n"
    $body = @{ tag_name = $tag; name = "t7drop $version"; body = $notes } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post "https://api.github.com/repos/$repo/releases" -Headers $headers `
        -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
}

if ($release.assets | Where-Object { $_.name -eq $assetName }) {
    Write-Host "$assetName is already attached."
} else {
    $uploadUrl = $release.upload_url -replace '\{.*\}$', ''
    Invoke-RestMethod -Method Post "$($uploadUrl)?name=$assetName" -Headers $headers `
        -ContentType 'application/vnd.android.package-archive' -InFile $apk | Out-Null
}
Write-Host "Released $tag with $assetName at $($release.html_url)"
