# Claudinine

Silent context optimizer for Claude Code, distributed as a plugin. It compacts
session transcripts at the boundaries where Claude Code re-reads them (session
start/resume/clear, pre-compact), so the next load starts lean. Install and
forget: no dashboards, no reports, no chatter.

## How it works

- **UserPromptSubmit** — after each of your prompts, the turn that just
  finished is mirrored to a sidecar file, then compacted in the live
  transcript (duplicate bash reads deduplicated, later: archive stubs and
  chain-collapse).
- **SessionEnd** — the final turn gets the same treatment, leaving the file
  clean at rest.
- **SessionStart / PreCompact** — full-scan repair for crash leftovers, plus
  mirror garbage collection.

Compaction never touches the live in-memory context of a running session —
the payout arrives at the next transcript load ("your next session starts
lean"). Every rewrite is validated (all lines parse, uuid chain intact, tail
record preserved) before an atomic swap; anything unexpected means the file is
left untouched.

Note: the transcript also backs the session scrollback, so compacted tool
outputs appear as short stubs when you scroll back after a resume. The full
originals are always preserved in the per-session mirror.

## Engineering

C# / .NET 10, Native AOT, zero NuGet dependencies. One small native binary per
platform (6 targets), committed under `bin/<rid>/` and routed by a dual shim
(`bin/claudinine` for POSIX shells, `bin/claudinine.cmd` for cmd.exe).

## Distribution

CI builds all six targets on every push to `main` (Native AOT cannot
cross-compile, so the matrix *is* the release build) and publishes them as one
zip release asset, `claudinine-<version>.zip`, built by `eng/pack-plugin.ps1`.
The archive carries only the runtime payload — manifest, hooks, shims, binaries
— which is ~8.7 MB against a ~250 MB working tree.

That asset is what the marketplace is meant to serve, via an `archive` source
pinned to the release URL and its SHA-256 (`eng/set-archive-source.ps1` writes
both). Installing then needs no git clone, which matters because this repo's
`.git` carries every binary ever shipped and grows ~18 MB per release.

The switch is staged, not live: `archive` sources require Claude Code 2.1.224+,
and versions 2.1.120–2.1.223 refuse to install them (older ones fail to load the
marketplace at all). Until the desktop app ships 2.1.224+, `marketplace.json`
stays on the relative-path source and CI skips the pin step. Flipping it is one
`eng/set-archive-source.ps1` run.
