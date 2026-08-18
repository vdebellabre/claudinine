# Upstream observations

Claudinine parses Claude Code's own session transcripts, so an upstream update can
break it silently. This file is the record of which upstream versions have actually
been looked at, and what was found. It is **append-only, hand-written, and gates
nothing** — no code reads it. Its only job is to answer "what changed since the last
time I checked?" without re-deriving the answer from scratch.

Add an entry when a version bump is noticed. An entry saying "read the changelog, no
territory touched" is worth writing: the value is in the *continuity*, not the detail.

## What to observe

Three tracks move independently. The first two have version numbers; the third is the
one that actually matters and does not.

| Track | How to read it |
|---|---|
| Desktop shell | `ls %LOCALAPPDATA%\AnthropicClaude` → `app-<ver>` dirs (newest mtime = current) |
| Embedded CLI | `%APPDATA%\Claude\claude-code\<ver>\claude.exe --version`; `.payload` holds a sha256 build identity |
| Transcript format | empirically, from a live session's JSONL — see `session-file-changes.md`, `parallel-batch-transcript-format.md` |

A shell bump alone has never moved the format. A CLI bump is the one to take seriously.

### Digging into the CLI bundle

The CLI is a single ~300 MB bundled exe with no CHANGELOG inside, but byte-regex over
it does resolve every transcript key we depend on, which beats reading release notes:

```bash
python -c "
import re
d=open('claude.exe','rb').read()
for k in [b'preservedSegment', b'isSidechain', b'toolUseResult', b'SubagentStop']:
    print(k.decode(), len(re.findall(re.escape(k), d)))
"
```

Three cautions, all learned the hard way:

- **`strings` returns nothing useful** on these bundles (and on the shell's `app.asar`).
  Use Python `re.finditer` over the raw bytes.
- **Match counts drift between builds with no format change.** 2.1.227 → 2.1.229 moved
  `toolUseResult` 98 → 100 and `PreCompact` 45 → 48 while the format was identical. A
  count delta is a prompt to go look, never a finding on its own. Presence/absence of a
  key is the signal worth trusting.
- **Old versions are pruned eventually.** Squirrel keeps a couple of `app-*` dirs and the
  CLI keeps a couple of version dirs, so a differential read is only possible for a while
  after the bump. If a bump looks interesting, diff it while both sides are still on disk.

## Observations

### 2026-08-18 — shell 1.30096.5 → 1.32352.1, CLI unchanged at 2.1.229

Noticed because the desktop app updated itself. **Shell-only bump; nothing to do.**

- Shell: `app-1.32352.1` (mtime 2026-08-18 08:38), replacing `app-1.30096.5`
  (2026-08-15). `app-1.30096.1` also still on disk. Package
  `AnthropicClaude-1.32352.1-full.nupkg`, 231,072,344 B.
- CLI: **untouched** — `%APPDATA%\Claude\claude-code\` still holds only `2.1.227` and
  `2.1.229`, both mtime 2026-08-14. The 2.1.229 exe is the one actually running
  (confirmed by process path), `--version` → `2.1.229 (Claude Code)`,
  sha256 `5736c66be98a372d5e5e3b3598ead89ab5a9d1aca60d347fe7b561801c58376c`,
  307,186,848 B. (2.1.227 predates the `.payload` metadata file, so no sha for it.)
- Format: not re-verified, and deliberately so — the CLI did not move, and the format
  travels with the CLI, not the shell.
- Differential read over the two retained CLI bundles found no key appearing or
  disappearing across 2.1.227 → 2.1.229: `preservedSegment` 16/16, `isSidechain` 81/81,
  `sidechain` 8/8, `compactMetadata` 54/54, with the count drift noted above on
  `toolUseResult` and `PreCompact`. `mirrorOf` absent from both, as expected — that key
  is ours, not upstream's.

**Baseline going forward: shell 1.32352.1, CLI 2.1.229.**

### Earlier, reconstructed from scattered notes

These predate this file and were recorded prose-style elsewhere; kept here so the trail
starts before 2026-08-18 rather than at it. Each points at its source rather than
restating it.

- **2.1.222** — parallel-batch transcript format captured empirically (batch
  serialization, chain forks, subagent file layout). The corpus this file's format
  assumptions rest on. See `parallel-batch-transcript-format.md`,
  `session-file-changes.md:108`.
- **2.1.223–227** — changelog surveyed 2026-08-12 (online, not from the bundle). 2.1.227
  fixed a quadratic message-normalization slowdown, so **pre-227 corpus timings are not
  comparable** to later ones. Recorded in memory `live-context-vs-disk-compaction.md`.
- **2.1.224** — the floor for `set-archive-source.ps1`: 2.1.120–2.1.223 refuse the
  archive source it sets. See `eng/set-archive-source.ps1:10`.
- **2.1.229** — first local Cowork run (desktop shell 1.30096.5.0): all six hooks
  register, then every hook dies exit 127. See `cowork.md:40`, `cowork.md:409`.
- **2.1.233** — Cowork cloud verification baseline, `entrypoint: remote_cowork`. See
  `cowork-compatibility.md:3`, `cowork.md:66`.
