# What Claudinine changes in session files, and why

This plugin modifies Claude Code's own session transcripts in place. That is
unusual enough to deserve a precise description rather than a summary, so this
document states exactly what is touched, what is guaranteed, and what the
failure modes are.

Audience: anyone reviewing this plugin before installing or listing it.

## The problem being solved

A Claude Code session transcript (`~/.claude/projects/<project>/<session>.jsonl`,
one JSON object per line) is append-only and grows without bound. Tool output
dominates it: on a 95-session corpus measured 2026-08-08, tool results were 57%
of the remaining API-visible content and assistant text 29%.

That matters because the transcript is replayed. Resuming a session, clearing
context, or compacting re-reads the file, so every byte of a verbose `grep` from
two hours ago is paid for again in tokens and latency. Deleting sessions is the
usual remedy, which loses the history.

Claudinine's premise is that most of that bulk is recoverable *without* losing
information, by moving full outputs to a sidecar file and leaving a retrievable
pointer in the transcript.

## What gets written

Two locations, both belonging to this plugin or the session it is compacting:

1. **The session transcript**, rewritten in place (details below).
2. **A per-session mirror**, `<CLAUDE_PLUGIN_DATA>/mirrors/<session-id>.jsonl`.
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
| `ToolResultAgeRule` | Age-tiered stubbing of old tool results. |
| `MegaBlockTrimRule` / `ImageStripRule` | Trims oversized blocks; stubs old base64 media (pasted images, PDFs, tool screenshots) with a retrieval pointer — `claudinine get <sid> --ref <uuid> --media` decodes the mirrored original to a file the Read tool can view. |
| dedup rules (bash-read, read, system-reminder, document) | Collapse byte-identical repeats. Lossless, and near-zero yield in practice. |
| housekeeping rules | Drop superseded edits, stale reminders, queue history, hook-success noise. |

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
- **Hooks run on every prompt.** Worst measured pass across the corpus is 558 ms,
  against a 25 s `UserPromptSubmit` budget; typical is ~91 ms.
- **Session-directory GC deletes orphans.** At SessionStart, `<uuid>/` sidecar
  directories whose `<uuid>.jsonl` transcript is gone are removed, guarded by a
  strict lowercase-hex uuid match, a 24-hour grace period on the newest write
  anywhere in the tree, and never the current session.

## Verifying the claims

- `dotnet test tests/Claudinine.Tests` — 238 tests, covering each rule, the
  validation gate, and the rechaining logic.
- `CLAUDININE_DEBUG=1` on any hook invocation prints what fired and what was
  refused.
- The rewrite is idempotent: run the hook twice over a transcript copy and the
  second pass produces a byte-identical file.
