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
