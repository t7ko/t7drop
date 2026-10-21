<#
.SYNOPSIS
Pushes to GitHub (origin) with the token: main, or the refs given as arguments.

.DESCRIPTION
Usage: scripts\push.ps1 [ref ...]. Annotated tags that point into the pushed commits go
along (--follow-tags). release.ps1 uses it to push main and the release tag.

This repository is public: first tools/scan_private.py --history checks the whole history
for anything that gives access to the server (see AGENTS.md), and nothing is pushed if it
finds something.

The token (fine-grained, Contents read and write on this repository only) is read from the
file that github.tokenFile in the git-ignored local.properties names. It reaches git as an
HTTP header through GIT_CONFIG_* environment variables, so it never appears on a command
line.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# @() keeps a single ref a list: splatting a lone string would pass its letters.
$refs = @(if ($args.Count) { $args } else { 'main' })

$ErrorActionPreference = 'Continue'
& python -I (Join-Path $root 'tools/scan_private.py') --history
$scan = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($scan) { throw 'scan_private.py found something private in the history (above): nothing pushed.' }

$properties = Join-Path $root 'local.properties'
$tokenFile = if (Test-Path $properties) {
    Get-Content $properties | Where-Object { $_ -match '^github\.tokenFile=(.*)$' } |
        ForEach-Object { $Matches[1].Trim().Replace('\:', ':') } | Select-Object -First 1
}
if (-not $tokenFile) { throw 'local.properties has no github.tokenFile.' }
$token = (Get-Content $tokenFile -TotalCount 1).Trim()

$env:GIT_CONFIG_COUNT = '1'
$env:GIT_CONFIG_KEY_0 = 'http.https://github.com/.extraHeader'
$env:GIT_CONFIG_VALUE_0 = 'Authorization: Basic ' +
    [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$token"))
try {
    # git writes progress to stderr, which Windows PowerShell would turn into errors.
    $ErrorActionPreference = 'Continue'
    & git -C $root -c credential.helper= push --follow-tags origin @refs
    if ($LASTEXITCODE) { throw "git push origin $($refs -join ' ') failed." }
} finally {
    Remove-Item Env:GIT_CONFIG_COUNT, Env:GIT_CONFIG_KEY_0, Env:GIT_CONFIG_VALUE_0
}
