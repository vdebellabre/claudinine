# Claudinine

**Claudinine keeps your Claude Code sessions from getting heavy.**

Every session, Claude Code writes down everything that happened — every file it read, every command it ran, every search result. That file grows fast, and it is mostly bulk you will never look at again: the full text of a file Claude read once, the output of a build that succeeded twenty turns ago.

The next time that session is loaded, all of that bulk gets read back in. It costs tokens, time, and it crowds out the part of the conversation that actually matters.

Claudinine trims the bulk as you go. It keeps a short summary of what happened and moves the full details to a side file, so nothing is lost — it is just not in the way anymore. Your next session starts lean.

## Why use Claudinine

- **Your long sessions stay usable.** Less filler in the transcript means more room for the actual conversation before Claude has to compact.
- **Resuming is faster and cheaper.** Reloading a session no longer means reloading megabytes of old tool output.
- **You do not have to think about it.** There is no dashboard, no report, no prompt asking you to approve anything. Install it and forget it.
- **Nothing is thrown away.** Full outputs are kept in a side file, and you can restore them (see [Getting your details back](#getting-your-details-back)).

Across 174 real sessions, transcripts shrank from **189 MB to 43 MB** on disk. The typical session is reduced by about **75% of the tokens** Claude has to read back when it loads that session again.

## Install

Claudinine is a Claude Code plugin. Run `/plugin install claudinine` in Claude Code, and that is the whole setup — the hooks register themselves and compaction starts with your next prompt. There is nothing to configure.

## How it works

Whenever it is invoked, Claudinine runs one pass over the whole session transcript: copy full content to the sidecar, then compact. This pass is idempotent — re-running it has no effect — so the same pass is safe to run at every hook point. There are five active hooks:

- On a new prompt — to compact the previous turn.
- On turn end — for autonomous stretches (scheduled tasks, loops, workflow runs) that chain many turns with no prompt between them. Throttled to at most one pass per two minutes, so it stays quiet in interactive sessions where the per-prompt pass already runs.
- On session exit — to compact the final turn, leaving the file clean at rest. Subagent transcripts (`<session>/subagents/agent-*.jsonl`) are swept here too, each with its own sidecar.
- On session start — acts as repair for crash leftovers, plus garbage collection of sidecars and orphaned session directories.
- Before Claude's compaction — same reasons as session start.

This behavior is what allows Claudinine to be run through hooks only, without any persistent process. This is also why performance is important.

Every rewrite is validated before an atomic swap, and any failed check leaves the original untouched. See [docs/session-file-changes.md](docs/session-file-changes.md) for exactly what is modified, why, and what the safety guarantees are.

Compaction cannot touch the live in-memory context of a running session — Claude Code loads the transcript once and works from memory. The benefit therefore arrives every time you resume a session.

One small native binary per platform, no runtime, published for x64/arm64 on Windows, macOS and Linux.

## Getting your details back

You can undo compaction for a session entirely, while it is closed. The transcript is rebuilt verbatim from the mirror, and Claudinine can leave that session alone from then on:

```bash
claudinine restore-compaction-off <session-id>
```

Use `restore-compaction-on` instead to restore then let compaction resume.

That form assumes a CLI/marketplace install, which keeps `claudinine` on PATH. A claude.ai-hosted install (Cowork) has no PATH entry — there, use the launcher Claudinine keeps next to each session's mirror:

```bash
sh ~/.claude/projects/<project>/<session-id>/claudinine/run.sh restore-compaction-off <session-id>
```

## Comparison with Cozempic

[Cozempic](https://github.com/Ruya-AI/cozempic) solves a closely related problem, and Claudinine started as an attempt to get the same benefit with far less machinery. If you are choosing between them, the first difference to mention is functional: Cozempic provides more than compaction — live token monitoring, agent-team protection and interactive diagnosis — if you want those features, Cozempic is the right pick. Claudinine focuses only on compaction, but has some serious advantages:

- **No dependencies, no runtime to install.** Claudinine is a single native binary, runnable as is. Cozempic needs Python + `uv` or `pip` + the `fastmcp` and `cozempic` packages.
- **No persistent processes.** Claudinine runs on hook invocations and exits; nothing stays resident. Cozempic spawns a background guard daemon per session and keeps an MCP server running alongside it.
- **Cross-platform without a shell.** Claudinine's hooks invoke a binary directly. Cozempic's hooks are long POSIX shell one-liners using `flock`, `stat`, and `/tmp` paths.
- **Lightning fast.** Hooks run on your prompts, so they have to be invisible: the per-prompt pass takes a median of **18 ms**, process startup included — under a tenth of a percent of the hook budget, and never more than 53 ms across the whole corpus. A full compaction of an untouched transcript, which happens once when Claudinine first meets a session, is a median of **82 ms**. Cozempic's hooks, see previous point, cannot fit this budget and are one of the reasons why it must rely on manual commands and external processes.
- **No MCP server, no context cost of its own.** MCP tool definitions occupy context in every session. Claudinine registers none.

The compaction itself also has major differences, and this is where most of the practical difference shows up:

- **Tool calls chain-collapse.** Claudinine processes turns that ran many tool calls into a digest record — each call listed with a short preview, full outputs moved aside. Cozempic prunes record by record (thinking blocks, stale reads, mega-block trim, envelope strip). Collapsing whole tool chains has a significant impact on compaction, especially for large sessions.
- **Redundancy is proven, not guessed.** Beyond pruning by age and size, Claudinine removes what a later record demonstrably makes obsolete. A file read twice keeps the newer result; an `edited_text_file` notice — which carries the entire file, not a diff, and is the fattest record type in a transcript — goes once a later notice, a full read or a write supersedes it; repeated task-list snapshots keep only the last, which alone removes 97% of that type's bytes. These are correctness wins as much as size wins: a stale full-file snapshot presented as current truth actively misleads the model.
- **A staleness clock that works on agentic sessions.** Cozempic ages records in user turns only, which barely moves when Claude works autonomously — on one measured session, 952 records and 207 tool results produced just 12 prompts, so nothing ever aged. Claudinine ages on either clock, user turns or tool results since, so long autonomous stretches decay normally.
- **Claudinine compacts its own overhead.** Chain-collapse leaves residue: one tool call per collapsed turn must survive, dragging its full input along (81% of all leftover call input), and every digest repeats the same ~1 KB of retrieval instructions (7% of all remaining content). Both are compacted in turn — the input becomes a preview, and only the first digest in a file teaches retrieval.
- **It runs continuously, not as a treatment.** Claudinine compacts each turn as it completes, so the file is already lean at rest. Cozempic's pruning is an operation you invoke — diagnose, dry-run, confirm, apply, then resume the session.

Underlying all of it: **removed content is kept, not deleted.** Every full output is written to the session's sidecar before anything is trimmed, so a stub is a pointer rather than a loss. Each one names the exact command that returns the original, so you can pull back a single output and keep every other saving — and a stripped screenshot or PDF is decoded back to a file Claude can read, re-entering the conversation as fresh vision input instead of being lost. Cozempic's safety net is a timestamped `.bak` copy of the whole file, which undoes the last treatment but cannot return one output while keeping the savings.

That principle is why another rule exists: when a conversation is forked to a new session, the copied digests still point at the parent session, whose sidecar will eventually be garbage-collected out from under them. Claudinine detects the fork, verifies the parent is genuine rather than merely quoted, merges its sidecar, and repoints the references — so everything keeps working as intended, transparently.

### Measured side by side

Both tools ran over the same corpus of **174 real sessions** (189.2 MB, 97 main transcripts and 77 subagent transcripts), each on its own copy so neither saw the other's output. Cozempic ran its strongest prescription (`treat -rx aggressive`). The corpus and harness are in the repo (`eng/bench/`), so the numbers below are reproducible.

**What "tokens" means here matters**, because it is where a naive measurement goes wrong. The count is BPE over only what Claude actually reads back: `message.content` blocks, and only from the last compaction boundary onward. Two large parts of a transcript are *not* counted, because the model never sees them:

- **`toolUseResult`** — a top-level field duplicating each tool's output alongside the copy in `message.content`. It feeds the transcript UI. It was **half the payload** on tool-heavy sessions.
- **Everything before a compaction boundary** — once Claude compacts, the loader reads only from that boundary on. On the corpus sessions that had compacted, that was **70% of the files**.

Deleting either shrinks the file on disk without saving Claude a single token. Counting them credits a tool for work that has no effect, so both were excluded for both tools. Byte columns still cover the whole file, which is the honest measure for disk.

| | baseline | Claudinine | Cozempic |
|---|---|---|---|
| **All sessions** (n=174) | 189.2 MB / 13.44 M tok | 42.7 MB (77.4%) / **4.20 M tok (68.8%)** | 83.8 MB (55.7%) / 9.89 M tok (26.4%) |
| **Main transcripts** (n=97) | 167.9 MB / 10.34 M tok | 38.7 MB (76.9%) / **3.63 M tok (64.9%)** | 71.1 MB (57.7%) / 7.88 M tok (23.8%) |
| **Subagent transcripts** (n=77) | 21.2 MB / 3.10 M tok | 4.0 MB (81.0%) / **0.57 M tok (81.6%)** | 12.7 MB (40.1%) / 2.01 M tok (35.1%) |

Those totals are dominated by whichever sessions happen to be largest — the ten biggest are about 28% of all corpus tokens. For what a single session should expect, the per-session view is the useful one, so here it is by size, with every file kept:

| session size | n | Claudinine | Cozempic |
|---|---|---|---|
| Under 30k tokens | 52 | **65.6%** | 20.5% |
| 30k – 100k | 86 | **77.7%** | 32.6% |
| 100k – 400k | 33 | **62.8%** | 22.9% |
| Over 400k | 3 | **67.0%** | 24.2% |

The median session is reduced by **74.2%** of its tokens with Claudinine and 23.8% with Cozempic. Claudinine saves more on **167 of 174** sessions, with 5 ties and 2 sessions where Cozempic saves more. It is also about 10× faster: compacting all 174 sessions from scratch takes ~31s against Cozempic's ~300s, which is what a native binary buys over a Python process spawned per session.

One of the two sessions Cozempic wins is worth detailing: it contains a single 900 KB block — a bundled skill the session loaded — which Cozempic truncates. Claudinine leaves skill text untouched by choice, since it's meant to impact Claude's behavior during a session, and should arguably persist across session reloads.

Subagent transcripts compact especially well, since a subagent run is one long uninterrupted chain of tool calls — exactly the shape chain-collapse is built for. Claudinine finds those files itself from the session directory; Cozempic has no session-directory concept, so it was pointed at each one explicitly.
