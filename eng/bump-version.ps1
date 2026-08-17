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
# bump produces 0.0.1. That fallback applies ONLY to a clean empty answer
# (exit 0, no output). A FAILING `gh release list` must never reach it:
# during the 2026-08-17 GitHub outage a 503 here fell back to 0.0.0 and a
# minor bump published a bogus "Release 0.1.0" commit + stray v0.1.0 tag to
# main (run 32053146604) -- an API error is not "no releases", it is "we do
# not know the current version", and the only safe answer is to fail the run
# before anything is built or pushed. Transient errors get a few retries.
#
# $global:LASTEXITCODE zeroing on the success path is load-bearing, not
# cleanup. Diagnosed against three failing CI runs: this is a dot-sourced
# script (cd.yml's `pwsh -command ". '{0}'"`), and a dot-sourced script's
# LAST NATIVE COMMAND's exit code becomes the whole step's exit code
# regardless of what PowerShell itself does afterward.
$latestTag = $null
for ($attempt = 1; $attempt -le 5; $attempt++) {
    $latestTag = gh release list --repo $Repo --limit 1 --json tagName --jq '.[0].tagName' 2>&1
    Write-Host "[bump-version] repo=$Repo attempt=$attempt exit=$LASTEXITCODE raw-output=[$latestTag]"
    if ($LASTEXITCODE -eq 0) { break }
    if ($attempt -lt 5) { Start-Sleep -Seconds (15 * $attempt) }
}
if ($LASTEXITCODE -ne 0) {
    throw "gh release list failed after 5 attempts; refusing to guess the current version (last output: $latestTag)"
}
$current = if ($latestTag) { $latestTag -replace '^v', '' } else { '0.0.0' }
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
