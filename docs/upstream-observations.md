# Upstream observations

Claudinine parses Claude Code's own session transcripts, so an upstream update can
break it silently. This file is the record of which upstream versions have actually
been looked at, and what was found. It is **append-only, hand-written, and gates
nothing** — no code reads it. Its only job is to answer "what changed since the last
time I checked?" without re-deriving the answer from scratch.

Add an entry when a version bump is noticed. An entry saying "read the changelog, no
territory touched" is worth writing: the value is in the *continuity*, not the detail.

## The review, when asked for one

Two checks. The changelog says what upstream *meant* to change; the bundle diff says
what actually moved in the binary we parse against. Neither subsumes the other — read
both, then write the entry.

1. **Changelog, from the last entry's baseline to current.** The bottom of this file's
   last entry names the previous shell and CLI versions; read the notes for everything
   in between. Online, since the bundle ships no CHANGELOG. Only the CLI matters here.
2. **Diff the CLI bundle** against the previous version, per the recipe below — but only
   when the CLI actually moved *and* both versions are still on disk (see the pruning
   caution). A shell-only bump ends at step 1.

Then append an entry, ending with the new baseline so the next review knows where to
start from.

**A corpus run is not part of this, deliberately.** `bench/corpus/` is 174 frozen JSONL
files and `compare.py` shells out only to `cln` and cozempic, never to `claude` — so a
run is a regression test on *our* parser against 2026-08-12-era inputs, and its verdict
is unchanged by whatever Claude version is installed. It cannot see an upstream format
change: new record shapes are simply absent from a frozen corpus, so everything passes
and nothing is learned. Use it to check our own changes, not upstream's.

## What to observe

Two tracks move independently, and both are read off disk:

| Track | How to read it |
|---|---|
| Desktop shell | `ls %LOCALAPPDATA%\AnthropicClaude` → `app-<ver>` dirs (newest mtime = current) |
| Embedded CLI | `%APPDATA%\Claude\claude-code\<ver>\claude.exe --version`; `.payload` holds a sha256 build identity |

Record both in every entry, but they carry different weight: a shell bump alone has never
moved anything we parse, while a CLI bump is the one to take seriously. Confirm which CLI
is actually *running* rather than trusting the highest version dir — the app can keep
several and run an older one (`Get-Process claude | Select Path`).

The thing that truly matters, the transcript format, has no version number and is not
checked by this review — it is pinned empirically in `session-file-changes.md` and
`parallel-batch-transcript-format.md`. The two checks here are early warning that those
documents may need re-verifying, not a substitute for it.

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
  after the bump — diff while both sides are still on disk. If the previous CLI is already
  gone, the bundle diff is simply unavailable: say so in the entry and let the changelog
  read stand alone, rather than reaching for a substitute. To keep the option open across
  a bump you expect to care about, copy the current `claude.exe` aside beforehand (~300 MB)
  — the `.payload` sha256 identifies which build it was.

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
