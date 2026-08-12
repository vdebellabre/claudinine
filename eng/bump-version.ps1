#!/usr/bin/env pwsh
# Computes the next plugin version from the current one, and (unless -WhatIf)
# writes it via set-version.ps1.
#
# Called by the release dispatch in cd.yml with the component chosen from the
# dropdown. Because the new version is COMPUTED from the current one rather than
# supplied, a release run cannot target a version that already shipped.
#
# cd.yml uses -WhatIf: it needs the number early (to name the tag and to inject
# into the build) but must not write the bump until the release has published.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Component,

    # Compute and report the next version without touching either file.
    [switch]$WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot '.claude-plugin/plugin.json'

# Read the current version from the manifest: it is the authoritative copy.
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$current = $manifest.version

if ($current -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    throw "manifest version '$current' is not major.minor.patch"
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
    # set-version.ps1 owns the write, including the manifest/csproj drift guard.
    & (Join-Path $PSScriptRoot 'set-version.ps1') -Version $next | Out-Null
    Write-Host "$current -> $next ($Component)"
}

[pscustomobject]@{
    Previous = $current
    Version  = $next
}
