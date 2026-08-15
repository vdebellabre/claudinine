# Claudinine × Cowork — status, evidence, open work

Supersedes `cowork-compatibility.md`, `claudinine-cowork-report.md` and `cowork-packaging-workorder.md`.

**Verdict: functionally compatible.** Claudinine installs, runs, compacts, and retrieves in Cowork
cloud. Packaging was the only hard blocker and it is fixed (`libexec/`, launcher-based retrieval).
What remains is a trigger-model gap that costs most of the benefit on this host, plus coverage gaps.

Evidence comes from two live cloud sessions on 2026-08-15, Claude Code `2.1.233`,
`entrypoint: remote_cowork`: a diagnostic-probe session (`f4bcf08f…`, environment measurements) and a
functional session (`2adf0db2…`, plugin `0.1.20` installed by plugin-file import).

## The host

| | cloud ("In the cloud") | local ("On your computer") |
|---|---|---|
| host | Firecracker VM, `x86_64`, Ubuntu glibc 2.39 | Linux VM inside the desktop app, host arch |
| `$HOME` | `/root` (cwd `/home/claude`), hooks and Bash agree | untested |
| session life | server-side, survives desktop restarts; `SessionEnd` on idle, re-hydrates from transcript on next activity | untested |
| plugin source | `~/.claude/plugins/synced/<name>/`, account-hosted install | untested |
| marketplaces | disabled (`SKIP_PLUGIN_MARKETPLACE=true`) | untested |

Only Linux binaries ever execute in either mode. The `win-*`/`osx-*` RIDs are dead weight for Cowork,
and the hosted `.plugin` no longer carries them (nor the Windows `.cmd` shim) — see *Packaging* below.
Both Linux RIDs stay: cloud is `x86_64`, but local Cowork runs a Linux VM at **host arch**, so an
Apple Silicon desktop needs `linux-arm64`. The shim routes on `uname -m`; a missing RID is a hard
failure, not a slow path.

---

## Verified

**Packaging.** claude.ai-hosted plugins may not ship a top-level `bin/` — the validator refuses the
upload outright (*"added to PATH on the CLI but not shown on the admin approval surface"*). Moving
the shims and per-RID binaries to `libexec/` clears it; the plugin now installs and enables. The
account pipeline preserves **executable bits** (`755` packed → `-rwxr-xr-x` materialised) and
**binary content** byte-for-byte (4 KiB of NULs, `0x1a`, lone CR/LF, invalid UTF-8 → sha256 match).
`CLAUDE_PLUGIN_ROOT` = `~/.claude/plugins/synced/<name>`; `CLAUDE_PLUGIN_DATA` =
`~/.claude/plugins/data/<name>-inline`.

**Runtime.** `libexec/claudinine version` runs; the shim resolves `linux-x64` correctly; linkage is
`libm`/`libc`/`ld-linux` only, so glibc 2.39 has no floor problem. Hook cost is **14–17 ms** per
event on cloud hardware (dev box: 18 ms), against hook timeouts of 30–60 s (`UserPromptSubmit` and
`Stop` were raised 25 → 60: the wake pass — subagent sweep plus three GC sweeps — runs under them,
and it is heaviest exactly when a Workflow-heavy session was just torn down).

**Retrieval without PATH.** `claudinine` is *not* resolvable in the Bash tool — the bare form returns
`command not found`, exit 127. (Claude Code appends `<pluginRoot>/bin` to PATH unconditionally, even
when no such directory exists, so a hosted plugin gets a dangling entry it is forbidden to fill.) The
launcher solves it: `run.sh` / `run.cmd` are written next to the colocated mirror with the resolved
absolute binary path and regenerated each pass. Digest headers spell that form, and every variant
works when executed **exactly as written** — `--info`, `--ref --grep`, `--grep` across all archived
outputs, `--full`, and subagent sidecars addressed by agent id. A `--full` retrieval of an archived
`Read` was diffed against the file on disk: **69/69 non-blank lines recovered, 0 missing**.

**Mirror durability.** Mirrors are colocated at `<project>/<sid>/claudinine/`, so anything that
snapshots, syncs or deletes the session carries them with it. `Compactor.MirrorLost` is the
fail-closed backstop: a transcript carrying its own stubs with no mirror anywhere disables the entire
pass, mirror append included, while a fork carrying the parent's stubs still runs so `ForkHealRule`
can adopt.

**Compaction and safety.** Chain-collapse merges adjacent digests rather than accumulating them
(header count fell 5 → 4 while the file shrank). Idempotency is byte-exact across a repeat pass. Live
transcript ran 69% of its mirror equivalent after 9 digests — *bytes over whole files, not the corpus
metric* (BPE over `message.content` after the last compaction boundary), so it is not comparable to
the 74.8% headline. Subagent sweep at `SessionEnd` worked: 11 records / 34,386 B → 7 / 19,835 B, with
its own sidecar. Every line of live, mirror and subagent transcripts JSON-parses after 20+ passes; no
dangling `parentUuid`, no orphaned `tool_result`, `tool_use`/`tool_result` pairing intact.

**Cowork record shapes.** `SendUserFile` (with `file_uuid`), device-bridge calls, subagent
transcripts, and `tool-results/` offloads (433 KB → a `.txt` referenced by absolute path) all survive
compaction; the offloaded file is untouched and its pointer still resolves. `<sid>/subagents/`,
`<sid>/tool-results/` and `<sid>/workflows/` are the app's own sidecars and are left alone.

**Hook lifecycle.** Hooks fire with the expected payloads. `SessionEnd` fires on idle teardown, so
"clean at rest" is achievable. `SubagentStop` fires per agent and carries `agent_transcript_path`
(plus `agent_id`, `agent_type`, `stop_hook_active`, `background_tasks`, `session_crons`). Synced
plugin hooks register under the cowork entrypoint, including mid-session when a plugin arrives while
a session is running.

**Settled by reading the code, not observation.** `queue-operation` removal is deliberate and sound:
`QueueHistoryCollapseRule` replays every enqueue/dequeue/remove per `sessionId`, removes all-or-
nothing and **only when every queue ends empty**, skips a trailing op as possibly mid-flight, and
fails the whole file closed on anything it does not understand — so nothing pending is ever dropped.
Extra `last-prompt` records are `MetadataKeepLastRule` (`last-prompt`, `custom-title`, `mode`),
keep-last, uuid-less so nothing can reference them. `tool-results/` cannot be reaped by GC today:
`SessionDirGc` deletes a `<sid>/` dir only when `<sid>.jsonl` is gone and nothing was touched for
24 h, and `CollectGarbageColocated` is scoped to `<sid>/claudinine/` and only acts on
`.jsonl`/`.skip`/`.load`/`.lock`/`.seen`.

---

## Open

### 1. Trigger model — where most of the remaining value is

**O1 · `SessionStart` does not fire on wake-from-idle — FIXED in-tree, pending cloud verification.**
Cowork sessions live server-side and survive desktop restarts. After `SessionEnd` on idle, the next
activity resumes the session into a **new process** that re-hydrates from the transcript — observed
at 13:27:32 (`resume_hydrate_ms` ≈ 1.0 s, pid 461 → 483, same session id). At that resume
`UserPromptSubmit` fired and `SessionStart` did not; a genuinely *new* session does fire it. So the
start-boundary work (load stamp, subagent sweep, all three GC sweeps) ran once at session creation
and never again — while re-reads happen repeatedly within one session's life.

*The fix as shipped* (hooks are per-invocation processes, so "once per host process" lives on disk):
`SessionEnd` writes a `<stem>.end` marker into the colocated `claudinine/` dir; a `UserPromptSubmit`
that finds it is the first prompt after a teardown and consumes it, replaying the start work — the
subagent sweep and the housekeeping trio. The load stamp is *not* re-written at the wake: `SessionEnd`
stamps the file at rest at the end of its own pass, which is byte-for-byte what the next re-hydration
will load — exact where a wake-time stamp was a guess, and a `Stop` wake would be a full turn late
(the whole autonomous turn is appended before the hook fires). A real `SessionStart` consumes the
marker too, so a CLI resume never double-runs. Crash teardowns that skip `SessionEnd` stay uncovered;
`SessionStart` remains the repair path there. Wake mechanics verified end-to-end locally (marker
written → consumed → no replay on the next prompt). *Still to verify on cloud:* after a real idle
teardown + wake, the `.load` stamp matches the file as `SessionEnd` left it and the first prompt
consumes the `.end`.

**O2 · No `Stop` trigger — FIXED in-tree.** Autonomous stretches — scheduled tasks, `/loop`, Workflow
runs, Monitors — pass dozens of turns with no user prompt, so `UserPromptSubmit` never fires and
nothing compacts. `Stop` is the per-turn boundary that always fires; it is now registered and runs
the same steady pass under a min-interval guard: every completed pass (any event) touches a
`<stem>.pass` stamp in the colocated dir, and `Stop` skips when the stamp is younger than 120 s — so
interactive sessions, where the per-prompt pass already runs, do not pay twice per turn. `Stop` also
joins the wake boundary from O1 (a pending `.end` marker bypasses the throttle), covering autonomous
resumes that never see a prompt — verified end-to-end locally with the real binary.

**O3 · Subagent compaction is boundary-bound — FIXED in-tree.** `CompactSubagents` ran only at
`SessionStart`/`SessionEnd`, so a Workflow's agent files stayed fat until the session ended — and
subagent transcripts are the best-compacting file type (82% on the corpus). `SubagentStop` is now
registered and compacts exactly the file the event names (`agent_transcript_path`), on the spot: no
enumeration, cost proportional to one agent's output, and the live session transcript is never
touched mid-turn. Skip markers are honoured with the same session-or-file logic as the boundary
sweeps, which still run as the repair path for agents whose `SubagentStop` was missed. Verified
end-to-end locally (agent file 11.9 KB → 4.2 KB at the event, mirror + sidecars in the session's
`claudinine/` dir, session transcript byte-identical).

*Concurrency:* hooks fire in parallel — several agents' `SubagentStop`s land together while the
session's own `Stop` runs — and two passes over the same transcript could interleave buffered mirror
appends into corrupt lines. Every pass therefore runs under a per-transcript `PassLock`
(`<stem>.lock`, exclusive handle: sharing violation on Windows, flock on Unix, OS-released on crash).
Try-acquire only, skip on busy: the holder is running the same idempotent pass. Parallel agents lock
different stems and never contend; a boundary sweep skips an agent file whose own `SubagentStop` is
mid-pass; the `.end` teardown marker is written outside the lock so a busy lock can never lose a
teardown. Cross-process enforcement verified locally (pass skipped while a foreign process held the
lock, compacted normally after release). Two scope notes: `.lock` files whose transcript is gone are
reaped by the colocated sweep (a Workflow-heavy session would otherwise accumulate dozens for
long-reaped agents), and the lock's guarantee does not extend to the cross-session GC sweeps that run
inside it — those touch trees whose own hooks hold their own locks, tolerable because they act only
on long-dead targets.

**O4 · `PreCompact` never observed firing naturally.** It was invoked synthetically for timing only.
Cowork forces autocompact lower (`CLAUDE_AUTOCOMPACT_PCT_OVERRIDE=80`), so it should fire *more* than
in the CLI — and on an ephemeral host it is the clearest in-session win. Confirm it actually fires.

### 2. Small corrections

**O5 · One `system` record present in the mirror, absent from live — SETTLED by code reading.** Not
unexplained: the only rule in the catalog that removes a `system` record outright is
`StopHookSummaryStripRule`, and only a zero-signal `stop_hook_summary` (no output, no errors, no
additional context, no prevented continuation, no stop reason) qualifies — boundary-preserved and
`compact_boundary` records are protected. `HookSuccessStripRule` touches only `attachment` records.
One such record lands after nearly every turn, so exactly this diff is expected in any session.

**O6 · Document the `queue-operation` behaviour — DONE.** `session-file-changes.md` now spells out
the full contract: per-`sessionId` replay, all-or-nothing removal only when every queue provably ends
empty, why partial removal is unsound (positional content-less dequeues), the trailing-op skip, and
fail-closed on anything the replay does not understand.

**O7 · Regression test — DONE.** `NoGcPathTouchesALiveSessionsToolResults` (MirrorColocationTests)
runs all three GC paths — legacy-pool sweep, colocated sweep, SessionDirGc — over a live session with
`tool-results/` and `workflows/` content aged past the grace window, with reapable bait proving the
sweeps ran hot, and asserts the app's sidecars survive.

**O8 · `version` reports `1.0.0` — FIXED and verified by CI.** The binaries were compiled before
`set-version` ever ran (the compile job never saw `inputs.version`); fixed by passing
`-p:Version={inputs.version}` to `dotnet publish`, with assertions at all four invocation points
comparing `claudinine version` output to the dispatched version. The v0.1.20 release published
2026-08-15 by the green main run carries the fix, so "which build is deployed" is now answerable
from inside a session.

### 3. Packaging & release

**O9 · The CI path — GREEN.** The `-Hosted` pack flag, the two artifacts (CLI zip keeps `bin/`
forwarders for the human verbs; hosted `.plugin` omits `bin/`), the `test ! -e verify-hosted/bin`
assertion, and release publication of both are in `build.yml`/`cd.yml` and merged to main. The hosted
verify step also asserts the Linux-only RID set *and the absence* of the four non-Linux ones, so a
silent regression back to a fat bundle fails CI. The Publish release run on main (2026-08-15 13:21,
run 31887005499) went green and published v0.1.20 with both assets — `claudinine-0.1.20.plugin` on
the release IS CI-produced. Last remaining step: import that CI artifact into claude.ai to replace
the hand-packed install (one manual action, same validator that already accepted the layout).

**O10 · Bundle size against the sync limits — RESOLVED, measured.** The syncer enforces roughly
4096 files / 25 MB per file / 64 MB total. Measured against the published 0.1.20 artifact: six RIDs
are 20.34 MB unpacked (largest single file 3.72 MB), so the fat bundle was never actually at risk of
the caps. The hosted `.plugin` is now Linux-only regardless — **7.27 MB, a 64% cut** — because the
other four binaries can never execute in a Cowork session. Per-RID: linux-arm64 3.72, linux-x64 3.53,
osx-x64 3.47, osx-arm64 3.43, win-arm64 3.17, win-x64 3.01 MB.

**O11 · Install route and README claims — DONE.** The README's Install section now documents the
claude.ai account-import route (marketplaces disabled, Linux-only artifact) alongside
`/plugin install`, and the restore section shows the launcher form
(`sh …/<sid>/claudinine/run.sh restore-compaction-off <sid>`) for hosted installs.

### 4. Coverage gaps

**O12 · Local "on your computer" mode is entirely untested.** Different host arch (arm64 on Apple
silicon), unknown `$HOME`, unknown plugin source and marketplace availability, unknown whether
`~/.claude` persists across sessions. Everything above was measured in cloud only.

**O13 · No corpus-scale Cowork numbers.** One session with 9 digests is a smoke test. Run
`eng/bench/` over real Cowork transcripts — Workflow-heavy ones especially — using the corpus metric,
not bytes.

**O14 · Fork/clone under Cowork resume.** `ForkHealRule`'s parent-genuineness test has not been
exercised against whatever Cowork does on resume, cross-device continuation, or a scheduled task
bound to a persistent session.

### 5. Docs

**O15 · The Cowork caveat in the README — DONE.** The README now carries the honest framing (in "How
it works", after the resume-benefit paragraph): wake-from-idle re-hydration happens repeatedly within
one session's life, so the re-read benefit applies *more often* in Cowork than in the CLI; what
shrinks is the long-tail archive value, since the transcript and its side file die with the
container. The wrong draft framing ("ephemeral, so the resume benefit largely does not apply") never
shipped.
