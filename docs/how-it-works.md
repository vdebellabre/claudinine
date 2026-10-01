# How it works

← [Back to the README](../README.md)

## What it looks like

A turn that ran three tool calls is written to the transcript as three full records — each carrying the complete output. Claudinine replaces them with one:

```text
[claudinine: this turn originally ran 3 separate tool calls. Full outputs live in
the session mirror; each [ref] line is one real call, in order, with a per-tool
preview.

RETRIEVAL — use the targeted form:
  <retrieve> --ref REF --grep PATTERN   # matching lines (PREFERRED)
  <retrieve> --ref REF --full           # entire output (last resort)
  REF = the 8-hex id in [brackets]: [ab12cd34] -> --ref ab12cd34]

[6765eec5] Bash(ls -la && wc -l README.md) -> 1305b :: 2 sections | README: 133
[a4af6a0b] Bash(cat README.md) -> 16409b :: # Claudinine / **Claudinine silently...
[db46dc21] Bash(cat .claude-plugin/*.json) -> 1869b :: 4 sections | manifest: {
```

The three outputs above totalled about 19 KB. What stays in the transcript is a few hundred bytes: one line per call, in order, each with its size, a preview, and an id. The full text of every one of them is in the side file, and the header tells Claude exactly how to fetch back any line it turns out to need.

(`<retrieve>` above stands in for the real retrieval command, which carries an absolute path to your install — see [Getting your details back](#getting-your-details-back). Sizes and previews are shortened here to fit the page.)

## Hooks and passes

Whenever it is invoked, Claudinine runs one pass over the whole session transcript: copy full content to the sidecar, then compact. This pass is idempotent — re-running it has no effect — so the same pass is safe to run at every hook point. There are six active hooks:

- On a new prompt — to compact the previous turn.
- On turn end — for autonomous stretches (scheduled tasks, loops, workflow runs) that chain many turns with no prompt between them. Throttled to at most one pass per two minutes, so it stays quiet in interactive sessions where the per-prompt pass already runs.
- On subagent completion — to compact that agent's transcript (`<session>/subagents/agent-*.jsonl`) the moment it finishes, each with its own sidecar. Subagent transcripts compact best of all file types.
- On session exit — to compact the final turn, leaving the file clean at rest. Subagent transcripts are swept here too, as repair for any missed completion events.
- On session start — acts as repair for crash leftovers, plus garbage collection of sidecars and orphaned session directories.
- Before Claude's compaction — same reasons as session start.

This behavior is what allows Claudinine to be run through hooks only, without any persistent process. This is also why performance is important.

Every rewrite is validated before an atomic swap, and any failed check leaves the original untouched. See [session-file-changes.md](session-file-changes.md) for exactly what is modified, why, and what the safety guarantees are.

Compaction cannot touch the live in-memory context of a running session — Claude Code loads the transcript once and works from memory. The benefit therefore arrives every time you resume a session.

On Cowork that moment comes more often than in the CLI, not less: cloud sessions are torn down when idle and re-hydrated from the transcript on the next activity, so one session pays the reload repeatedly within its life — each time from the compacted file. What shrinks there is the long tail: when the cloud container is eventually reclaimed, the transcript and its side file go together, so the archive does not outlive the session the way a local one does.

Cowork also leans harder on two of the hooks above. Sessions there often run long autonomous stretches — scheduled tasks, workflows, agent fan-outs — with no prompt in between, which is exactly what the turn-end hook covers: on one measured cloud session a single autonomous turn went from 285 KB to 36 KB, a stretch that would not have compacted at all without it. And because those stretches spawn many subagents, compacting each agent transcript the moment it finishes matters more than in the CLI: across one session's five agent files, 802 KB of tool output became 142 KB.

One small native binary per platform, no runtime, published for x64/arm64 on Windows, macOS and Linux — including Linux under WSL, which is an ordinary marketplace install inside the distro. That is what lets a single install follow you from the CLI to a cloud container to your own desktop.

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

Claudinine writes that same launcher form into every stub it leaves in the transcript, so Claude can pull an individual output back without you doing anything. Cowork's local mode ("On your computer") is the one place that works differently, because those sessions usually have no shell at all: there Claudinine keeps a plain-text copy of every archived output inside the session's own workspace (`outputs/.claudinine/refs/`), and stubs point Claude's file tools at it instead of quoting a command — retrieval works with nothing to run. To restore such a session yourself, use the launcher's Windows twin from a regular terminal — the session store lives under your profile:

```bash
%APPDATA%\Claude\local-agent-mode-sessions\<install>\<device>\local_<id>\.claude\projects\<project>\<session-id>\claudinine\run.cmd restore-compaction-off <session-id>
```

(On a macOS desktop, the same colocated directory carries `run.sh`.)

## Diagnostics

Claudinine is deliberately silent: any anomaly makes a pass skip itself rather than risk the transcript, with no output. If you suspect compaction is not happening and want to see why, create an empty file at `~/.claude/claudinine-debug.log` — every subsequent pass appends its diagnostics there (skips, failures, per-pass stats), from every hook, with timestamps and process ids. Delete the file to go silent again; it stops growing at 10 MB. Setting the `CLAUDININE_DEBUG` environment variable still prints the same diagnostics to stderr for interactive runs.
