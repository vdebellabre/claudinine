# What Claudinine changes in session files, and why

This plugin modifies Claude Code's own session transcripts in place. That is
unusual enough to deserve a precise description rather than a summary, so this
document states exactly what is touched, what is guaranteed, and what the
failure modes are.

Audience: anyone reviewing this plugin before installing or listing it.

## The problem being solved

A Claude Code session transcript (`~/.claude/projects/<project>/<session>.jsonl`,
one JSON object per line) is append-only and grows without bound. Tool traffic
dominates it. Measured over the 174-session corpus (`eng/bench/census.py`,
counting only what the model reads back — `message.content` from the last
compaction boundary onward): **55.7% tool results, 27.1% tool-call inputs, 17.2%
assistant and user text**. Better than four in five tokens a resumed session
pays for are machine traffic, not conversation.

That matters because the transcript is replayed. Resuming a session, clearing
context, or compacting re-reads the file, so every byte of a verbose `grep` from
two hours ago is paid for again in tokens and latency. Deleting sessions is the
usual remedy, which loses the history.

Claudinine's premise is that most of that bulk is recoverable *without* losing
information, by moving full outputs to a sidecar file and leaving a retrievable
pointer in the transcript.

## What gets written

Two locations, both belonging to this plugin or the session it is compacting:

1. **The session transcript**, rewritten in place (details below). Subagent
   transcripts (`<session>/subagents/agent-*.jsonl`) get the same treatment,
   swept at SessionEnd and SessionStart.
2. **A per-session mirror**, `<CLAUDE_PLUGIN_DATA>/mirrors/<session-id>.jsonl`
   (for a subagent transcript, `mirrors/agent-<id>.jsonl`).
   `CLAUDE_PLUGIN_DATA` is the documented writable per-plugin directory; if it is
   unset the fallback is `~/.claudinine/mirrors`. The mirror is append-only and
   holds the uncompacted content — it is what the transcript would have been.

Nothing else on disk is written. No network calls are made, ever: the binary has
no HTTP client and no telemetry, and the plugin ships no MCP server. Nothing is
sent anywhere.

## What gets changed inside the transcript

Every modification is one of two kinds:

- **Replacement** — a record's tool-result content is swapped for a shorter
  stub, keeping the record, its `uuid`, and its position.
- **Removal** — an entire inert record is dropped (a stale reminder block, a
  superseded metadata record), and surviving children are rechained.

The rules, in execution order, are declared in one catalog
(`src/Claudinine/Rules/ICompactionRule.cs`). The main ones:

| Rule | What it does |
| --- | --- |
| `ChainCollapseRule` | Collapses a multi-call turn into one digest record: one `[ref]` line per call with a short preview. The bulk of the savings. |
| `CarrierHeaderDedupRule` | The digest's retrieval instructions are identical in every digest, so only the first per file keeps the long form. |
| `AnchorInputStubRule` | Replaces a collapsed turn's retained `tool_use.input` with a pointer plus a 90-character preview. |
| `ToolResultAgeRule` | Age-tiered stubbing of old tool results. Age is measured on a dual clock — user turns *or* tool results appended since — because a user-turn-only clock never advances during long autonomous stretches. |
| `MegaBlockTrimRule` / `ImageStripRule` | Trims oversized blocks; stubs old base64 media (pasted images, PDFs, tool screenshots) with a retrieval pointer — `claudinine get <sid> --ref <uuid> --media` decodes the mirrored original to a file the Read tool can view. Thinking blocks are never trimmed: they are signed, and a tampered block risks API rejection on replay. |
| `ReadSupersessionRule` (bash-read, read) | Retires a read result whose every line range is covered by a later read of the same file — the earlier one is a strictly staler copy. The session's six most recent reads are never touched. |
| dedup rules (system-reminder, document) | Collapse byte-identical repeats. Lossless, and near-zero yield in practice. |
| `ForkHealRule` | The desktop app forks a conversation to a new session id on an API-error retry, copying history verbatim — so the copied digests still name the *parent* session, whose mirror will eventually be GC'd out from under them. Runs first, before any rule reads a digest: validates the candidate parent, merges its mirror, and retargets only records the parent actually mirrored under the same uuid. Quoted commands belonging to other sessions stay verbatim. |
| housekeeping rules | Drop superseded edits, stale reminders, queue history, hook-success noise (see below). |

### Whole-record removal is canary-verified

The housekeeping rules delete entire records rather than stubbing them, so the
question is not "is this big?" but "does the app replay it on resume?". That was
settled empirically rather than assumed — marker records were planted early, mid
and late in a live session (2026-08-07, session `6faceebf`, v2.1.222), which was
then resumed to see what came back:

- `Stop` and `PostToolUse` `hook_success` records were invisible to the resumed
  model — pure disk history, and 81% of that type's bytes. They are removed.
- `SessionStart` `hook_success` records **are** replayed verbatim into resumed
  context. They are never touched.

Removal is therefore an allowlist, not a blocklist: any event not *proven* inert
(`SessionStart`, `PreToolUse`, or anything a future version introduces) is kept.
`QueueHistoryCollapseRule` applies the same standard differently — it replays the
enqueue/dequeue history internally and only drops it when every queue provably
ends empty; anything the replay cannot account for fails the whole file closed.

These removals shrink the file on disk. They are not counted in Claudinine's
published token figures, which measure only `message.content` — the benchmark
gives these rules no credit at all.

Every stub carries a `claudinine` key, which makes changes machine-identifiable
and the rules idempotent — re-running over already-compacted output is a no-op.

## What is guaranteed

The rewrite path (`src/Claudinine/Transcript/TranscriptFile.cs`) is fail-closed:
it validates the complete result independently before anything is swapped in, and
**any** failed check abandons the rewrite and leaves the original file byte-for-byte
untouched. The checks:

- **Format sentinel on load** — a single line that is not a JSON object aborts
  the pass. An unfamiliar file shape means do nothing.
- **Reparse** — every output line must parse as JSON.
- **Chain integrity** — each surviving record's `uuid` and `parentUuid` must match
  what was computed, and nothing may still reference a removed record
  (`dangling-parent`, `dangling-leaf`, `dangling-source`).
- **Tail preservation** — the final record must survive with its `uuid` intact and,
  unless its own links had to be rechained, byte-identical. Claude Code chains the
  next append off that record.
- **Never empty** — a rewrite producing zero records is refused.

Only then is the result written to a temp file and moved over the original in one
atomic `File.Move`. Set `CLAUDININE_DEBUG=1` to have refusals name the failed
check on stderr.

Two further properties worth stating:

- **The live session is never affected.** Rewrites touch the file on disk, not the
  running session's in-memory context. This was verified empirically, including a
  canary that resumed a session whose replayed history contained rewritten
  records: ~37.8k tokens replayed from cache, no error, no UI breakage.
- **Nothing is destroyed.** Full original content stays in the mirror and is
  retrievable with `claudinine get <session-id> --ref <ref> [--grep P | --info |
  --full | --media]`. A mirror is deleted only once its own transcript no longer
  exists.
- **Everything is reversible.** `claudinine restore-compaction-off <session-id>`
  (run while the session is closed) rebuilds the transcript from its mirror —
  verified line-identical to the pre-compaction original on the heaviest real
  sessions — and freezes the session: hooks keep mirroring it but never compact
  it again, so the restore is never silently undone.
  `claudinine restore-compaction-on <session-id>` restores it once and lets
  steady-state compaction resume (it also unfreezes a frozen session).

## Known, accepted trade-offs

Stated plainly, because they are real:

- **Scrollback shows stubs.** The transcript also backs the UI's scrollback, so
  after a resume, compacted tool outputs appear as short digests rather than the
  original text. The full content is in the mirror, not lost, but it is one
  retrieval step away instead of inline — and `restore-compaction-off` brings the
  whole session back verbatim if you want it.
- **It is not free of assumptions about an undocumented format.** The transcript
  schema is Claude Code's internal format and can change. The mitigations are the
  format sentinel (unknown shape → do nothing), structural rather than textual
  matching, and fail-closed validation — but a format change could make the plugin
  inert until updated. It should not corrupt anything, and that is the design
  priority.
- **Hooks run on every prompt.** Measured over the 174-session corpus
  (`eng/bench/steady.py`), the steady-state `UserPromptSubmit` pass — the one a
  user actually waits for, over a transcript already compacted and mirrored — is
  **17.5 ms median, 24.1 ms p90, 52.7 ms worst**, process startup included. That
  is 0.21% of the 25 s hook budget at worst. The cold whole-file pass, which
  happens once at SessionStart over an untouched transcript, is 81.8 ms median,
  205 ms p90 and 832 ms worst; that is the one to cite for a first run rather
  than for per-prompt cost. Both columns come from the same serial
  `eng/bench/steady.py` run — `compare.py` also prints a per-file time, but it
  runs files in parallel, so its timings carry contention and are not a latency
  measurement.
- **Session-directory GC deletes orphans.** At SessionStart, `<uuid>/` sidecar
  directories whose `<uuid>.jsonl` transcript is gone are removed, guarded by a
  strict lowercase-hex uuid match, a 24-hour grace period on the newest write
  anywhere in the tree, and never the current session.

## Verifying the claims

- `cd src && dotnet run --project Claudinine.Tests` — 285 tests, covering each
  rule, the validation gate, and the rechaining logic.
- `CLAUDININE_DEBUG=1` on any hook invocation prints what fired and what was
  refused.
- The rewrite is idempotent: run the hook twice over a transcript copy and the
  second pass produces a byte-identical file.
