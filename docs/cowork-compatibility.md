# Claudinine × Cowork — compatibility checklist

Status legend: **[V]** verified live in a Cowork cloud session (2026-08-15, Claude Code 2.1.233,
`entrypoint: remote_cowork`) · **[?]** unknown, needs a test · **[!]** known gap, needs work ·
**[X]** closed.

Rev 3 (2026-08-15): **A2 closed green** — the account plugin pipeline preserves exec bits AND
binary bytes, so a native binary can ship in a hosted plugin as long as it is not in `bin/`. A4, A5
and A6 closed by the same probe run. A0 (no top-level `bin/`) stands as the packaging blocker; E5
(retrieval without PATH) is the consequence. C3 gains a concrete mechanism: `SubagentStop` carries
`agent_transcript_path`.

Rev 2: E1 and A6 closed by measurement; E2 closed by the mirror-colocation change
(`<project>/<sid>/claudinine/`) plus the `MirrorLost` tripwire in `Compactor`; new item E4.

Two Cowork runtimes matter, and they differ:

| | cloud ("In the cloud") | local ("On your computer") |
|---|---|---|
| host | Firecracker VM, `x86_64`, ephemeral, snapshot/restore | Linux VM inside the desktop app, host arch |
| `$HOME` | `/root` (cwd `/home/claude`) | ? |
| lifetime | reclaimed on idle; `--session-mode resume` re-runs `claude --resume` | ? persists across sessions |
| plugin source | `~/.claude/plugins/synced/<name>/` (account sync) | ? |
| marketplaces | disabled (`SKIP_PLUGIN_MARKETPLACE=true`) | ? |

---

## A. Distribution & install

- **A0 [!] BLOCKER — claude.ai-hosted plugins may not ship a top-level `bin/`.** Measured
  2026-08-15 by uploading a probe plugin; the validator refused it: *"Plugin contains a top-level
  bin/ directory … claude.ai-hosted plugins may not ship bin/ executables because they are added to
  PATH on the CLI but are not shown on the admin approval surface. Declare executable entry points
  via hooks, commands, or mcpServers instead."* Claudinine's shipped layout is exactly that
  (`bin/claudinine` shim + `bin/<rid>/claudinine`), so **the plugin as packaged today cannot be
  installed into a Claude account at all**, and account install is the only route into cloud Cowork.
  The GitHub-archive/marketplace route (CLI) is unaffected. Consequences: (a) executables must move
  to a directory not named `bin/` — pending confirmation that any executable payload is permitted;
  (b) E1 re-opens, because the PATH injection that made bare `claudinine get` work *is* the banned
  mechanism. See E5.
- **A1 [!] Establish the install path.** `/plugin install claudinine` is not it: cloud sessions run
  with `SKIP_PLUGIN_MARKETPLACE=true`, and plugins arrive pre-synced from the account into
  `~/.claude/plugins/synced/<name>/`. Decide the supported route — `.plugin` bundle installed into
  the account from the desktop app (then synced), vs. local-mode-only marketplace install.
- **A2 [X] Executable bit AND binary integrity survive the plugin sync.** Measured 2026-08-15 with
  `claudinine-probe` v0.2 installed into the account and synced into a live cloud session: the three
  files packed `755` materialised `-rwxr-xr-x` at
  `~/.claude/plugins/synced/claudinine-probe/libexec/`, data files stayed `644`, and direct
  execution without a shell interpreter worked. A 4 KiB blob of NULs, `0x1a`, lone CR/LF, `0xff
  0xfe` and invalid UTF-8 round-tripped byte-identical (sha256 match, 4096 bytes). **A native binary
  can ship in a hosted plugin** — the 1980-mtime/`644` pattern seen on `cowork-plugin-management` was
  simply a markdown-only plugin, not evidence of stripping. Packaging constraint is A0, not this.
- **A3 [?] Sync limits.** The syncer has a `synced_file_limit_exceeded` path with constants around
  4096 files / 25 MB per file / 64 MB total. Six RIDs ≈ 19 MB unpacked — probably fine, but confirm.
  Consider a Linux-only bundle for Cowork (see B1): smaller, and no dead mac/win binaries.
- **A4 [X] `CLAUDE_PLUGIN_ROOT` for synced plugins.** Resolves to
  `~/.claude/plugins/synced/<plugin-name>`, as seen by the hooks themselves. Whole tree intact.
- **A5 [X] Synced-plugin hooks register under the cowork entrypoint — and do so mid-session.** The
  plugin was installed into the account while a cloud session was already running (started 08:02,
  synced 12:28); its `UserPromptSubmit` hook fired on the very next prompt at 12:30, and
  `SubagentStop` on the next subagent. No restart needed. `SessionStart` did not fire, having
  already passed — so a first-install session skips the repair pass, which is harmless.
- **A6 [X] `CLAUDE_PLUGIN_DATA` naming.** `~/.claude/plugins/data/<plugin-name>-<source>` — an
  account-synced plugin gets `-inline` (`…/data/claudinine-probe-inline`), a skills-dir install got
  `-skills-dir`. The legacy `claudinine-*` glob matches both, and post-colocation this only affects
  reads of pre-migration mirrors. `HOME=/root` in both hook and Bash contexts, so the legacy
  `~/.claudinine` fallback agrees too.

## B. Runtime & platform

- **B1 [V] Only Linux binaries ever execute.** Cloud is `linux-x64`; local mode is a Linux VM, so
  `linux-arm64` on Apple silicon hosts. The `win-x64` publish in the tree is irrelevant to Cowork in
  both modes. Verify the `bin/claudinine` shim's arch dispatch works on both.
- **B2 [?] libc compatibility.** Confirm the Native AOT binary runs on the cloud image (glibc
  version, `libstdc++`/`libz` presence) and on the desktop's Linux VM image, which may be a
  different distro.
- **B3 [?] Cold-start cost on this hardware.** 18 ms median was measured on a dev box. Re-measure in
  the Firecracker VM; the hook budget is shared with Cowork's own Python hooks
  (`user-prompt-submit-reply-reminder.py`, `stop-hook-git-check.sh`).

## C. Hook events & session lifecycle

- **C1 [V] Hooks work and the payload is the expected shape.** A probe plugin shipping its own
  executable fired `SessionStart`, `UserPromptSubmit` and `SessionEnd`, each with `session_id`,
  `transcript_path`, `hook_event_name`.
- **C2 [!] `UserPromptSubmit` is the wrong steady-state trigger for Cowork.** Cowork runs long
  autonomous stretches — scheduled tasks, `/loop`, Workflow runs, Monitors — where dozens of turns
  pass with no user prompt at all. The per-turn boundary that always fires is `Stop`. Evaluate
  adding `Stop` (Cowork already registers one) with a min-interval guard so the cost stays bounded.
- **C3 [!] Subagent sweep timing — with a better mechanism than sweeping.** `CompactSubagents` runs
  only at `SessionStart`/`SessionEnd`, while a single Cowork `Workflow` spawns dozens of agents
  inside one session, so `subagents/` grows unbounded between boundaries — and subagent transcripts
  are the best-compacting file type (82%). Measured: `SubagentStop` fires per agent and its payload
  carries **`agent_transcript_path`** (plus `agent_id`, `agent_type`, `stop_hook_active`,
  `background_tasks`, `session_crons`). So compact *that one file* the moment it completes — no
  directory enumeration, no waiting for a boundary, cost proportional to one agent's output.
- **C4 [X] `SessionEnd` fires on idle teardown.** Observed 12:42:32 after the session went idle;
  the process then exited and the container survived. So "clean at rest" is achievable in cloud.
- **C7 [!] `SessionStart` does not fire when an idle Cowork session is woken.** Cowork sessions live
  server-side and survive desktop-app restarts, so "restarting the app" neither creates nor restarts
  one. What *does* happen: after `SessionEnd` on idle teardown, the next activity resumes the session
  into a **new process** which re-hydrates from the transcript — observed at 13:27:32
  (`resume_hydrate_ms` ≈ 1.0s, pid 483, same session id). At that resume the probe's
  `UserPromptSubmit` fired and its `SessionStart` did **not**; the runner's timing keys show
  `hooks_init_ms` and `prompt_submit_hooks_ms` but no session-start hook phase. (One clean
  observation — the only other resume, 10:37:24, predates the probe's install.) A genuinely *new*
  session does fire `SessionStart` normally. Consequences: the crash-repair pass,
  `MirrorFile.CollectGarbage`, `SessionDirGc` and `LoadStamp.Write` never run on wake-from-idle —
  which in cloud Cowork is exactly when the transcript is re-read and the saving is realised, and it
  happens repeatedly within one session's life. `LoadStamp`'s premise ("the app seeds its context
  buffer from the transcript BEFORE SessionStart hooks run") has no hook to hang on there. Move that
  work to the first `UserPromptSubmit` of a process (a process-lifetime flag makes it
  once-per-process), keeping `SessionStart` as the CLI path.
- **C8 [?] Plugin state reaches a live session unreliably.** A desktop-app restart pushed
  `claudinine-probe` into this running session at 12:28 (`plugin_state_refresh_inline_ms`), but a
  later install and a later *deactivation* both failed to land, including across the 13:27 resume
  whose sync reported `plugins_sync_install_ms: 1`. **A brand-new session picks up account plugins
  reliably** — that is the supported way to test. Not a Claudinine problem; a testing-procedure note.
- **C6 [?] Cowork-only hook payload fields.** `SubagentStop` carries `background_tasks` and
  `session_crons`; worth a look at whether any other event exposes state a compactor should respect.
- **C5 [?] `PreCompact` fires.** Cowork forces autocompact lower (`CLAUDE_AUTOCOMPACT_PCT_OVERRIDE=80`),
  so this event should be *more* frequent than in the CLI. Confirm.

## D. On-disk layout & new record shapes

- **D1 [V] Core layout is identical.** `~/.claude/projects/<slug>/<sid>.jsonl`, sidecar dir
  `<sid>/`, and `<sid>/subagents/agent-*.jsonl` — confirmed by spawning a throwaway agent.
- **D2 [V/?] `<sid>/tool-results/*.txt`.** Cowork offloads oversized tool output to files and puts a
  path stub in the transcript — overlapping partially with the mirror. `RuleHelpers.PersistedOutputPath`
  already understands a persisted-output stub; verify it matches *this* format and that no rule ever
  drops the path (dropping it strands the file forever).
- **D3 [?] `<sid>/subagents/agent-*.meta.json`.** New sibling of each agent transcript. Confirm the
  sweep ignores it and that `restore-*` doesn't orphan it.
- **D4 [?] `<sid>/workflows/journal.jsonl`.** Workflow resume replays this. Nothing may touch it —
  `SessionDirGc` only deletes whole orphan dirs, so this is probably safe by construction; confirm.
- **D5 [!] Cowork-only record types.** The transcript carries shapes the corpus never had:
  `SendUserFile` cards with `file_uuid`, artifact create/update, task notifications, `Monitor`
  events, remote-devices MCP results (`device_stage_files`, `device_list_dir` — one of which already
  produced a 109 KB single-line result in this session), Workflow progress records. Every rule needs
  a pass over a real Cowork corpus, not just the CLI corpus.
- **D6 [?] Fork/clone semantics.** Cowork resumes across devices and binds scheduled tasks to
  persistent sessions. Re-validate `ForkHealRule`'s parent-genuineness test against whatever Cowork
  does on resume/fork.

## E. Retrieval & mirror durability

- **E1 [!/X] PATH: the slot exists, but hosted plugins may not fill it.** Claude Code appends
  `<pluginRoot>/bin` to the **Bash tool's** PATH **unconditionally — even when no `bin/` directory
  exists**: with the probe installed, PATH contained
  `…/plugins/synced/claudinine-probe/bin` while the plugin shipped only `libexec/`. A non-`bin`
  directory is never added (`libexec` was not on PATH, `command -v` failed). Combined with A0 this
  means a hosted plugin gets a dangling PATH entry it is forbidden to populate at pack time.
  Materialising `$CLAUDE_PLUGIN_ROOT/bin/` from a `SessionStart` hook *would* work — and would
  reproduce exactly the un-reviewed PATH content A0 exists to prevent. Treat that as circumvention,
  not as the design. Use E5(a) instead. Original measurement: Claude Code appends every installed
  plugin's `bin/` directory to the **Bash tool's** PATH. Proven: a probe plugin's `bin/marker-tool` resolved
  via `command -v`, and the live PATH already ends with
  `…/plugins/synced/cowork-plugin-management/bin`. So the bare `claudinine get …` in digest headers
  works in Cowork as in the CLI. Notes: hook processes do *not* get plugin bin dirs on PATH (they
  get `CLAUDE_PLUGIN_ROOT` / `CLAUDE_PLUGIN_DATA`; the hook already uses an absolute path), and the
  same bin dir was appended twice — don't assume dedup. Conditional on A2.
- **E5 [!] Retrieval without PATH (re-opened by A0).** In a hosted install the binary won't be on
  PATH, so digest headers that say bare `claudinine get …` are dead there. Two candidate fixes:
  **(a)** every pass writes a one-line launcher next to the colocated mirror
  (`<sid>/claudinine/run.sh`) and headers emit `sh <…>/claudinine/run.sh get …` — fully compliant
  (no PATH entry), exec-bit-independent (invoked via `sh`), regenerated every pass so it can't go
  stale, and the path is already transcript-adjacent; costs a header change.
  **(b)** a `SessionStart` hook drops a shim into `~/.local/bin` (writable and on PATH in cloud —
  the probe measures this) — zero header change, but it re-creates precisely the PATH injection A0
  exists to prevent, so it is likely to be closed later and is a poor foundation. Prefer (a).
- **E2 [X] Mirror durability — solved by placement.** Mirrors are now colocated at
  `<project>/<sid>/claudinine/<stem>.jsonl`, inside the same sidecar dir as `subagents/`,
  `tool-results/` and `workflows/`, so any snapshot, restore, sync or delete carries mirror and
  transcript together. Backed by `Compactor.MirrorLost`, a fail-closed tripwire: a transcript
  carrying its *own* stubs with no mirror anywhere disables the whole pass (including the mirror
  append, so the evidence isn't blinded), while a fork carrying the *parent's* stubs still runs so
  `ForkHealRule` can adopt. Legacy flat pools stay read-only with one-time migration on first touch.
- **E3 [?] Mirror GC vs. cloud lifetime.** GC assumes transcripts age out slowly on a developer's
  disk. Cloud containers are recycled aggressively; check the interaction so GC never runs against a
  half-restored tree. Note `CollectGarbageColocated` deliberately ignores recorded absolute paths
  (they go stale exactly when a tree moves) and tests sibling existence instead — the right call for
  snapshot/restore.
- **E4 [!] `MirrorFile.CollectGarbageColocated` has no caller.** `HookRunner` still calls only
  `MirrorFile.CollectGarbage()` (legacy pools) and `SessionDirGc.Run()`. A dead session's whole
  sidecar dir is reaped by `SessionDirGc`, so nothing leaks there — but files orphaned *inside a
  living session* (a deleted subagent transcript's mirror, its `.skip`/`.load`/`.seen`) are never
  swept, which is exactly the case the method was written for, and exactly the case Cowork's
  Workflow runs generate most. Wire it at `SessionStart`:
  `MirrorFile.CollectGarbageColocated(MirrorLocator.ClaudinineDirFor(transcriptPath))`.
  (Checked across all non-`Rules/` sources.)

## F. Cowork-specific UX & safety

- **F1 [?] Does the desktop UI render session history from the local JSONL, or from server-side
  state?** If local, digests become visible to the *user* mid-conversation, not just to the model.
  That's a product decision, not a bug — but it must be a decision.
- **F2 [?] Server-side transcript copy.** Cowork streams the session to the backend for cross-device
  viewing. Confirm rewriting the local file can't cause a mismatch, re-upload, or duplicate on resume.
- **F3 [—] `statusline` verb is inert in Cowork** (no status line). Harmless; just don't advertise it.
- **F4 [?] Where the benefit actually lands.** One-shot cloud sessions (scheduled tasks, quick asks)
  are discarded with the container — zero benefit, non-zero risk. Long resumed sessions and local
  mode are the real targets. Consider whether to no-op in obviously one-shot contexts.

## G. Validation

- **G1** Build a Cowork corpus (cloud + local, Workflow-heavy sessions included) and run
  `eng/bench/` against it. Expect different ratios from the CLI corpus.
- **G2** Idempotence + validity assertions over Cowork-only record types (D5).
- **G3** End-to-end in a real Cowork session: install → run a Workflow → let it idle out → resume →
  confirm the savings, and confirm `claudinine get` still retrieves.
- **G4** CI: add a Linux-only Cowork bundle target and a smoke test that exercises the synced-plugin
  layout.

---

## Suggested order

1. ~~**E2, A2, E1**~~ — all closed. The pipeline preserves exec bits and binary content, so the
   remaining problems are packaging shape and retrieval, not feasibility.
2. **A0 + E5** — repack executables outside `bin/`, and switch digest headers to a launcher next to
   the colocated mirror. These two together are what makes a hosted install work at all.
3. **E4** — one-line wiring, no reason to carry it.
3. **D5 + G1** — run the compactor over real Cowork transcripts and see what breaks. (This session's
   own transcript is already a valid specimen.)
4. **C2/C3** — the trigger model, which is where Cowork differs most from the CLI.
5. **A1/A4/A5** — packaging and install, once the above says it's worth shipping.
6. **F1/F2** — product-level calls.
