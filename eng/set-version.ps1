#!/usr/bin/env pwsh
# Writes an explicit plugin version into the two files that carry it.
#
# The version lives in .claude-plugin/plugin.json (the plugin's identity and
# update signal, read by pack-plugin.ps1) and in Claudinine.csproj (compiled
# into the binary, reported by `claudinine version`). They must not drift, so
# this script is the only place either is rewritten -- bump-version.ps1 computes
# the next version and delegates here.
#
# build.yml calls this directly (via its `version` input) to pack a version that
# is not committed anywhere yet, which is what lets cd.yml build and publish
# before touching main.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot '.claude-plugin/plugin.json'
$csprojPath = Join-Path $repoRoot 'src/Claudinine/Claudinine.csproj'

# The manifest holds the authoritative copy.
$manifestRaw = Get-Content $manifestPath -Raw
$manifest = $manifestRaw | ConvertFrom-Json
$current = $manifest.version

if ($current -notmatch '^\d+\.\d+\.\d+$') {
    throw "manifest version '$current' is not major.minor.patch"
}

# Guard against the two files having drifted before the write: silently
# overwriting a mismatch would hide the drift in the released binary.
$csprojRaw = Get-Content $csprojPath -Raw
if ($csprojRaw -notmatch '<Version>([^<]+)</Version>') {
    throw "no <Version> element in $csprojPath"
}
$csprojCurrent = $Matches[1]
if ($csprojCurrent -ne $current) {
    throw "version drift: manifest says '$current' but csproj says '$csprojCurrent'"
}

if ($Version -eq $current) {
    Write-Host "already at $Version"
    return [pscustomobject]@{ Previous = $current; Version = $Version; Changed = $false }
}

# Rewrite in place with a targeted replacement rather than re-serializing the
# JSON, which would reorder keys and reformat the whole manifest.
$manifestUpdated = $manifestRaw -replace "(`"version`"\s*:\s*`")$([regex]::Escape($current))(`")", "`${1}$Version`${2}"
if ($manifestUpdated -eq $manifestRaw) {
    throw "failed to rewrite version in $manifestPath"
}
Set-Content -Path $manifestPath -Value $manifestUpdated -NoNewline

$csprojUpdated = $csprojRaw -replace "<Version>$([regex]::Escape($current))</Version>", "<Version>$Version</Version>"
if ($csprojUpdated -eq $csprojRaw) {
    throw "failed to rewrite version in $csprojPath"
}
Set-Content -Path $csprojPath -Value $csprojUpdated -NoNewline

Write-Host "$current -> $Version"

[pscustomobject]@{
    Previous = $current
    Version  = $Version
    Changed  = $true
}
