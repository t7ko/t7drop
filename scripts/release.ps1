<#
.SYNOPSIS
Publishes the version in android/app/build.gradle.kts as a GitHub release with the APK and
T7Drop.exe.

.DESCRIPTION
Run it on main with everything committed. It builds dist/t7drop-<version>.apk
(android/scripts/build.ps1) and dist/T7Drop.exe (windows/scripts/build.ps1, which takes the
same version and runs the Windows tests), tags the last commit v<version>, pushes main and
the tag (scripts/push.ps1, which first scans the history for private data), creates the
GitHub release and uploads both. Obtainium on the phone finds new releases at
https://github.com/t7ko/t7drop and takes the APK; the Windows app's updater takes T7Drop.exe
from the latest release. Each step is skipped when already done, so the script can be run
again after a failure.

T7Drop.exe is signed with the release key (windows/tests/Signing.cs; docs/security.md) into
T7Drop.exe.sig, which goes up before the exe: from 0.3.0 on, the Windows app installs only a
release whose exe that key signed.

The token is read from the file that github.tokenFile in the git-ignored local.properties
names, the release key from the file that windows.signKeyFile names. Neither is printed.
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
$apkName = "t7drop-$version.apk"
$exeName = 'T7Drop.exe'  # the name the Windows updater looks for (windows/src/Updater.cs)
$sigName = "$exeName.sig"

$properties = Join-Path $root 'local.properties'
function Get-LocalPath([string] $name) {
    $value = Get-Content $properties | Where-Object { $_ -match "^$([regex]::Escape($name))=(.*)$" } |
        ForEach-Object { $Matches[1].Trim().Replace('\:', ':').Replace('\\', '\') } | Select-Object -First 1
    if (-not $value) { throw "local.properties has no $name." }
    $value
}
$signKeyFile = Get-LocalPath 'windows.signKeyFile'
if (-not (Test-Path $signKeyFile)) { throw "The release key $signKeyFile is not there." }

if ((Invoke-Git rev-parse --abbrev-ref HEAD) -ne 'main') { throw 'Release from main.' }
if (Invoke-Git status --porcelain) { throw 'Commit your changes first: a release is built from a commit.' }

$head = Invoke-Git rev-parse HEAD
if (Invoke-Git tag --list $tag) {
    if ((Invoke-Git rev-parse "$tag^{commit}") -ne $head) { throw "$tag already tags another commit; raise versionName." }
} else {
    Invoke-Git tag -a $tag -m "t7drop $version"
}

& (Join-Path $root 'android/scripts/build.ps1')
$apk = Join-Path $root "dist/$apkName"
& (Join-Path $root 'windows/scripts/build.ps1')
$exe = Join-Path $root "dist/$exeName"
if ((Get-Item $exe).VersionInfo.FileVersion -ne "$version.0") { throw "$exe is not version $version." }
# Tests.exe (windows/scripts/build.ps1 built it with the exe) refuses a key other than the one the updater trusts.
& (Join-Path $root 'windows/obj/Tests.exe') sign $signKeyFile $exe
if ($LASTEXITCODE) { throw "Signing $exe failed." }
$sig = "$exe.sig"

& (Join-Path $PSScriptRoot 'push.ps1') main $tag

$token = (Get-Content (Get-LocalPath 'github.tokenFile') -TotalCount 1).Trim()

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

$uploadUrl = $release.upload_url -replace '\{.*\}$', ''
# The signature before the exe: an updater that sees the exe finds its signature too.
foreach ($asset in @(@($apkName, $apk, 'application/vnd.android.package-archive'), @($sigName, $sig, 'text/plain'),
        @($exeName, $exe, 'application/octet-stream'))) {
    if ($release.assets | Where-Object { $_.name -eq $asset[0] }) {
        Write-Host "$($asset[0]) is already attached."
    } else {
        Invoke-RestMethod -Method Post "$($uploadUrl)?name=$($asset[0])" -Headers $headers `
            -ContentType $asset[2] -InFile $asset[1] | Out-Null
    }
}
Write-Host "Released $tag with $apkName, $sigName and $exeName at $($release.html_url)"
