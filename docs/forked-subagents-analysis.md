# Forked subagents: what the transcripts actually look like

Status: **analysis only, no fix applied.** Everything below was measured on disk
on 2026-08-18. Nothing here is a proposal; the last section lists what a fix
would have to decide.

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

## Finding 1 — inherited digests defeat fork-heal's detection (CONFIRMED LIVE)

The agent transcript contains **three `[claudinine:` digest headers and zero rule
stamps**. Our compactor never wrote this file: the headers arrived as copied
parent context. They carry **four launcher paths naming the parent session**
`b85203e9`, inside a file whose own identity is `agent-a9b839ac5eaa570bd`.

`ForkHealRule.Apply` derives `currentSid` from the file stem
(`ForkHealRule.cs:40`), so here it is `agent-a9b839ac5eaa570bd`. It then collects
foreign sids from the digest's retrieval command. Measured on this file, that
capture returns **nothing at all** — the get-target set is empty. The parent sid
exists only in the *path* portion of the launcher invocation, which the capture
group does not cover. `flagged.Count == 0`, and the rule returns
(`ForkHealRule.cs:58-59`) before any parent lookup happens.

So the gap is in **detection**, not resolution. This was previously recorded in
`session-file-changes.md` as reachable in principle but unreached in practice.
That is no longer accurate: it is reached.

A second gap sits behind it. Even if detection fired, `MirrorFile.MirrorUuidsOf`
resolves a parent through `ParentMirrorFiles`, which only ever constructs
`<sid>/claudinine/<sid>.jsonl` and `<pool>/<sid>.jsonl` candidates
(`MirrorLocator.cs:184`), never `agent-*.jsonl`. Both gaps would have to be
closed together.

**Severity today: latent, not active.** All refs in the agent file resolve, from
both sessions. It becomes real only when a referenced session is aged out while
an agent file that references it survives — see finding 4 for why that
combination is plausible rather than theoretical.

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
  it survives header-format changes.
- **We do not model this type.** It has no `uuid`, no `sessionId`, no `message`.
  It sat at the head of a file that our tail walk and reachability guard
  processed without complaint, but nothing in the code anticipates it, so that is
  an untested tolerance rather than a designed one.

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
while the file itself lives on under the other links. Combined with finding 1,
that is the concrete path to a dead ref — an agent transcript surviving with
digest headers pointing at a mirror GC has already collected.

Not demonstrated; no aged-out example was available. Stated as the mechanism to
check first.

## Methodology correction: `st_ino` is unsound on Windows

An early link-detection pass compared `os.stat().st_ino` across a directory walk.
That is **not reliable here**: the reported inode for the same unmodified file
changed between two scans (`4503599628991780` → `5629499535834061`) while `nlink`
stayed 4. The walk found the right two paths by luck.

`fsutil hardlink list <path>` is the authoritative enumeration on NTFS. Any
future link-awareness in this codebase must not key on `st_ino` comparison.

## What a fix would have to decide

Open questions, in the order they gate each other:

1. **Should a forked agent's inherited digests be healed at all?** They resolve
   today. The argument for healing is finding 4; the argument against is that
   rewriting inherited parent context inside an agent file changes records the
   agent did not author.
2. **If yes, detect via `fork-context-ref` or via launcher paths?** The record's
   `parentSessionId` is cleaner, but only exists for fork-mode agents; copied
   headers could in principle reach a plain agent file by other routes.
3. **What is the adoption target?** `ParentMirrorFiles` would need an
   `agent-*.jsonl` candidate shape, and a rule for which of several linked
   sessions is the right parent when more than one mirror exists.
4. **Does mirror ownership need to become link-aware?** That is the largest
   change and the least justified by current evidence — the divergence in finding
   3 was benign and operator-caused.

None of this is urgent: no data is lost, and every ref measured resolves.

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
