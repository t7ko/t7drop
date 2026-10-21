<#
.SYNOPSIS
Runs the unit tests and builds dist/t7drop-<version>.apk (in the repository root), a signed release build.

.DESCRIPTION
The APK is built from the working tree and signed with the release key that
android/local.properties points to (release.storeFile, release.storePasswordFile,
release.keyAlias). Every APK given to the phone must be signed with that same key, or
Android refuses the update. For tries on the emulator, gradlew.bat assembleDebug is enough.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $env:JAVA_HOME) {
    # local.properties may name the JDK (java.home=...), as it names the SDK.
    $javaHome = Select-String -Path (Join-Path $root 'local.properties') -Pattern '^java[.]home=(.*)$' |
        Select-Object -First 1
    if ($javaHome) { $env:JAVA_HOME = $javaHome.Matches[0].Groups[1].Value.Replace('\:', ':') }
}

$gradleFile = Get-Content (Join-Path $root 'app/build.gradle.kts') -Raw
if ($gradleFile -notmatch 'versionName = "([^"]+)"') { throw 'versionName not found in app/build.gradle.kts.' }
$version = $Matches[1]

if (-not (Select-String -Path (Join-Path $root 'local.properties') -Pattern '^release\.storeFile=' -Quiet)) {
    throw 'local.properties has no release.storeFile; release builds would be unsigned.'
}
if (git -C $root status --porcelain) {
    Write-Warning 'The working tree has uncommitted changes; the APK includes them.'
}

# Gradle writes warnings to stderr, which Windows PowerShell would turn into errors.
$ErrorActionPreference = 'Continue'
& (Join-Path $root 'gradlew.bat') -p $root --console=plain testDebugUnitTest assembleRelease
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE) { throw 'The Gradle build failed.' }

$dist = Join-Path (Split-Path -Parent $root) 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$apk = Join-Path $dist "t7drop-$version.apk"
Copy-Item (Join-Path $root 'app/build/outputs/apk/release/app-release.apk') $apk -Force
Write-Host "Built $apk"
