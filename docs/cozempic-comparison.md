# Comparison with Cozempic

← [Back to the README](../README.md)

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

## Measured side by side

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
