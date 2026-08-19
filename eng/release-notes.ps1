#!/usr/bin/env pwsh
# Extracts a release's changelog section from CHANGELOG.md, and (with -Promote)
# renames the `## Unreleased` heading to that version in place.
#
# The changelog is written on main as work lands, under `## Unreleased`.
# cd.yml calls this twice in the release job: once with -Promote, inside the
# same step that writes the version, so the rename rides the release commit;
# and once plain, to build the body it hands `gh release create`.
#
# Fail-closed on an empty section. A release whose notes are silently blank is
# the failure mode this whole file exists to prevent, and it is cheap to fix
# (write the section, re-dispatch) precisely BECAUSE the version job runs
# before anything is built or pushed.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Rename `## Unreleased` -> `## <Version>` and write the file back.
    [switch]$Promote
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$path = Join-Path $repoRoot 'CHANGELOG.md'
if (-not (Test-Path $path)) { throw "no CHANGELOG.md at $path" }

$lines = @(Get-Content $path)

# Promote first, so the extraction below finds the section under its final
# name whether this run renamed it or a previous hand-edit already did.
#
# The emptiness check runs BEFORE the write: a promote that renames an empty
# Unreleased and THEN throws leaves a permanent empty `## <Version>` section
# behind, and the next run extracts that empty section instead of the real
# one. Validate, then write -- so a failed promote is a no-op on disk.
if ($Promote) {
    # Already-promoted check comes FIRST. cd.yml can legitimately re-run the
    # release job (the asset upload is the one recoverable post-push step), and
    # by then this script has already renamed Unreleased and reopened an empty
    # slot above it -- so an Unreleased-first check would find that empty slot
    # and throw on a release whose notes are in fact already correct.
    $promoted = $lines | Where-Object { $_ -match "^##\s+$([regex]::Escape($Version))\s*$" }
    if ($promoted) {
        Write-Host "[release-notes] '## $Version' already present; nothing to promote"
    } else {
        $idx = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^##\s+Unreleased\s*$') { $idx = $i; break }
        }
        if ($idx -lt 0) {
            throw "CHANGELOG.md has no '## Unreleased' heading and no '## $Version' section"
        }
        $pending = [System.Collections.Generic.List[string]]::new()
        for ($i = $idx + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^##\s+') { break }
            $pending.Add($lines[$i])
        }
        if (-not ($pending -join "`n").Trim()) {
            throw "'## Unreleased' in CHANGELOG.md is empty -- write the release notes there on main and re-dispatch"
        }
        # Rename in place, then reopen an empty Unreleased slot ABOVE it for the
        # next cycle.
        $head = if ($idx -gt 0) { @($lines[0..($idx - 1)]) } else { @() }
        $tail = @($lines[($idx + 1)..($lines.Count - 1)])
        $lines = $head + @('## Unreleased', '', "## $Version") + $tail
        Set-Content -Path $path -Value (($lines -join "`n") + "`n") -NoNewline
        Write-Host "[release-notes] promoted '## Unreleased' -> '## $Version'"
    }
}

# Extract: from the version heading to the next `## ` heading (or EOF).
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^##\s+$([regex]::Escape($Version))\s*$") { $start = $i + 1; break }
}
if ($start -lt 0) { throw "no '## $Version' section in CHANGELOG.md" }

$body = [System.Collections.Generic.List[string]]::new()
for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s+') { break }
    $body.Add($lines[$i])
}

$text = ($body -join "`n").Trim()
if (-not $text) {
    throw "the '## $Version' section in CHANGELOG.md is empty -- write the release notes under '## Unreleased' on main and re-dispatch"
}
$text
