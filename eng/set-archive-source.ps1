#!/usr/bin/env pwsh
# Points .claude-plugin/marketplace.json at a published release archive.
#
# The `archive` source needs a pinned URL plus its digest, both of which change
# every release, so this rewrite is part of the release flow rather than a
# hand-edit. `version` stays declared in plugin.json, which makes the version
# string (not the digest) the update signal -- so the digest exists purely as an
# integrity check.
#
# Requires Claude Code v2.1.224+ on the consumer side: 2.1.120-2.1.223 refuse to
# install an archive-source plugin, and older versions fail to load the whole
# marketplace. Keep the relative-path source until that floor is met.
#
# Usage:
#   eng/set-archive-source.ps1 -Sha256 <hex64> [-Version <v>] [-Repo owner/name] [-WhatIf]

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $Sha256,
    [string] $Version,
    [string] $Repo = 'vdebellabre/claudinine'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$marketplacePath = Join-Path $repoRoot '.claude-plugin/marketplace.json'
$manifestPath = Join-Path $repoRoot '.claude-plugin/plugin.json'

if (-not $Version) {
    $Version = (Get-Content $manifestPath -Raw | ConvertFrom-Json).version
}
if (-not $Version) { throw 'no version supplied and none in plugin.json' }

# Pinned, immutable asset URL: the release tag and file name both carry the
# version, so the digest below always describes exactly these bytes.
$url = "https://github.com/$Repo/releases/download/v$Version/claudinine-$Version.zip"

$market = Get-Content $marketplacePath -Raw | ConvertFrom-Json
$plugin = $market.plugins | Where-Object { $_.name -eq 'claudinine' }
if (-not $plugin) { throw "no 'claudinine' plugin entry in $marketplacePath" }

$plugin.source = [ordered]@{
    source = 'archive'
    url    = $url
    sha256 = $Sha256.ToLowerInvariant()
}

if ($PSCmdlet.ShouldProcess($marketplacePath, "point at $url")) {
    # Trailing newline keeps the file diff-clean against the hand-written original.
    ($market | ConvertTo-Json -Depth 10) + "`n" | Set-Content $marketplacePath -NoNewline -Encoding utf8
    Write-Host "marketplace.json -> $url"
    Write-Host "sha256 = $($Sha256.ToLowerInvariant())"
}
else {
    ($market | ConvertTo-Json -Depth 10)
}
