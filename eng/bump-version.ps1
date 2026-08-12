#!/usr/bin/env pwsh
# Computes the next plugin version from the last one actually released, and
# (unless -WhatIf) writes it via set-version.ps1.
#
# develop carries no version in either file it would otherwise be read from
# (see set-version.ps1) -- the manifest and csproj on develop can never go
# stale because there is nothing in them to go stale. So "current version" is
# not a file read; it is whatever GitHub says was tagged last. -Repo defaults
# to the origin remote's owner/name, overridable for testing against a
# checkout with no such remote.
#
# Called by the release dispatch in cd.yml with the component chosen from the
# dropdown. Because the new version is COMPUTED from the last release rather
# than supplied, a release run cannot target a version that already shipped.
#
# cd.yml uses -WhatIf: it needs the number early (to name the tag and to inject
# into the build) but must not write the bump until the release has published.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Component,

    # owner/repo to query for the last release tag. Defaults to the checkout's
    # own origin remote.
    [string]$Repo,

    # Compute and report the next version without touching either file.
    [switch]$WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Repo) {
    $originUrl = git remote get-url origin
    if ($originUrl -notmatch '[:/]([^/]+)/([^/]+?)(\.git)?$') {
        throw "could not parse owner/repo from origin remote '$originUrl'"
    }
    $Repo = "$($Matches[1])/$($Matches[2])"
}

# No prior release at all (a brand-new repo) starts at 0.0.0, so a first patch
# bump produces 0.0.1. gh exits non-zero when there are no releases (or, in
# practice, whenever it fails); that is the one case this treats as "no
# current version" rather than an error. Diagnosed against three failing CI
# runs: this is a dot-sourced script (cd.yml's `pwsh -command ". '{0}'"`), and
# a dot-sourced script's LAST NATIVE COMMAND's exit code becomes the whole
# step's exit code regardless of what PowerShell itself does afterward --
# there was no thrown exception (a try/catch around this never caught
# anything), just a stale $LASTEXITCODE=1 from this gh call surviving all the
# way to the end of the file. `exit 0` after handling the failure is not
# optional cleanup here, it is the actual fix.
$latestTag = gh release list --repo $Repo --limit 1 --json tagName --jq '.[0].tagName' 2>&1
if ($LASTEXITCODE -ne 0 -or -not $latestTag) {
    $current = '0.0.0'
} else {
    $current = $latestTag -replace '^v', ''
}
$global:LASTEXITCODE = 0

if ($current -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    throw "latest release tag '$latestTag' does not parse as vMAJOR.MINOR.PATCH"
}
$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3]

# Bumping a component resets the ones below it.
switch ($Component) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}
$next = "$major.$minor.$patch"

if ($WhatIf) {
    Write-Host "$current -> $next ($Component, not written)"
} else {
    # set-version.ps1 owns the write, into files that hold no version today.
    & (Join-Path $PSScriptRoot 'set-version.ps1') -Version $next | Out-Null
    Write-Host "$current -> $next ($Component)"
}

[pscustomobject]@{
    Previous = $current
    Version  = $next
}
