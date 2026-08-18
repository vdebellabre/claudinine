# Forked subagents: what the transcripts actually look like

Status: **amended 2026-08-18 (second pass), classification fix shipped.** The
first pass was measurement-only; a re-measurement the same day overturned
finding 1 (see its rewritten section — the original text mis-read the file in
two ways), surfaced the actual defect (classification), and that defect was
fixed with tests. Findings 2–4 stand as measured.

## Why this was investigated

CLI 2.1.232 turned subagent forking on by default (`subagent_type: "fork"`,
described upstream as the subagent inheriting the full conversation). The worry
was concrete: Claudinine compacts a session transcript into digest headers plus
retrieval refs, so a subagent inheriting that conversation would inherit
**stubs**, and its retrieval commands would name a *different* session's
launcher. Two questions followed — is the inherited context usable, and does the
inherited shape land on disk somewhere our rules mishandle.

The desktop CLI is pinned at 2.1.229, which rejects `subagent_type: "fork"` as an
unknown agent type, so the fork itself was run on a standalone CLI at 2.1.232+.

The first question is answered elsewhere (`session-file-changes.md`, section
"Session forks"): the fork inherits the compacted view faithfully, and retrieval
**works** from inside it — it ran the header's command unmodified against the
parent session's launcher and sid, and got the full record back. Retrieval is
session-addressed, not identity-scoped. The residual cost is that inherited prose
arrives as assertions whose evidence sits behind a `get`. This document covers
the second question: the on-disk shape.

## The measurement setup

Session `b85203e9` (standalone CLI, plugin 1.1.0 at user scope) accumulated
digest headers, then spawned a forked subagent `agent-a9b839ac5eaa570bd`. The
operator later **switched the terminal into the subagent conversation to talk to
it directly**, which created a second session, `8917c7ca`. That switch matters
for finding 3 and is the reason it turned out benign.

Artifacts:

- `b85203e9-…/subagents/agent-a9b839ac5eaa570bd.jsonl` — 15 records, 28 753 B
- the same file, hard-linked into `8917c7ca-…/subagents/`
- one mirror per session, under each session's own `claudinine/` directory

## Finding 1 — REWRITTEN 2026-08-18: no inherited digests exist on disk; the real gap was classification

The first pass claimed the three `[claudinine:` headers "arrived as copied
parent context" and that fork-heal's detection missed them because the parent
sid "exists only in the path portion of the launcher invocation". **Both claims
were wrong.** Re-measured against the live file:

- **The fork materializes no inherited records at all.** The 15 on-disk records
  are the `fork-context-ref` pointer (record 0) plus the agent's OWN
  conversation. The three `[claudinine:` hits are the operator's canary prompt
  and the agent QUOTING a digest header in its answer (records 1–3). The
  inherited compacted view exists only behind the pointer — `contextLength: 36`
  against 15 records was saying exactly this.
- **The regex measurement was a raw-vs-decoded artifact.** The launcher commands
  in the file DO spell the parent sid as the get argument:
  `…/b85203e9-…/claudinine/run.sh\" get b85203e9-8ccd-… --ref adfed4ed --full`.
  The first-pass check ran against raw JSONL, where the quote is JSON-escaped
  (`run.sh\"`), so the pattern's literal `run.sh"` never matches — the same
  escaped-quote trap `Compactor.MirrorLost` handles explicitly on RawLine.
  Against the decoded strings `ForkHealRule` actually visits, the capture DOES
  return the parent sid.
- **Detection actually stops one gate earlier.** `ForkHealRule.Apply` skips any
  record without a `claudinine` envelope (`ForkHealRule.cs:49`), and no record
  in this file has one — that is what "zero rule stamps" meant. And for this
  file, not healing is the DESIGNED outcome: these are quoting records, the
  exact class the fork-vs-quote validation exists to leave verbatim. Even with
  detection, `genuineFork` would refuse — agent-authored uuids are not in the
  parent's mirror.

So there are no inherited digests on disk to heal, and fork-heal is the wrong
frame for fork-mode agents entirely. (It remains the right frame for SESSION
forks, which copy records verbatim, envelopes included.) What the second pass
found instead:

**The real defect was classification.** `IsSidechainFile` required EVERY record
to carry `isSidechain: true`; the `fork-context-ref` head carries none, so every
fork-mode agent file classified as MAIN, and ChainCollapse's sidechain guard
(`ChainCollapseRule.cs:173`) aborted on every record. Verified live on a copy of
this file: SubagentStop → exit 0, transcript byte-unchanged, zero compaction,
silently. Once forks are the default everywhere, that would have canceled
subagent compaction (82.1% token savings on agent files in the corpus benchmark)
wholesale.

**Fixed 2026-08-18**: `fork-context-ref` is classification-neutral in
`IsSidechainFile`, protected in `IsProtected()`, and the tolerance is pinned by
`ForkedSubagentTests` (classification both directions, byte-identical survival
of the head record, mirroring by content hash, idempotence). Re-verified on the
live copy: the pass now compacts it and is byte-idempotent across passes. The
snapshot corpus contains zero `fork-context-ref` records, so the change is a
no-op on every existing file by construction.

Incidental observation from the same run — pre-existing, NOT caused by the fix
(reproduced on the identical file minus the head record, which classifies
sidechain under the OLD rule too): a turn whose collapsible batch reduces to one
call still collapses, and on a file with no prior full header the digest header
lands unamortized — 28.6 KB → 30.1 KB, net negative. Likely the denial-exclusion
path missing the reduced-count MinCalls re-check the tail-drop path has; spun
off as its own task.

One first-pass claim in this area survives, narrowed: `ParentMirrorFiles` never
constructs `agent-*.jsonl` candidates (`MirrorLocator.cs:184`). But
`ProjectDirFor` explicitly handles `subagents/` paths (`MirrorLocator.cs:52`),
so a parent-SESSION candidate resolves correctly from inside an agent file — the
gap only matters if a referenced parent were itself an agent, which nothing
observed produces.

## Finding 2 — `fork-context-ref`, a record type we do not model

Record 1 of a forked agent transcript:

```json
{"type": "fork-context-ref",
 "agentId": "a9b839ac5eaa570bd",
 "parentSessionId": "b85203e9-8ccd-4ceb-a02c-c8503f209958",
 "parentLastUuid": "77d11ebf-5531-4b1b-ba23-8dbd3996886f",
 "contextLength": 36}
```

It is a **pointer, not content**: `contextLength: 36` against 15 records on disk.
The inherited context is referenced by `parentLastUuid`; only part of it is
materialized into the agent file. This is consistent with what the fork reported
seeing — the compacted view — while the on-disk file holds a pointer plus a
partial replay.

Two properties matter for any future work:

- It carries the parent linkage in **clean structured fields**. Any detection
  built on this is strictly better than regexing launcher paths: unambiguous, and
  it survives header-format changes. Per the rewritten finding 1, it is also the
  ONLY sound detection signal — text detection is envelope-gated at
  `ForkHealRule.cs:49`, and relaxing that gate would reopen the quoting
  false-positive surface it exists to close.
- ~~We do not model this type.~~ **Modeled since 2026-08-18.** It has no `uuid`,
  no `sessionId`, no `message`, no `isSidechain`. It originally sat at the head
  of a file that our tail walk and reachability guard processed without
  complaint — an untested tolerance. It is now designed: classification-neutral
  (`TranscriptFile.IsSidechainFile`), protected (`TranscriptRecord.IsProtected`),
  mirrored by content hash like other uuid-less lines, all pinned by
  `ForkedSubagentTests`.

## Finding 3 — divergent mirrors of one shared file (EXPLAINED, NOT A DEFECT)

The agent transcript has `nlink = 4`. Enumerated authoritatively with
`fsutil hardlink list`:

| # | Path | Kind |
|---|---|---|
| 1 | `…/b85203e9/subagents/agent-a9b839ac5eaa570bd.jsonl` | session transcript |
| 2 | `…/8917c7ca/subagents/agent-a9b839ac5eaa570bd.jsonl` | session transcript |
| 3 | `…/Temp/claude/…/7e07d404-…/tasks/a9b839ac5eaa570bd.output` | task output |
| 4 | `…/Temp/claude/…/8917c7ca-…/tasks/a9b839ac5eaa570bd.output` | task output |

So the task-output mechanism hard-links an agent transcript into a temp `tasks/`
directory as `.output`, and each involved session contributes one transcript link
plus one temp link. `7e07d404` appears only in the temp path — a session id with
no transcript of its own in this project.

Both sessions mirrored the file independently, and the mirrors **diverge**:

| | live file | `b85203e9` mirror | `8917c7ca` mirror |
|---|---|---|---|
| records | 15 | 5 | 16 |
| `mirrorOf` | — | its own `subagents/` path | its own `subagents/` path |

Each mirror's `mirrorOf` points at its own session's path, and both paths name
the same bytes. The uuid chain shows why this is benign: records 9 and 10 share
`parentUuid: 6d86aeff` — a genuine fork point — with record 9 (07:42:07) under
`b85203e9` and record 10 (07:42:22) under `8917c7ca`. The conversation moved to
the new session and kept writing into the same file. The `b85203e9` mirror is
simply the last state it observed (09:37); `8917c7ca` took over and mirrored 16
records by 09:41. **Nothing overwrote a good view with a stale one.**

What survives as a design observation, not a bug: the colocated-mirror model
assumes one mirror owns one transcript. Here it is 1:N by construction, with
per-mirror `.lock` files that do not exclude each other. The observed sequence
was sequential; **concurrent liveness of both sessions was not tested.**

## Finding 4 — GC deletes the mirror, not the agent transcript

`MirrorFile.CollectGarbage` deletes a mirror when its `mirrorOf` target no longer
exists (`MirrorFile.cs:346-351`), and the colocated sweep resolves an agent
mirror's transcript as `<sessionDir>/subagents/<stem>.jsonl`
(`MirrorFile.cs:411-413`). Both are **plain `File.Exists` path checks**.

With multiply-linked files that is conservative in one direction — a link
disappearing from one session does not make the file vanish, and the other links
keep the inode alive, so `File.Exists` on a surviving path stays true and that
mirror is kept. The hazard is the reverse direction, and it is real: if one
session's `subagents/` link is removed, **that session's mirror is deleted**,
while the file itself lives on under the other links.

With finding 1 rewritten, the refs at risk are narrower than first stated: the
on-disk ones are QUOTED commands in agent prose (out of healing scope by design
— fork-heal deliberately leaves quotes verbatim), and the fork's LIVE inherited
context (out of reach of any disk tool). A dead quoted ref degrades an archived
conversation's replayability, not the live session. Watch-item, not a defect.

Not demonstrated; no aged-out example was available. Stated as the mechanism to
check first.

## Methodology correction 2: regex measurements must run on DECODED strings

The first pass's central detection claim fell to this: a regex containing a
literal `"` (like ForkHealRule's `run\.sh"`) never matches raw JSONL, where the
quote is escaped as `\"`. Any measurement of a rule's pattern must be run
against the decoded string values the rule actually visits
(`node.ForEachString`), never against the raw line. This is the second time the
trap has bitten — `Compactor.MirrorLost` carries an explicit JSON-escaped
variant for exactly this reason, and its comment records the first time.

## Methodology correction: `st_ino` is unsound on Windows

An early link-detection pass compared `os.stat().st_ino` across a directory walk.
That is **not reliable here**: the reported inode for the same unmodified file
changed between two scans (`4503599628991780` → `5629499535834061`) while `nlink`
stayed 4. The walk found the right two paths by luck.

`fsutil hardlink list <path>` is the authoritative enumeration on NTFS. Any
future link-awareness in this codebase must not key on `st_ino` comparison.

## What was decided, and what stays open

The second pass resolved most of the first pass's open questions:

1. ~~Should a forked agent's inherited digests be healed at all?~~ **Dissolved.**
   There are no inherited digests on disk (rewritten finding 1) — the only
   parent-naming text is quotes, which fork-heal deliberately leaves verbatim.
   Nothing to heal, so nothing to decide.
2. ~~Detect via `fork-context-ref` or via launcher paths?~~ **Answered**: if
   detection is ever needed, `fork-context-ref` — text detection is
   envelope-gated and the envelope never survives the fork's re-serialization.
3. **Classification** (not on the first pass's list — it was the actual defect):
   **shipped 2026-08-18**, see finding 1.
4. **Adoption target / link-aware mirror ownership**: both stay hypothetical.
   `ParentMirrorFiles` resolves parent sessions from agent files already; the
   `agent-*.jsonl` candidate shape and link-awareness have no observed scenario
   that needs them. Finding 4's dead-quoted-ref path remains the watch-item that
   would reopen this.

Nothing urgent remains: no data is lost, every ref measured resolves, and forked
agent files now compact.

## Reproducing the measurements

All read-only. Paths assume the artifacts above still exist.

```bash
grep -c '\[claudinine:' <sess>/subagents/agent-<id>.jsonl
```

```bash
grep -o '"rule":"[a-z-]*"' <sess>/subagents/agent-<id>.jsonl | sort | uniq -c
```

```bash
powershell.exe -NoProfile -Command "fsutil hardlink list '<abs-path-to-agent-jsonl>'"
```

```bash
sh <sess>/claudinine/run.sh get <sess> --ref <ref> --info
```
