#!/usr/bin/env pwsh
# Bumps the plugin version in the two files that carry it.
#
# The version lives in .claude-plugin/plugin.json (the plugin's identity and
# update signal, read by pack-plugin.ps1) and in Claudinine.csproj (compiled
# into the binary, reported by `claudinine version`). They must not drift, so
# this script is the only sanctioned way to change either.
#
# Called by the release dispatch in ci.yml with the component chosen from the
# dropdown. Because the new version is COMPUTED from the current one rather
# than supplied, a release run cannot target a version that already shipped.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Component
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot '.claude-plugin/plugin.json'
$csprojPath = Join-Path $repoRoot 'src/Claudinine/Claudinine.csproj'

# Read the current version from the manifest: it is the authoritative copy.
$manifestRaw = Get-Content $manifestPath -Raw
$manifest = $manifestRaw | ConvertFrom-Json
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

# Guard against the two files having drifted before the bump: silently
# overwriting a mismatch would hide the drift in the released binary.
$csprojRaw = Get-Content $csprojPath -Raw
if ($csprojRaw -notmatch '<Version>([^<]+)</Version>') {
    throw "no <Version> element in $csprojPath"
}
$csprojCurrent = $Matches[1]
if ($csprojCurrent -ne $current) {
    throw "version drift: manifest says '$current' but csproj says '$csprojCurrent'"
}

# Rewrite in place with a targeted replacement rather than re-serializing the
# JSON, which would reorder keys and reformat the whole manifest.
$manifestUpdated = $manifestRaw -replace "(`"version`"\s*:\s*`")$([regex]::Escape($current))(`")", "`${1}$next`${2}"
if ($manifestUpdated -eq $manifestRaw) {
    throw "failed to rewrite version in $manifestPath"
}
Set-Content -Path $manifestPath -Value $manifestUpdated -NoNewline

$csprojUpdated = $csprojRaw -replace "<Version>$([regex]::Escape($current))</Version>", "<Version>$next</Version>"
if ($csprojUpdated -eq $csprojRaw) {
    throw "failed to rewrite version in $csprojPath"
}
Set-Content -Path $csprojPath -Value $csprojUpdated -NoNewline

Write-Host "$current -> $next ($Component)"

[pscustomobject]@{
    Previous = $current
    Version  = $next
}
