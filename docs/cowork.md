# Claudinine × Cowork — status, evidence, open work

Supersedes `cowork-compatibility.md`, `claudinine-cowork-report.md` and `cowork-packaging-workorder.md`.

**Verdict: functionally compatible in both modes.** Claudinine installs, runs, compacts, and
retrieves in Cowork cloud; it installs, runs, and compacts in local mode (v0.1.22, validated
2026-08-15 — hooks wired on the desktop host, transcript compacted, mirror colocated). Packaging was
the only hard blocker and it is fixed (`libexec/`, all six RIDs, launcher-based retrieval). All three
trigger-model gaps (O1–O3) are shipped and validated against a real cloud host. What remains:
the local-mode retrieval namespace split (the one functional gap — headers quote host paths the
sandbox cannot resolve), `PreCompact` (unobserved), fork/clone (untested), and one latent statusline
accounting defect.

Evidence comes from three live cloud sessions on 2026-08-15, `entrypoint: remote_cowork`: a
diagnostic-probe session (`f4bcf08f…`, environment measurements) and a functional session
(`2adf0db2…`, plugin `0.1.20` by plugin-file import), both on Claude Code `2.1.233`; and a validation
session (`a4fc865d…`, plugin **0.1.21**, Claude Code **`2.1.42`**) which exercised O1/O2/O3/O8/O13
end-to-end. The version spread matters: cloud hosts are not homogeneous, and the newer-numbered build
is not the one that ran last. The validation host also carried `CLAUDE_CODE_TRANSCRIPT_LOCAL_GC=1` and
the feature flag `tengu_ccr_delta_rehydrate: true` — the transcript is server-authoritative and
re-hydrated by delta, which is exactly the mechanism the `.load` stamp exists to price.

**Transcript layout on this host.** The live transcript is a *flat* file at
`<project>/<sid>.jsonl`, while `<project>/<sid>/` holds only the app's sidecars (`subagents/`,
`tool-results/`, `ccr-tip.json`) and our colocated `claudinine/` dir. `MirrorLocator` handles this
correctly — both stamps recorded the flat path — but any tooling that assumes the transcript lives
*inside* the session dir will find nothing (this is what broke `curate.py`, see O13).

## The host

| | cloud ("In the cloud") | local ("On your computer", measured 2026-08-15) |
|---|---|---|
| entrypoint | `remote_cowork` | `local-agent` |
| Claude Code | 2.1.233 / 2.1.42 | 2.1.229 |
| host | Firecracker VM, `x86_64`, Ubuntu glibc 2.39 | **split**: Bash tool in a Linux VM (`/sessions/<name>`), hooks on the **desktop host** (Windows measured) |
| `$HOME` | `/root` (cwd `/home/claude`), hooks and Bash agree | Bash: `/sessions/<name>` (home = cwd); hooks: the Windows profile |
| session store | `~/.claude/projects/` in the container | host-side, per cowork-session: `%APPDATA%\Claude\local-agent-mode-sessions\<install>\<device>\local_<id>\.claude\projects\<slug>\` — VM sees it **read-only** at `<mnt>/.claude/` |
| session life | server-side, survives desktop restarts; `SessionEnd` on idle, re-hydrates from transcript on next activity | untested |
| plugin source | `~/.claude/plugins/synced/<name>/`, account-hosted install | host: `…\rpm\plugin_<id>\`; VM view: `<mnt>/.remote-plugins/plugin_<id>/` |
| marketplaces | disabled (`SKIP_PLUGIN_MARKETPLACE=true`) | untested |

**The "only Linux binaries ever execute" premise was wrong.** It held for cloud, but local Cowork
executes hooks on the **desktop host** — the Linux VM exists (it is the Bash sandbox), hooks just
don't run there (see O12 for the measurement). The hosted `.plugin` therefore carries **all six
RIDs plus the `claudinine.cmd` twin** again; a Windows or macOS desktop resolves its native binary
through the same shim. The shim routes on `uname -s`/`uname -m`; a missing RID is a hard failure,
not a slow path — every hook dies exit 127.

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
orphaned `tool_result`, `tool_use`/`tool_result` pairing intact. No dangling `parentUuid` *introduced
by a pass* — the validation session found two danglers that the host itself wrote and the mirror never
saw, so this assertion has to be phrased as "no new danglers", not "none" (see O1).

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

**O1 · `SessionStart` does not fire on wake-from-idle — FIXED, validated on cloud (2026-08-15 test run).**
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
written → consumed → no replay on the next prompt).

*Cloud validation.* A real idle teardown was observed at **17:23:16** by a 2-second poller: the
`claude` pid left the process table and, in the same tick, `SessionEnd` ran a full pass (mirror
547,106 → 557,167 B) and wrote both `<stem>.end` and `<stem>.load`. After a 22-minute idle stretch a
scheduled wake re-entered the session; by then the `.end` marker was gone and the start work had run.

The load stamp is the part that had to be exact, and it is: the stamp holds **51 records / 121,159 B**
and the re-hydrated transcript reproduces **51/51 byte-for-byte, 0 missing, 0 size drift**. Delta
re-hydration therefore returns the file *as `SessionEnd` left it* — compacted — so the compaction
survives the round trip and the watermark prices a reload exactly. (Beware when checking this by hand:
count **bytes**, not characters. Measuring `len(str)` in Python reports 11 phantom mismatches of 4–18
B on records containing em-dashes and arrows — an artefact of the ruler, not of the stamp.)

Two honest limits on this run. First, `.load` was rewritten at **17:37**, 14 minutes after the
teardown and with no prompt and no appended record in between — so the host tore down (or re-loaded)
a second time during one idle stretch. Second, that means the wake path cannot be *isolated* from the
evidence at hand: `SessionEnd` and `SessionStart` both write `.load`, and both `SessionStart` and the
wake consume `.end`, so the marker's absence at 17:45 is consistent with either. What is proven is the
outcome the fix exists to guarantee — teardown recorded, stamp exact against the re-hydrated file,
marker consumed, start work not skipped. Isolating *which* boundary consumed it needs a host-side
hook trace, not more file forensics.

*Incidental, and worth knowing before trusting `parentUuid` chains on this host:* the live transcript
carries **2 records whose `parentUuid` (`9f7ed389…`) is absent from the transcript** — and absent from
the mirror too, which is the exoneration: the mirror is the union of everything claudinine ever saw,
so a record it never saw is one it cannot have removed. The host itself wrote children of a parent it
never persisted (17:08, the turn where a stale scheduled wake landed). Rules must tolerate dangling
parents in Cowork transcripts rather than treat them as corruption, and integrity assertions phrased
as "0 dangling `parentUuid`" will fail here through no fault of ours.

**O2 · No `Stop` trigger — FIXED, validated on cloud (2026-08-15 test run).** Autonomous stretches — scheduled tasks, `/loop`, Workflow
runs, Monitors — pass dozens of turns with no user prompt, so `UserPromptSubmit` never fires and
nothing compacts. `Stop` is the per-turn boundary that always fires; it is now registered and runs
the same steady pass under a min-interval guard: every completed pass (any event) touches a
`<stem>.pass` stamp in the colocated dir, and `Stop` skips when the stamp is younger than 120 s — so
interactive sessions, where the per-prompt pass already runs, do not pay twice per turn. `Stop` also
joins the wake boundary from O1 (a pending `.end` marker bypasses the throttle), covering autonomous
resumes that never see a prompt — verified end-to-end locally with the real binary.

*Cloud validation — both halves.* **Fires:** an autonomous turn (subagent fan-out plus a long bash
stretch) ended with the pass stamp 6 minutes stale; `Stop` ran and took the live transcript
**285,170 → 35,865 B, 87.4% in one turn**, mirror keeping all 98 originals, result 18 records with 0
dangling `parentUuid` and 0 unpaired `tool_use`/`tool_result`. That entire stretch had no user prompt
in it and would have compacted **not at all** under the old model — this is the single largest
measured win of the whole exercise. **Throttles:** a deliberately short turn ended ~35 s after the
previous pass; `Stop` ran nothing and the stamp did not move until the next prompt arrived 3 minutes
later. The 120 s guard behaves as designed on a real host, in both directions.

**O3 · Subagent compaction is boundary-bound — FIXED, validated on cloud (2026-08-15 test run).** `CompactSubagents` ran only at
`SessionStart`/`SessionEnd`, so a Workflow's agent files stayed fat until the session ended — and
subagent transcripts are the best-compacting file type (82% on the corpus). `SubagentStop` is now
registered and compacts exactly the file the event names (`agent_transcript_path`), on the spot: no
enumeration, cost proportional to one agent's output, and the live session transcript is never
touched mid-turn. Skip markers are honoured with the same session-or-file logic as the boundary
sweeps, which still run as the repair path for agents whose `SubagentStop` was missed. Verified
end-to-end locally (agent file 11.9 KB → 4.2 KB at the event, mirror + sidecars in the session's
`claudinine/` dir, session transcript byte-identical).

*Cloud validation.* Two agents finished together and both files were compacted at the event, mid-turn,
while the session transcript kept growing untouched: `agent-a965f68d` **51 records / 185,206 B →
8 / 42,028 B** (chain-collapse folded 19 tool calls into one `[ref]` digest), with `isSidechain`
intact on every record, 0 dangling `parentUuid`, 0 unpaired `tool_use`/`tool_result`, and every line
still parsing. Retrieval through the launcher then worked exactly as the digest header spells it —
`--info` (4,100 B / 74 lines), `--ref --grep`, and `--full` returning 74/74 lines. Across the five
agent files this session produced, live totals **142,362 B against 801,817 B of mirrored originals
(82.2%)**, and those ratios held byte-for-byte across the teardown in O1.

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
Still unobserved after the 2026-08-15 validation session: the hook is registered and the session ran
long, but it never approached the 80% threshold — partly *because* `Stop` now keeps the transcript
small, which is a pleasant irony and also means `PreCompact` may be rare in practice on this host.

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

**O8 · `version` reports `1.0.0` — FIXED, shipped in v0.1.21.** The binaries were compiled before
`set-version` ever ran (the compile job never saw `inputs.version`); fixed by passing
`-p:Version={inputs.version}` to `dotnet publish`, with assertions at all four invocation points
comparing `claudinine version` output to the dispatched version. Correction to an earlier claim
here: v0.1.20 did NOT carry the fix — that pipeline change was still develop-local when 0.1.20 ran,
so its binaries report `1.0.0` (and its green run proves nothing, since the assertions ship in the
same commit). PR #10 synced the pipeline to main and v0.1.21 (2026-08-15) is the first release whose
binaries report their real version, assertion-verified on every RID in the run log — and
independently confirmed on a real host the same day (the local-mode sandbox ran the shim and got
`0.1.21`, see O12).

**O16 · The statusline under-reports exactly the rules that save the most — NEW, measured
2026-08-15.** `StatuslineVerb.Measure` prices a reload as, for each record **still present** in the
transcript, `buffer − size` (buffer = the `.load` size, else the mirror original). Records that a rule
removed *outright* are therefore never counted — the loop only iterates over what is still in the
file. The code comment justifies this for records "the transcript dropped entirely (pre-boundary
history)", which is right for host-side drops, but chain-collapse and the dedup rules also work by
**removing** records, and those bytes are genuinely reclaimable: the buffer still holds them, a
reload would not.

Measured on a synthetic session built from real material (a chain-collapsed agent file standing in
for a session transcript, `.load` stamped with the fat originals as a reload would have delivered
them):

| | records | bytes |
|---|---|---|
| buffer as loaded (`.load`) | 50 | 185,005 |
| transcript after the pass | 8 | 42,020 |
| removed outright by rules | 42 | 148,005 |
| **true reclaim on reload** | | **142,985** |
| **what the statusline reports** | | **0 — it prints nothing** |

The real session was correctly silent for an unrelated and benign reason (after O1's `SessionEnd`
stamp, `.load` matched the file byte-for-byte, so nothing *was* reclaimable). The defect only surfaces
mid-session, after rules drop records that were present at the last load — which on this host is the
common case, since chain-collapse is the dominant rule in Cowork (87.4% of the O2 win came from it).

Three caveats before anyone treats this as urgent. It is **cosmetic**: `Measure` feeds only the
statusline hint, never a compaction decision, so no data is at risk. It is **invisible in Cowork** —
the Cowork UI has no status line to render one. And it is **currently latent on the CLI too**: the
plugin registers no `statusLine` anywhere since 2026-08-12 (there is no non-brittle way to point
`statusLine.command` at a plugin binary — `${CLAUDE_PLUGIN_ROOT}` expands only in plugin hooks), so
today `Measure` runs only for a user who wired the verb into their own settings by absolute path.
Net: this costs nobody a number today; it is a prerequisite for re-enabling the statusline, not a
live regression.

The naive fix (count any `.load` uuid missing from live as fully reclaimed) over-reports the opposite
way: a host-side `/compact` boundary mid-session also drops records that were in `.load`, and those
are gone from the buffer too. Claudinine can tell the two apart — it knows which uuids *it* removed,
from the mirror and the digest envelopes — so the fix is a discrimination, not a one-liner, and wants
its own test over both shapes.

### 3. Packaging & release

**O9 · The CI path — GREEN (assertions corrected 2026-08-15).** The `-Hosted` pack flag, the two
artifacts (CLI zip keeps `bin/` forwarders for the human verbs; hosted `.plugin` omits `bin/`), the
`test ! -e verify-hosted/bin` assertion, and release publication of both are in `build.yml`/`cd.yml`
and merged to main. The hosted verify step originally asserted the Linux-only RID set *and the
absence* of the four non-Linux ones — an assertion that turned out to actively enforce the O12
blocker once local mode was measured, so it is now inverted: all six RIDs plus `claudinine.cmd`
must be present, `bin/` must still be absent. The Publish release run on main (2026-08-15 13:21,
run 31887005499) went green and published v0.1.20 with both assets — CI-produced, though still
version-blind (see O8); v0.1.21 (run 31894149339) is the fully correct artifact, and it also carries
the Fable-safeguards header fix every install needs. Last remaining step: import
`claudinine-0.1.21.plugin` into claude.ai to replace the hand-packed install (one manual action,
same validator that already accepted the layout).

**O10 · Bundle size against the sync limits — RESOLVED, and the 64% cut REVERTED (2026-08-15).** The
syncer enforces roughly 4096 files / 25 MB per file / 64 MB total. Measured against the published
0.1.20 artifact: six RIDs are 20.34 MB unpacked (largest single file 3.72 MB), so the fat bundle was
never actually at risk of the caps. On that size margin the hosted `.plugin` briefly went Linux-only
(7.27 MB) on the premise that "the other four binaries can never execute in a Cowork session" — false
for local mode, where hooks execute on the desktop host (O12), so the cut broke local mode outright
and is reverted: the hosted bundle carries all six RIDs again. The size math above is what makes the
revert free. Per-RID: linux-arm64 3.72, linux-x64 3.53, osx-x64 3.47, osx-arm64 3.43, win-arm64 3.17,
win-x64 3.01 MB.

**O11 · Install route and README claims — DONE.** The README's Install section now documents the
claude.ai account-import route (marketplaces disabled, Linux-only artifact) alongside
`/plugin install`, and the restore section shows the launcher form
(`sh …/<sid>/claudinine/run.sh restore-compaction-off <sid>`) for hosted installs.

### 4. Coverage gaps

**O12 · Local "on your computer" mode — FIXED AND VALIDATED (2026-08-15, v0.1.22 re-import: hooks
execute on the desktop host, transcript compacts, colocated mirror present).** History of the find,
from the first local run on 0.1.21: Windows desktop, entrypoint `local-agent`, Claude Code 2.1.229, plugin 0.1.21 account-import:
installs, all six hooks register (`PreCompact` included), and the shim runs from the sandbox printing
`0.1.21` — the first independent confirmation of O8 on a real host. But **every hook fires and dies,
exit 127, on every boundary — nothing compacts in local mode on any 0.1.20/0.1.21 install.** Root
cause from `hook_non_blocking_error` stderr: local Cowork runs hooks **on the Windows host**, not in
the Linux VM — Claude Code invokes the sh shim through Git Bash (MSYS `/c/…` paths), `uname -s`
returns `MINGW*`, the shim correctly resolves `libexec/win-x64/claudinine.exe` — which the O10 cut
removed. The shim's own header comment anticipated exactly this path; the 64% bundle cut is what
broke it. *Fixed in-tree:* the hosted `.plugin` packs all six RIDs plus `claudinine.cmd` again
(`pack-plugin.ps1`, `-HostedRids` remains as a deliberate-slimming override), and O9's
absence-assertion — which actively enforced the bug — now asserts presence of all six. Shipped in
v0.1.22 and validated the same day by a fresh local run after account re-import.

Findings from the same run, recorded for whoever validates next:

- *The read-only mount was a red herring.* The VM sees the session store read-only
  (`touch` → EROFS), which looked like a hard blocker — but hooks never run there. Host-side the
  store is ordinary writable AppData; verified by direct inspection: flat transcript only, no
  `<sid>/` dir at all, zero claudinine artifacts, 9 `hook_non_blocking_error` records
  (SessionStart 1, UserPromptSubmit 4, Stop 4).
- *Two id namespaces:* the cowork session id (`local_1ea9e2a1…`) and the Claude Code `sessionId`
  (`e690121b…`) are unrelated; the store path embeds the former, the transcript stem is the latter.
- *`cwd` lies about the project:* every record carries the outputs scratchpad as `cwd` with
  `gitBranch: HEAD` — anything keying on `cwd` to find the repo lands in the wrong place.
- *Record shapes present* (queue-operation 6, attachment 11, last-prompt 5, mode 2): every shape
  O5/O6 describe, so those rules get exercised the moment hooks can run.
- *Next decisive unknown — retrieval across the namespace split.* Once hooks run, claudinine
  executes natively on Windows and bakes **Windows absolute paths** into launcher headers
  (`sh "C:/Users/…/run.sh" get …`) — but the model's Bash tool runs in the VM, where that path does
  not exist. The store IS visible from the VM (read-only suffices for `get`) and so is the plugin
  dir, just under different roots (`<mnt>/.claude/…`, `<mnt>/.remote-plugins/…`), so a path
  translation exists in principle; whether the launcher should emit both forms is design work, not
  a patch. Until then, local-mode compaction will work but header-quoted retrieval commands will
  not resolve from the sandbox.

**O13 · No corpus-scale Cowork numbers — MEASURED (2026-08-15 test run).** Real Cowork transcripts,
genuine cl100k ruler:

| population   | n | baseline           | bytes | tokens | non-idempotent |
|--------------|---|--------------------|-------|--------|----------------|
| subagent     | 5 | 0.8 MB / 0.12 M tok | 82.2% | 88.2%  | 0 |
| main session | 1 | 0.5 MB / 0.04 M tok | 50.4% | 52.3%  | 0 |

Per-file wins 5/5 on the subagent population (median 84.7%), and both populations land where the CLI
corpus predicted (82% agent / ~52.5% live-context) — Cowork transcripts compact like CLI ones; the
shapes differ but the ratios hold. Honest caveats: n=6 is a smoke corpus, not the 95-file snapshot,
and the main-session baseline is reconstructed (mirror originals + the tail records not yet
mirrored) because a live session's mirror can never cover its own tail — `curate.py` correctly
refuses it, so that number is labelled mirror+tail, not a stock curate run. The run also surfaced
three bench defects, all fixed in-tree the same day: `curate.py` now probes the colocated
`claudinine/` mirror dir (it only knew the legacy flat pools, so on any current install every
compacted session skipped as "no mirror"), excludes `**/claudinine/**` from the transcript scan (a
colocated mirror is a baseline, not a candidate), and the ruler self-seeds tiktoken's cache from the
npm `gpt-tokenizer` package when the openaipublic CDN is blocked (hash-checked against tiktoken's own
expected_hash — identical encoder or refusal, see `eng/bench/ruler.py`).

**O14 · Fork/clone under Cowork resume.** `ForkHealRule`'s parent-genuineness test has not been
exercised against whatever Cowork does on resume, cross-device continuation, or a scheduled task
bound to a persistent session. Partially closed: the 2026-08-15 run drove five scheduled tasks bound
to a persistent session (`send_later`) through teardown and re-hydration, and no fork ever appeared —
resume continues the same session id and the same record chain, so `ForkHealRule` never had cause to
engage. Cross-device continuation and an actual branch remain untested.

*Operational note on scheduled tasks, which the design should account for:* each firing delivers a
real `UserPromptSubmit` into the persistent session, which **resets the host's idle timer**. A session
kept alive by a recurring schedule may therefore never reach `SessionEnd` at all — and firings can
also arrive **out of order** relative to the work (a wake queued at 17:08 for 17:11 landed after a
turn that had already done its job). Both push the same conclusion: `Stop` is the trigger doing the
real work in Cowork, and the `.end`/`.load` machinery is a repair path for the teardowns that do
happen, not the main mechanism.

### 5. Docs

**O15 · The Cowork caveat in the README — DONE.** The README now carries the honest framing (in "How
it works", after the resume-benefit paragraph): wake-from-idle re-hydration happens repeatedly within
one session's life, so the re-read benefit applies *more often* in Cowork than in the CLI; what
shrinks is the long-tail archive value, since the transcript and its side file die with the
container. The wrong draft framing ("ephemeral, so the resume benefit largely does not apply") never
shipped.
