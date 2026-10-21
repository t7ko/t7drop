<#
.SYNOPSIS
Builds dist\T7Drop.exe (in the repository's dist folder) and runs the tests.

.DESCRIPTION
Uses the C# compiler of .NET Framework 4.8, which is part of Windows 10 and 11, so nothing has
to be installed and the exe runs on any such Windows (x64). That compiler understands C# 5
only: no $"..." strings, ?. or => members.

The version is versionName in android/app/build.gradle.kts: one version for both apps
(docs/design.md, "Releases"). It goes into obj\Version.cs. lib\libsodium.dll is embedded in
the exe. The exe is built into windows\obj first, and the tests check it (Tests.exe test);
only then is it copied to dist\.
#>
$ErrorActionPreference = 'Stop'
$windows = Split-Path -Parent $PSScriptRoot
$root = Split-Path -Parent $windows
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $windows 'src'
$obj = Join-Path $windows 'obj'
$dist = Join-Path $root 'dist'
$exe = Join-Path $dist 'T7Drop.exe'
New-Item -ItemType Directory -Force $obj, $dist | Out-Null

$gradle = Get-Content (Join-Path $root 'android/app/build.gradle.kts') -Raw
if ($gradle -notmatch 'versionName = "(\d+)\.(\d+)\.(\d+)"') { throw 'versionName (x.y.z) not found in android/app/build.gradle.kts.' }
$version = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$versionFile = Join-Path $obj 'Version.cs'
$versionCode = "// Written by scripts/build.ps1 from versionName in android/app/build.gradle.kts.`r`n" +
    "[assembly: System.Reflection.AssemblyVersion(`"$version`")]`r`n" +
    "[assembly: System.Reflection.AssemblyFileVersion(`"$version`")]`r`n"
[IO.File]::WriteAllText($versionFile, $versionCode, (New-Object Text.UTF8Encoding $false))

$sources = @(Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }) + $versionFile
$tests = @(Get-ChildItem (Join-Path $windows 'tests') -Filter *.cs | ForEach-Object { $_.FullName })
$sodium = Join-Path $windows 'lib\libsodium.dll'
$common = @('/nologo', '/codepage:65001', '/optimize+', '/warnaserror+', '/platform:x64',
    "/resource:$sodium,libsodium.dll",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.Runtime.Serialization.dll', '/r:System.Net.Http.dll', '/r:System.Security.dll',
    '/r:System.Web.dll', '/r:System.Web.Extensions.dll', '/r:System.IO.Compression.dll',
    '/r:System.IO.Compression.FileSystem.dll')

function Invoke-Csc([string[]] $arguments) {
    & $csc @common @arguments
    if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE." }
}

# The exe's icon is the tray icon, drawn by the same code.
$ico = Join-Path $obj 'app.ico'
Add-Type -Path (Join-Path $src 'Icons.cs') -ReferencedAssemblies System.Drawing
[T7Drop.Icons]::WriteIco($ico, [T7Drop.TrayState]::Online, [int[]](16, 20, 24, 32, 40, 48, 64, 256))

$built = Join-Path $obj 'T7Drop.exe'
Invoke-Csc (@('/target:winexe', "/win32manifest:$src\app.manifest", "/win32icon:$ico", "/out:$built") + $sources)

$testExe = Join-Path $obj 'Tests.exe'
Invoke-Csc (@('/target:exe', '/main:T7Drop.Tests.TestMain', "/out:$testExe") + $sources + $tests)
& $testExe test $built $root
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

if (Test-Path $exe) {
    try { [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None').Close() }
    catch { throw "$exe is in use. Exit t7drop (right-click its icon, Exit), then build again." }
}
Copy-Item $built $exe -Force
Write-Host "Built $exe (version $version)"
