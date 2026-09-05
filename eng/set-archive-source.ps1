#!/usr/bin/env pwsh
# Points .claude-plugin/marketplace.json at a published release archive.
#
# The `archive` source needs a pinned URL plus its digest, both of which change
# every release, so this rewrite is part of the release flow rather than a
# hand-edit: cd.yml (phase A) runs it on the release branch, so the pin ships
# inside the reviewed release PR. The digest is an integrity check, verified
# again by cd-publish.yml against the staged asset before anything publishes.
#
# The entry also declares `version`, duplicating the zip's plugin.json. That is
# not redundancy for its own sake: an archive entry has no directory inside the
# marketplace clone, so the Desktop app's update detection reads nothing but
# the entry itself and compares its `version` to the installed one. Without it
# the Desktop never shows an update for an archive-sourced plugin (observed
# 2026-09-05, shell 1.46388.4 / CLI 2.1.260 -- see docs/upstream-observations.md).
# The CLI's `plugin update` would fall back to the digest, so only the Desktop
# path depends on this field.
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

# Entry-declared version: the only marketplace-side signal the Desktop reads
# for an archive entry (see header). Must equal the zip's plugin.json version.
$plugin | Add-Member -NotePropertyName version -NotePropertyValue $Version -Force

$plugin.source = [ordered]@{
    source = 'archive'
    url    = $url
    sha256 = $Sha256.ToLowerInvariant()
}

if ($PSCmdlet.ShouldProcess($marketplacePath, "point at $url")) {
    # Trailing newline keeps the file diff-clean against the hand-written original.
    ($market | ConvertTo-Json -Depth 10) + "`n" | Set-Content $marketplacePath -NoNewline -Encoding utf8
    Write-Host "marketplace.json -> $url (version $Version)"
    Write-Host "sha256 = $($Sha256.ToLowerInvariant())"
}
else {
    ($market | ConvertTo-Json -Depth 10)
}
