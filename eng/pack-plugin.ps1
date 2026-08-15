#!/usr/bin/env pwsh
# Packages the plugin for distribution, and prints the SHA-256 that
# marketplace.json must pin (CLI archive) or that release notes record.
#
# Two artifacts from one canonical layout (executables live in libexec/, never
# in a top-level bin/ -- the claude.ai plugin validator refuses any hosted
# plugin shipping bin/, because bin/ is auto-added to the Bash tool's PATH
# without appearing on the admin approval surface):
#
#   default        claudinine-<v>.zip     CLI `archive`-source install. Adds a
#                                         bin/ with 2-line forwarders to
#                                         ../libexec so the human verbs stay on
#                                         PATH (`claudinine version`, ...).
#   -Hosted        claudinine-<v>.plugin  claude.ai account upload. Drops bin/
#                                         -- no PATH entry at all; retrieval
#                                         goes through the per-session launcher
#                                         the compactor writes (see
#                                         src/Claudinine/Mirror/Launcher.cs).
#                                         Carries ALL SIX RIDs: cloud sessions
#                                         are Linux containers, but local
#                                         ("On your computer") Cowork executes
#                                         hooks on the DESKTOP HOST (measured
#                                         2026-08-15 on Windows: Git Bash ran
#                                         the shim, win-x64 was absent, every
#                                         hook died exit 127), and the same
#                                         account artifact feeds both modes.
#                                         Pass -HostedRids to slim deliberately.
#
# The archive carries only what an installed plugin needs at runtime: the
# manifest, the hooks, the commands, the shims, and the six native binaries.
# Source and tests are deliberately excluded -- they are what makes a git clone
# of this repo heavy, which is the whole reason this archive exists.
#
# Layout inside the archive (contents at the root, not wrapped in a folder):
#   .claude-plugin/plugin.json
#   hooks/hooks.json
#   commands/*.md
#   README.md
#   libexec/claudinine, libexec/<rid>/claudinine[.exe]
#   libexec/claudinine.cmd                      (only when a win-* RID ships)
#   bin/claudinine, bin/claudinine.cmd          (CLI zip only, forwarders)
#
# Usage:
#   eng/pack-plugin.ps1 -BinRoot <dir> [-OutDir <dir>] [-Hosted]
#
# -BinRoot is required: a directory holding one subdirectory per RID (the CI
# artifact download layout). The binaries are not committed, and Native AOT
# cannot cross-compile, so CI's six-RID matrix is the only place a complete set
# exists. The shims are read from eng/shims/ regardless.

[CmdletBinding()]
param(
    [string] $BinRoot,
    [string] $OutDir,
    [switch] $Hosted,
    [string[]] $HostedRids
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

# The hosted bundle carries all six RIDs. It briefly (0.1.20-0.1.21) shipped
# Linux-only on the theory that claude.ai-hosted sessions are Linux containers
# -- true for cloud, but the SAME account-uploaded .plugin feeds local
# ("On your computer") Cowork, where hooks execute on the desktop host, not in
# the Linux sandbox VM (measured 2026-08-15: Windows host, Git Bash invoked the
# sh shim, `uname -s`=MINGW* routed to win-x64/claudinine.exe, absent, exit 127
# on every hook -- docs/cowork.md O12). Full set is 20.34 MB unpacked against
# a 64 MB sync cap (O10), so the cut bought little and broke a whole mode.
#
# -HostedRids remains as the deliberate-slimming override.
$allRids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
# Split on commas: `pwsh -File script.ps1 -HostedRids a,b` passes ONE argument
# ("a,b"), unlike an in-process call which binds two. CI invokes via -File, so
# without this the whole list arrives as a single bogus RID name.
$HostedRids = @($HostedRids | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$rids = if ($HostedRids) { $HostedRids } else { $allRids }
$unknown = $rids | Where-Object { $_ -notin $allRids }
if ($unknown) { throw "unknown RID(s): $($unknown -join ', '); valid: $($allRids -join ', ')" }

# Version is the plugin's identity and the update signal: the archive file name
# embeds it, so a pinned URL changes every release. develop carries no version
# at all (see set-version.ps1) -- only a tree cd.yml has already injected a
# real version into (via build.yml's `version` input) has one. CI packs
# develop directly with no injection, purely to verify the archive still
# assembles and extracts; "dev" makes that build's non-release nature visible
# in its own file name rather than asserting a version CI was never given.
$manifestPath = Join-Path $repo '.claude-plugin/plugin.json'
$manifestVersion = (Get-Content $manifestPath -Raw | ConvertFrom-Json).PSObject.Properties['version']
$version = if ($manifestVersion) { $manifestVersion.Value } else { 'dev' }

$stage = Join-Path ([System.IO.Path]::GetTempPath()) "claudinine-pack-$([System.Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $stage | Out-Null

try {
    New-Item -ItemType Directory -Force -Path (Join-Path $stage '.claude-plugin') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'hooks') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'libexec') | Out-Null

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
    # at libexec/ in the archive, which is where hooks.json invokes them from.
    Copy-Item (Join-Path $repo 'eng/shims/claudinine') (Join-Path $stage 'libexec/claudinine')
    # The .cmd half of the dual-shim pattern is only reachable from a Windows
    # host; a bundle carrying no win-* binary has nothing for it to route to.
    if ($rids | Where-Object { $_.StartsWith('win-') }) {
        Copy-Item (Join-Path $repo 'eng/shims/claudinine.cmd') (Join-Path $stage 'libexec/claudinine.cmd')
    }

    foreach ($rid in $rids) {
        $exe = if ($rid.StartsWith('win-')) { 'claudinine.exe' } else { 'claudinine' }
        $src = Join-Path $BinRoot (Join-Path $rid $exe)
        if (-not (Test-Path $src)) { throw "missing binary for ${rid}: $src" }
        New-Item -ItemType Directory -Force -Path (Join-Path $stage "libexec/$rid") | Out-Null
        Copy-Item $src (Join-Path $stage "libexec/$rid/$exe")
    }

    # bin/ is CLI-archive-only PATH convenience for the human verbs; the hosted
    # bundle MUST NOT carry it (validator refusal, see header).
    if (-not $Hosted) {
        New-Item -ItemType Directory -Force -Path (Join-Path $stage 'bin') | Out-Null
        Copy-Item (Join-Path $repo 'eng/shims/bin-forward') (Join-Path $stage 'bin/claudinine')
        Copy-Item (Join-Path $repo 'eng/shims/bin-forward.cmd') (Join-Path $stage 'bin/claudinine.cmd')
    }

    # Resolve to an absolute path: the zip CLI below runs with the staging dir as
    # its cwd, so a relative -OutDir would land inside the staging dir (or fail
    # outright, as `zip` exit 15 "cannot open output file").
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $OutDir = (Resolve-Path $OutDir).Path
    # The account upload expects the `.plugin` extension; the marketplace
    # `archive` source expects a zip. Same format, different name.
    $ext = if ($Hosted) { 'plugin' } else { 'zip' }
    $zip = Join-Path $OutDir "claudinine-$version.$ext"
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
            # Executable bits for the shims and every POSIX binary.
            & chmod '+x' 'libexec/claudinine'
            foreach ($rid in $rids | Where-Object { -not $_.StartsWith('win-') }) {
                & chmod '+x' "libexec/$rid/claudinine"
            }
            $entries = @('.claude-plugin', 'hooks', 'libexec', 'README.md')
            if (-not $Hosted) {
                & chmod '+x' 'bin/claudinine'
                $entries += 'bin'
            }
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
