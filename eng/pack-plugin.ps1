#!/usr/bin/env pwsh
# Packages the plugin as a zip for `archive`-source distribution, and prints
# the SHA-256 that marketplace.json must pin.
#
# The archive carries only what an installed plugin needs at runtime: the
# manifest, the hooks, the commands, the shims, and the six native binaries.
# Source and tests are deliberately excluded -- they are what makes a git clone
# of this repo heavy, which is the whole reason this archive exists.
#
# Layout inside the zip (contents at the root, not wrapped in a folder):
#   .claude-plugin/plugin.json
#   hooks/hooks.json
#   commands/*.md
#   README.md
#   bin/claudinine, bin/claudinine.cmd, bin/<rid>/claudinine[.exe]
#
# Usage:
#   eng/pack-plugin.ps1 -BinRoot <dir> [-OutDir <dir>]
#
# -BinRoot is required: a directory holding one subdirectory per RID (the CI
# artifact download layout). The binaries are not committed, and Native AOT
# cannot cross-compile, so CI's six-RID matrix is the only place a complete set
# exists. The shims are read from eng/shims/ regardless.

[CmdletBinding()]
param(
    [string] $BinRoot,
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir)  { $OutDir  = Join-Path $repo 'artifacts' }
# No default for -BinRoot: the six binaries are no longer committed, so there is
# nowhere in the tree to fall back to. Native AOT cannot cross-compile, so only
# CI (or a caller that has gathered all six RIDs itself) can pack a full archive.
if (-not $BinRoot) {
    throw '-BinRoot is required: pass the directory holding one subdirectory per RID (CI passes the downloaded artifacts).'
}

# Absolute from here on: the staging copy below reads from $BinRoot while the zip
# CLI runs with a different cwd, so relative inputs must not be re-resolved.
if (-not (Test-Path $BinRoot)) { throw "BinRoot does not exist: $BinRoot" }
$BinRoot = (Resolve-Path $BinRoot).Path

$rids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')

# Version is the plugin's identity and the update signal: the archive file name
# embeds it, so a pinned URL changes every release.
$manifestPath = Join-Path $repo '.claude-plugin/plugin.json'
$version = (Get-Content $manifestPath -Raw | ConvertFrom-Json).version
if (-not $version) { throw "no version in $manifestPath" }

$stage = Join-Path ([System.IO.Path]::GetTempPath()) "claudinine-pack-$([System.Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $stage | Out-Null

try {
    New-Item -ItemType Directory -Force -Path (Join-Path $stage '.claude-plugin') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'hooks') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'bin') | Out-Null

    Copy-Item $manifestPath (Join-Path $stage '.claude-plugin/plugin.json')
    Copy-Item (Join-Path $repo 'hooks/hooks.json') (Join-Path $stage 'hooks/hooks.json')
    Copy-Item (Join-Path $repo 'README.md') (Join-Path $stage 'README.md')

    # Slash commands. Copied as a directory because the set grows: a missing
    # commands/ dir in the zip is a silent feature loss for installed users --
    # the plugin works, minus every command.
    $commandsSrc = Join-Path $repo 'commands'
    if (Test-Path $commandsSrc) {
        Copy-Item $commandsSrc (Join-Path $stage 'commands') -Recurse
    }
    # The shims are hand-written source (eng/shims/), not build output; they land
    # at bin/ in the archive, which is where hooks.json invokes them from.
    Copy-Item (Join-Path $repo 'eng/shims/claudinine') (Join-Path $stage 'bin/claudinine')
    Copy-Item (Join-Path $repo 'eng/shims/claudinine.cmd') (Join-Path $stage 'bin/claudinine.cmd')

    foreach ($rid in $rids) {
        $exe = if ($rid.StartsWith('win-')) { 'claudinine.exe' } else { 'claudinine' }
        $src = Join-Path $BinRoot (Join-Path $rid $exe)
        if (-not (Test-Path $src)) { throw "missing binary for ${rid}: $src" }
        New-Item -ItemType Directory -Force -Path (Join-Path $stage "bin/$rid") | Out-Null
        Copy-Item $src (Join-Path $stage "bin/$rid/$exe")
    }

    # Resolve to an absolute path: the zip CLI below runs with the staging dir as
    # its cwd, so a relative -OutDir would land inside the staging dir (or fail
    # outright, as `zip` exit 15 "cannot open output file").
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $OutDir = (Resolve-Path $OutDir).Path
    $zip = Join-Path $OutDir "claudinine-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    # `zip` preserves the Unix executable bit; Compress-Archive does not store
    # permissions at all. Whether Claude Code's extractor honours the bit is
    # undocumented, so the shim is also invoked via `sh` by Claude Code on
    # Windows and the SessionStart hook re-chmods on POSIX -- but shipping the
    # bit when we can costs nothing and is the difference between a working and
    # a broken install on macOS/Linux.
    $useZipCli = $null -ne (Get-Command zip -ErrorAction SilentlyContinue)
    if ($useZipCli) {
        Push-Location $stage
        try {
            # Executable bits for the shim and every POSIX binary.
            & chmod '+x' 'bin/claudinine'
            foreach ($rid in $rids | Where-Object { -not $_.StartsWith('win-') }) {
                & chmod '+x' "bin/$rid/claudinine"
            }
            $entries = @('.claude-plugin', 'hooks', 'bin', 'README.md')
            if (Test-Path 'commands') { $entries += 'commands' }
            & zip -q -r -X $zip @entries
            if ($LASTEXITCODE -ne 0) { throw "zip failed with exit code $LASTEXITCODE" }
        }
        finally { Pop-Location }
    }
    else {
        Write-Warning 'zip CLI not found; falling back to Compress-Archive (Unix executable bits will NOT be stored)'
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    }

    $sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = [math]::Round((Get-Item $zip).Length / 1MB, 2)

    [pscustomobject]@{
        Version = $version
        Zip     = $zip
        SizeMB  = $size
        Sha256  = $sha
        Bits    = if ($useZipCli) { 'preserved' } else { 'not-stored' }
    }
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
