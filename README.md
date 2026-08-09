# Claudinine

**Claudinine keeps your Claude Code sessions from getting heavy.**

Every session, Claude Code writes down everything that happened — every file it
read, every command it ran, every search result. That file grows fast, and it is
mostly bulk you will never look at again: the full text of a file Claude read
once, the output of a build that succeeded twenty turns ago.

The next time that session is loaded — you resume it, or Claude compacts it —
all of that bulk gets read back in. It costs tokens, time, and it crowds out the
part of the conversation that actually matters.

Claudinine trims the bulk as you go. It keeps a short summary of what happened
and moves the full details to a side file, so nothing is lost — it is just not
in the way anymore. Your next session starts lean.

## Why you might want this

- **Your long sessions stay usable.** Less filler in the transcript means more
  room for the actual conversation before Claude has to compact.
- **Resuming is faster and cheaper.** Reloading a session no longer means
  reloading megabytes of old tool output.
- **You do not have to think about it.** There is no dashboard, no report, no
  prompt asking you to approve anything. Install it and forget it.
- **Nothing is thrown away.** Full outputs are kept in a side file, and you can
  pull any of them back or undo the whole thing (see [Getting your details back](#getting-your-details-back)).

The bigger the session, the more it helps. Across 95 real sessions, transcripts
shrank from **152.6 MB to 35.8 MB (77%)**. Short, conversation-heavy sessions
shrink around 43%; sessions over 3 MB — the ones that were actually hurting —
shrink around 79%.

## Comparison with Cozempic

[Cozempic](https://github.com/Ruya-AI/cozempic) solves a closely related problem,
and Claudinine started as an attempt to get the same benefit with far less
machinery. If you are choosing between them, the differences that matter are:

- **No dependencies, no runtime to install.** Claudinine is a single native
  binary. Cozempic needs Python, plus `uv` or `pip`, plus the `fastmcp` and
  `cozempic` packages — on a machine where the wrong `python3` comes first on
  `PATH`, that is a real source of breakage.
- **No persistent processes.** Claudinine runs on hook invocations and exits;
  nothing stays resident. Cozempic spawns a background guard daemon per session
  and keeps an MCP server running alongside it.
- **No MCP server, so no context cost of its own.** MCP tool definitions occupy
  context in every session. A tool whose purpose is to save context should not
  spend any first; Claudinine registers none.
- **Nothing is installed or upgraded behind your back.** Cozempic's session-start
  hook runs `pip install --upgrade cozempic` on every session unless you set an
  opt-out variable, which means the code that edits your transcripts can change
  without you asking. Claudinine only changes when you update the plugin.
- **No slash commands to remember, no nudges.** Claudinine has no user-facing
  loop at all: it never posts status lines, never suggests you run a treatment,
  and adds no turns to your conversation. Cozempic is driven through
  `/cozempic` skills (treat, reload, guard, doctor) and prompts you when it
  thinks you should act.
- **Cross-platform without a shell.** Claudinine's hooks invoke a binary
  directly. Cozempic's hooks are long POSIX shell one-liners using `flock`,
  `stat`, and `/tmp` paths — fragile on native Windows.

The compaction itself also works differently, and this is where most of the
practical difference shows up:

- **Chain-collapse has no equivalent.** Claudinine's biggest single win is
  turning a turn that ran many tool calls into one digest record — each call
  listed with a short preview, full outputs moved aside. Cozempic prunes
  record by record (thinking blocks, stale reads, mega-block trim, envelope
  strip); it has no notion of collapsing a whole tool chain, which is exactly
  where large sessions put their weight.
- **Removed content is kept, not deleted.** Claudinine writes every full output
  to a per-session mirror, so a stub is a pointer rather than a loss, and
  `restore-compaction-off` rebuilds the transcript verbatim. Cozempic's safety
  net is a timestamped `.bak` copy of the whole file — fine for undoing the
  last treatment, but it does not let you pull back one specific output while
  keeping the savings.
- **It runs continuously, not as a treatment.** Claudinine compacts each turn
  as it completes, so the file is already lean at rest. Cozempic's pruning is
  an operation you invoke — diagnose, dry-run, confirm, apply, then resume the
  session — with savings quoted per prescription (`gentle` through
  `aggressive`) at the moment you run it.

### Measured side by side

Both tools were run over the same corpus of 77 real sessions (130 MB of
transcripts), each on its own copy so neither saw the other's output — every
one a transcript neither tool had ever touched, so both start from the same
untouched baseline. Cozempic ran its strongest prescription
(`treat -rx aggressive`). Token counts are BPE counts over the message payload
— text, thinking, tool inputs and tool results — so the JSON envelope is
excluded and both tools are measured with the same ruler.

| | baseline | Claudinine | Cozempic |
|---|---|---|---|
| **All sessions** (n=77) | 130.3 MB / 19.22 M tok | 28.6 MB (78.1%) / **4.24 M tok (77.9%)** | 64.0 MB (50.8%) / 7.14 M tok (62.9%) |
| **Over 1 MB** (n=36) | 113.3 MB / 16.62 M tok | 22.6 MB (80.0%) / **3.49 M tok (79.0%)** | 55.4 MB (51.1%) / 6.06 M tok (63.6%) |
| **100 KB – 1 MB** (n=33) | 16.6 MB / 2.58 M tok | 5.7 MB (65.9%) / **0.73 M tok (71.6%)** | 8.4 MB (49.6%) / 1.07 M tok (58.7%) |
| **Under 100 KB** (n=8) | 0.4 MB / 0.02 M tok | 22.3% / **41.2%** | 18.6% / 22.1% |

Claudinine saves more tokens on 67 of the 77 sessions, and the gap widens with
size: on sessions over 1 MB it wins 34 of 36. It is also about 15× faster over
the corpus (10s against 151s), which is what a native binary buys over a Python
process spawned per session.

Cozempic does things Claudinine deliberately does not: live token monitoring,
agent-team protection across compaction, and interactive diagnosis. If you want
a tool you drive, look there. Claudinine is for people who want the transcript
to stay small and never think about it again.

## Install

Add the marketplace, then install the plugin:

```bash
/plugin marketplace add vdebellabre/claudinine
```

```bash
/plugin install claudinine
```

That is the whole setup. Claudinine runs silently from then on.

Requires Claude Code 2.1.224 or later. Compatible with Claude Desktop and Claude CLI.
Published for x64/arm64 on Windows, macOS and Linux.

## One thing to expect

Your scrollback and the transcript are the same file, so after you resume a
session, older tool outputs show up as short stubs instead of their full text.
That is the compaction you asked for — the originals are still on disk.

### Getting your details back

You can undo compaction for a session entirely, while it is closed. The transcript is
rebuilt verbatim from the mirror, and Claudinine can leave that session alone from
then on:

```bash
claudinine restore-compaction-off <session-id>
```

Use `restore-compaction-on` instead to restore then let compaction resume.

---

# Technical details

## Where the savings come from

Most of the yield is **chain-collapse**: a turn that ran many tool calls becomes
one digest record listing each call with a short preview, and the full outputs
move to a sidecar mirror. The rest comes from the aging and trim family
(age-tiered tool-result stubs, mega-block trim, image strip) and from
record-level housekeeping (superseded file edits, stale reminder blocks, queue
history).

Byte percentages understate the API-token saving by roughly 3.5×, since JSON
envelope overhead dilutes the bytes but not the tokens.

## How it works

- **UserPromptSubmit** — after each of your prompts, the turn that just
  finished is mirrored to a sidecar file, then compacted in the live transcript.
- **SessionEnd** — the final turn gets the same treatment, leaving the file
  clean at rest. Subagent transcripts (`<session>/subagents/agent-*.jsonl`) are
  swept here too, each with its own mirror.
- **SessionStart / PreCompact** — full-scan repair for crash leftovers
  (including the subagent sweep), plus garbage collection of mirrors and
  orphaned session directories.

Compaction never touches the live in-memory context of a running session — the
payout arrives at the next transcript load. Every rewrite is validated before an
atomic swap, and any failed check leaves the original untouched. See
[docs/session-file-changes.md](docs/session-file-changes.md) for exactly what is
modified, why, and what the safety guarantees are.

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

That asset is what the marketplace serves, via an `archive` source pinned to the
release URL and its SHA-256 (`eng/set-archive-source.ps1` writes both). Installing
needs no git clone, which matters because this repo's `.git` carries every binary
ever shipped and grows ~18 MB per release.

## License

MIT — see [LICENSE](LICENSE).
