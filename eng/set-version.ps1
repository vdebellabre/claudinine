#!/usr/bin/env pwsh
# Writes an explicit plugin version into the two files that carry it.
#
# Between releases both files carry the PREVIOUS release's version, written by
# the last release commit and never by hand -- so this overwrites an existing
# value in place, and inserts the field fresh only on a tree that never had
# one (`version` is optional in plugin.json, and an absent <Version> falls
# back to the SDK's own default).
#
# build.yml calls this directly (via its `version` input) to pack a version
# that is not committed anywhere yet, which is what lets cd.yml build and
# publish before touching main. cd.yml then calls it again to make that same
# write permanent in the release commit.

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

# Manifest: insert "version" right after the top-level "name" if absent,
# overwrite in place if somehow already present (e.g. re-running against a
# tree that already got the write). A generic '"name"\s*:' match would also
# hit the nested author.name -- anchored on the top-level key being the first
# line of the file (as pack-plugin.ps1's own manifest already is), matched
# via -match on that single line rather than a whole-file regex, so this can
# never touch the wrong occurrence.
$manifestLines = Get-Content $manifestPath
if ($manifestLines[0] -notmatch '^\{$' -or $manifestLines[1] -notmatch '^\s*"name"\s*:\s*"[^"]*"') {
    throw "$manifestPath does not start with the expected { / `"name`" shape"
}
$existingVersionLine = $manifestLines | Where-Object { $_ -match '^\s*"version"\s*:\s*"[^"]*",?\s*$' } | Select-Object -First 1
if ($existingVersionLine) {
    $manifestLines = $manifestLines | ForEach-Object {
        if ($_ -eq $existingVersionLine) { $_ -replace '"version"\s*:\s*"[^"]*"', "`"version`": `"$Version`"" }
        else { $_ }
    }
} else {
    $nameLine = $manifestLines[1]
    $insertAfter = if ($nameLine.TrimEnd().EndsWith(',')) { $nameLine } else { $nameLine.TrimEnd() + ',' }
    $manifestLines[1] = $insertAfter
    $manifestLines = @($manifestLines[0], $manifestLines[1], "  `"version`": `"$Version`",") + $manifestLines[2..($manifestLines.Length - 1)]
}
$manifestUpdated = ($manifestLines -join "`n") + "`n"
Set-Content -Path $manifestPath -Value $manifestUpdated -NoNewline

# csproj: same idea, anchored on <AssemblyName> so it lands in the same
# PropertyGroup rather than appended at file scope.
$csprojRaw = Get-Content $csprojPath -Raw
if ($csprojRaw -match '<Version>[^<]*</Version>') {
    $csprojUpdated = $csprojRaw -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
} else {
    if ($csprojRaw -notmatch '<AssemblyName>[^<]*</AssemblyName>') {
        throw "no <AssemblyName> element in $csprojPath to anchor the version insert on"
    }
    $csprojUpdated = $csprojRaw -replace '(<AssemblyName>[^<]*</AssemblyName>)', "`$1`n`t`t<Version>$Version</Version>"
}
if ($csprojUpdated -eq $csprojRaw) {
    throw "failed to write version into $csprojPath"
}
Set-Content -Path $csprojPath -Value $csprojUpdated -NoNewline

Write-Host "-> $Version"

[pscustomobject]@{
    Version = $Version
    Changed = $true
}
