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
   transcripts (`<session>/subagents/agent-*.jsonl`) get the same treatment —
   each one individually the moment its agent finishes (SubagentStop), plus a
   sweep at SessionEnd and SessionStart as repair for missed events.
2. **A per-session mirror**, colocated with the session inside its own sidecar
   directory: `<project>/<session-id>/claudinine/<session-id>.jsonl` (for a
   subagent transcript, `<session-id>/claudinine/agent-<id>.jsonl`) — next to
   the `subagents/`, `tool-results/` and `workflows/` directories Claude Code
   itself keeps there. The mirror is append-only and holds the uncompacted
   content — it is what the transcript would have been. Colocation is the
   durability guarantee: anything that snapshots, syncs, backs up or deletes
   the session carries its mirror with it. (Pre-0.2 versions kept mirrors in a
   flat pool — `$CLAUDE_PLUGIN_DATA/mirrors` or `~/.claudinine/mirrors`; those
   are still read, and a legacy mirror is migrated to the colocated path the
   first time its session is touched.)

   The same `claudinine/` directory also holds two tiny **retrieval
   launchers**, `run.sh` and `run.cmd`, regenerated on every pass to point at
   the currently running binary. The digest headers invoke retrieval through
   `sh <…>/claudinine/run.sh` rather than a bare `claudinine`, because a
   claude.ai-hosted (Cowork) install puts nothing on PATH. They are safe to
   delete — the next pass rewrites them — and if the session tree moves, the
   header's launcher path self-heals on the next pass.

   Between a session's teardown and its next start boundary the directory also
   carries a transient `<session-id>.end` marker: written at SessionEnd,
   consumed by the next SessionStart — or by the next prompt or turn end, on
   hosts (Cowork cloud) that re-hydrate an idled session without firing
   SessionStart, so the start-of-session work still runs once per hydration.
   A `<session-id>.pass` stamp records when a compaction pass last completed;
   only its mtime is read, by the Stop trigger's min-interval guard (see
   below). A `<stem>.lock` file per transcript backs the cross-process pass
   lock: hooks can fire concurrently (parallel subagents finishing while the
   session's own turn ends), so each pass holds its transcript's lock and a
   contended hook simply skips — the holder is doing the same idempotent work.
   The lock is the open file handle, not the file's existence: a crashed hook
   releases it via the OS, and the leftover `.lock` file is inert.

If the transcript carries retrieval stubs pointing at its own mirror and no
mirror can be found anywhere, the plugin fails closed: no compaction, no mirror
writes, nothing — the loss stays visible instead of being papered over.

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

`QueueHistoryCollapseRule` applies the same standard differently, and since it is
the one record class removed outright rather than digested, its exact contract is
worth spelling out. `queue-operation` records are the app's queued-message
history (messages typed while a turn was still running). The rule replays every
`enqueue`/`dequeue`/`remove` in file order, tracking one queue per `sessionId`
(resumed sessions can interleave several), and removes the operations **only when
every queue provably ends empty** — a non-empty queue means a message is still
pending delivery. Removal is all-or-nothing by necessity, not caution: dequeues
are positional and carry no content, so after deleting any prefix of the history
the remaining operations no longer replay to the same state, and no partial
removal can be proven safe. A trailing queue operation is always skipped as
possibly mid-flight (the pass converges next time), and anything the replay does
not understand — an unknown operation, a dequeue on an empty queue, a remove that
misses — keeps the whole file's queue history untouched.

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
  --full | --media]` (or, where the binary is not on PATH, the same arguments
  through the session's launcher: `sh <…>/<session-id>/claudinine/run.sh get …`
  — the exact command every digest header spells out). A mirror is deleted only
  once its own transcript no longer exists.
- **Everything is reversible.** `claudinine restore-compaction-off <session-id>`
  (run while the session is closed) rebuilds the transcript from its mirror —
  verified line-identical to the pre-compaction original on the heaviest real
  sessions — and freezes the session: hooks keep mirroring it but never compact
  it again, so the restore is never silently undone.
  `claudinine restore-compaction-on <session-id>` restores it once and lets
  steady-state compaction resume (it also unfreezes a frozen session).

## Session forks

The desktop app can fork a conversation into a new session id, copying history
records verbatim. Measured on a live fork (2026-08-18, `251e5d4a` forked to
`7868eb69`, CLI 2.1.229):

- **The transcript is copied; the sidecar is not.** 41 of the fork's 55 records
  still carried the *parent's* `sessionId`, untouched. The parent's
  `subagents/` directory was **not** copied — the fork had no `subagents/` dir
  at all.
- **No `forkedFromSessionId` field.** This fork form stamps nothing. Do not use
  that field to detect forks — a disk-wide search for it found only prose
  mentions, never a real record.
- **Retrieval survives the fork.** The fork got its own mirror
  (`claudinine/7868eb69.jsonl`, `mirrorOf` pointing at itself) and 78 of 79 refs
  resolved, including pre-fork refs from the session's first tool call. The one
  failure was a `ToolSearch` whose output was empty (`-> 0b :: (no output)`) —
  nothing to archive, and it fails identically in the parent, so it is not a
  fork artifact. Every launcher path in the fork named the new sid; zero stale
  parent paths remained.

Two things this measurement did *not* establish, recorded so they are not
mistaken for verified:

- **Which mechanism did the retargeting.** Only `chain-collapse` and
  `anchor-input-stub` stamps appear in the fork — no fork-heal stamp — and
  `Launcher.EnsureCurrent` rewrites paths on every pass regardless of forks. So
  "ForkHealRule detected and retargeted" and "the launcher rewrite made it moot"
  are indistinguishable from the on-disk evidence. The outcome is correct either
  way; the cause is not pinned.
- **What happens to the abandoned subagent.** The fork keeps the spawn digest
  and it resolves from the session mirror, but the agent transcript itself lives
  only in the parent. If the parent is GC'd, the fork retains a working ref to
  the spawn summary and loses only the sidechain detail behind it. That is
  believed harmless, but it is an assumption, not a checked invariant.

### Fork-heal does not cover subagents, and on this evidence does not need to

`ForkHealRule` detects a foreign session by regexing the sid out of a digest's
retrieval command. In a subagent transcript that capture yields the agent's own
id (`agent-<id>`), because the emitters derive the same string from the file
stem (`ChainCollapseRule.cs:101`). The *parent* sid appears only inside the
launcher path (`.../<parent-sid>/claudinine/run.sh`), which the capture group
does not cover. So in an agent file the rule finds nothing foreign and returns
before any lookup: the gap is in **detection**, not resolution.

A second gap sits behind it: `ParentMirrorFiles` only ever constructs
`<sid>/claudinine/<sid>.jsonl` and `<pool>/<sid>.jsonl` candidates
(`MirrorLocator.cs:184`), never `agent-*.jsonl`, so adoption could not resolve
an agent mirror even if detection fired. Both would have to change together.

Neither gap is reachable by a session fork, because the fork abandons
`subagents/` rather than copying it — there is no copied agent file carrying a
stale parent path.

### Measured: what `subagent_type: "fork"` inherits

CLI 2.1.232 turned on subagent forking by default, described upstream as the
subagent inheriting the full conversation. Measured 2026-08-18 on a standalone
CLI at 2.1.232+ (session `b85203e9`, plugin 1.1.0 at user scope), by asking a
forked subagent to report only what it could see, without tools:

- **The fork inherits the compacted view, faithfully.** Its view matched the
  parent's exactly — same carrier headers, same refs, no raw payload leaking
  through on the inheriting side. Context inheritance operates on the compacted
  transcript, not on a pre-rewrite copy or a hydrated view. It quoted a carrier
  header back verbatim.
- **Full outputs are absent from the inherited context, previews are present.**
  For ref `adfed4ed` the fork had the preview fragment and the inter-call note,
  and could not see the rest of the payload.
- **Retrieval works from inside the fork.** It ran the header's command
  unmodified — a launcher path belonging to the *parent* session, with a `<sid>`
  argument that is not the agent's own — and the full record came back intact.
  Retrieval is session-addressed, not identity-scoped; a forked subagent is not
  confined to its own session's archive.
- **The retrieved bytes confirmed the parent's prose.** The parent had asserted
  specifics (a settings key, a marketplace registration) that were unverifiable
  from collapsed context; the archive returned them and they were accurate.

So the earlier worry — that a forked subagent would hold stubs with no way back
— does not hold. The cost is real but narrower, and best stated the way the fork
itself put it: the parent's conclusions arrive as **assertions whose evidence is
behind a retrieval call**. Warrant is deferred, not lost. This is why the
per-call inter-call notes are load-bearing rather than decorative — they are the
only thing keeping a collapsed turn interpretable without paying for a `get`.
Do not trim them as redundant prose.

One incidental finding, worth keeping because it constrains every matcher: a
first attempt appended `; echo "EXIT=$?"` to the header's command and was
**denied by the permission layer before execution** — no exit status, no output.
The identical command in the header's bare form ran without a prompt. Appending
to the retrieval command breaks the match; the bare form is the one that works.

Two things deliberately not claimed from this measurement: the `600b` size match
was called consistent-on-inspection by the fork rather than verified (`--info`
was not run), and the fork's "7 prior messages" was its own turn-boundary
framing, not a raw block count — it flagged both itself.

**Still unmeasured:** whether a forked subagent's `agent-<id>.jsonl` on disk
carries copied parent records, and so whether parent-sid launcher paths ever
land inside an agent file. The context-level question above is answered; the
disk-level one is not. Until it is, the two code-level gaps in the preceding
section remain reachable in principle and unreached in practice.

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
- **Hooks run on every prompt, and on turn ends.** The turn-end (`Stop`) trigger
  exists for autonomous stretches — scheduled tasks, loops, workflow runs — that
  chain many turns with no prompt between them; a min-interval guard (one pass
  per 120 s at most, tracked via the `.pass` stamp) keeps it from doubling the
  per-prompt work in interactive sessions. Measured over the 174-session corpus
  (`eng/bench/steady.py`), the steady-state `UserPromptSubmit` pass — the one a
  user actually waits for, over a transcript already compacted and mirrored — is
  **17.5 ms median, 24.1 ms p90, 52.7 ms worst**, process startup included. That
  is under 0.1% of the 60 s hook budget at worst. The cold whole-file pass, which
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

- `cd src && dotnet run --project Claudinine.Tests` — 330+ tests, covering each
  rule, the validation gate, and the rechaining logic.
- `CLAUDININE_DEBUG=1` on any hook invocation prints what fired and what was
  refused.
- The rewrite is idempotent: run the hook twice over a transcript copy and the
  second pass produces a byte-identical file.
