# Claudinine

Silent context optimizer for Claude Code, distributed as a plugin. It compacts
session transcripts at the boundaries where Claude Code re-reads them (session
start/resume/clear, pre-compact), so the next load starts lean. Install and
forget: no dashboards, no reports, no chatter.

Measured on a 95-transcript corpus of real sessions: **152.6 MB → 35.8 MB
(77%)**. Savings scale *with* session size — small sessions are prose-dominated
and compact ~43%, sessions over 3 MB compact ~79% — because the win comes from
tool output, not from text. Byte percentages understate the API-token saving by
roughly 3.5×, since JSON envelope overhead dilutes the bytes but not the tokens.

## What actually earns the savings

Most of the yield is **chain-collapse**: a turn that ran many tool calls becomes
one digest record listing each call with a short preview, and the full outputs
move to a sidecar mirror. The rest comes from the aging and trim family
(age-tiered tool-result stubs, mega-block trim, image strip) and from
record-level housekeeping (superseded file edits, stale reminder blocks, queue
history).

Exact-duplicate deduplication is also implemented (bash reads, `Read` results,
system reminders, documents), but be aware it contributes **~0% on real session
profiles** — identical byte-for-byte re-reads are rare in practice. It is kept
because it is cheap and lossless, not because it is where the win is.

## How it works

- **UserPromptSubmit** — after each of your prompts, the turn that just
  finished is mirrored to a sidecar file, then compacted in the live transcript.
- **SessionEnd** — the final turn gets the same treatment, leaving the file
  clean at rest.
- **SessionStart / PreCompact** — full-scan repair for crash leftovers, plus
  garbage collection of mirrors and orphaned session directories.

Compaction never touches the live in-memory context of a running session —
the payout arrives at the next transcript load ("your next session starts
lean"). Every rewrite is validated before an atomic swap, and any failed check
leaves the original untouched. See [docs/session-file-changes.md](docs/session-file-changes.md)
for exactly what is modified, why, and what the safety guarantees are.

Note: the transcript also backs the session scrollback, so compacted tool
outputs appear as short stubs when you scroll back after a resume. The full
originals are preserved in the per-session mirror, retrievable with
`claudinine get <session-id> --ref <ref> [--grep P | --info | --full | --media]`.
To undo compaction entirely, run `claudinine restore-compaction-off <session-id>`
while the session is closed: the transcript is rebuilt verbatim from the mirror
and the session is left alone from then on (`restore-compaction-on` restores it
once and lets compaction resume).

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
