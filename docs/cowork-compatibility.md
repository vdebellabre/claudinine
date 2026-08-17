# Claudinine × Cowork — compatibility checklist

Status legend: **[V]** verified live in a Cowork cloud session (2026-08-15, Claude Code 2.1.233,
`entrypoint: remote_cowork`) · **[L]** verified live in a Cowork **local** session on Windows
(2026-08-17, desktop app 1.30096.5.0, `local_<uuid>` layout, VM shell **down** throughout — two such
sessions: the pre-fix measurement run behind Rev 4, and the v1.1.0 validation run behind Rev 6) ·
**[?]** unknown, needs a test · **[!]** known gap, needs work · **[X]** closed.

Rev 6 (2026-08-17, later): **Rev 5 is validated live** — a fresh local session on the released
**v1.1.0**, VM down for its entire duration, exercised the shell-free path end to end. Hooks executed
on the Windows host and completed; local mode was detected structurally, landing the dump at exactly
`local_<uuid>/outputs/.claudinine/refs/`; the length stamp tracked the mirror as it grew
(23,970 → 362,972 B) so no-op passes skip; **47 ref files** accumulated, one input anchor and one
result per archived call. Fidelity was proven by *retrieval*, not inspection — `Grep` across the refs
dir resolved matches inside a 40,978 B archived `Read`, i.e. the header's own PREFERRED verb working.
The emitted header carried every claimed property: `DIR` stated once, `REF` bound
(`[ab12cd34] -> ab12cd34`), `mirror key: <sid>` breadcrumb, path-free short pointers, and an explicit
"this session's shell cannot run retrieval commands". **E6/E7/E8/E9 move from built to validated**;
B7's structural detection likewise. Two things the session could not measure from inside, both for
the same reason (the allowlist): the live transcript's size, hence the local compaction ratio, and
the plugin's own version string — `1.1.0` is the dispatched release, trusted from the pipeline rather
than read off the host. The allowlist itself reproduced exactly: `Glob` on `…\rpm\plugin_<id>\` was
refused while `outputs/` read freely. Still open and unchanged: **B2/B3/B8 and G5's next-window run**,
which all need the VM and are consolidated as **O18** in `cowork.md`, plus **D8**, which does not and
is carried as **O17** there. One correction to B8 while it is open: O12's 2026-08-15 observation that
the shim "runs from the sandbox printing `0.1.21`" was made on one of the four boot days, so it is
probably already affirmative evidence that `libexec` IS reachable from the VM — worth re-reading that
run's evidence before spending the next window on `ls /sessions/*/mnt/outputs/../../`. *(Follow-up,
same day: the re-read happened and closed B8 — the plugin tree reaches the VM via its own
`.remote-plugins` mount, no traversal involved; see B8. B2/B3 and G5's next-window run stay open.)*

Rev 5 (2026-08-17): **Rev 4's fixable items are built** (develop, same day). What shipped:
**B5/B1 →** `run.sh` now targets the plugin's `libexec/claudinine` routing shim (uname-based RID
dispatch at RUN time) instead of baking the generator's own binary — coherent on every host whose
shell can reach the path; local Cowork needs no launcher at all anymore, see next. **B7+E6 →**
local mode is detected structurally (a `local_<uuid>` ancestor with `outputs/` and `.claude/`
children — the write path's question is "where can the file tools read?", which only the layout
answers; the doc's tool-name discriminator remains right for classifying transcripts) and gets a
shell-free retrieval surface: every mirrored record is dumped as `outputs/.claudinine/refs/<ref>.txt`
(+ `<ref>-media-N.*` for base64 media) and digest headers teach `Read`/`Grep` against that dir
instead of shell commands. The colocated mirror STAYS canonical (durability, restore); the dump is
the retrieval projection, regenerated from the mirror when deleted, length-stamped to skip no-op
passes. A `mirror key: <sid>` breadcrumb in the local block keeps `MirrorLost` and `ForkHealRule`
working (their third accepted form, alongside bare and launcher). **E7 →** the promise is now
load-bearing: in local mode a failed refs dump stops compaction (mirror-first style), `MirrorLost`
tolerates a lost mirror while the dump still serves retrieval, and trips when both are gone.
**E8 →** `REF` is bound in every header form (`[ab12cd34] -> --ref ab12cd34`); short headers are
now sid-free, path-free POINTERS (no more dead bare-`claudinine` command), and the pointer target
is guaranteed live: header dedup keeps one full RETRIEVAL block per **compact-boundary segment**,
not per file, so the app's context slice always contains the instructions. Old short headers and
old bare-command media stubs are healed to the new forms in place; anchor-input and media stubs are
now self-sufficient in the launcher form (or the refs-file path in local mode). **E9 →** the
"≥ 2 tool calls" trigger inference was wrong: `MinCalls` is 1 and emission is gated by pure
economics (`digestCost × 1.1 < replacedBytes`); single-call turns pass through when their digest
can't pay, not because of a count floor. Still open from Rev 4: B2/B3/B8, D8, G5's next-window run.

Rev 4 (2026-08-17): first observations from **local mode on Windows**, which turns out to be
structurally unlike both cloud Cowork and the CLI, and is the only host where Claudinine's
generator and its retrieval shell run on **different operating systems**. Consequences: B1's "only
Linux binaries ever execute" is true of the *shell* and false of the *generator* (B5); E5(a) — the
launcher, the fix Rev 3 recommended — cannot work there at all (E6); the colocated mirror from E2 is
unreachable by the only tools that always exist (E7); and the VM that owns the sole shell booted 4
times in the 4 months to 2026-08-17 on this machine (B6). Retrieval never once executed during a long, tool-heavy
session and nothing surfaced an error (E8). New: B5–B8, D7, E6–E9, G5.

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
| host | Firecracker VM, `x86_64`, ephemeral, snapshot/restore | **two hosts**: agent + hooks + file tools on the **Windows** host; `mcp__workspace__bash` in a Linux microVM |
| agent/hook platform | `linux-x64` | **`win-x64`** — same OS as the file tools, *not* the shell |
| shell | same machine as the hook | separate OS; may be absent entirely (B6) |
| `$HOME` | `/root` (cwd `/home/claude`) | Windows profile for hooks; VM has its own |
| session root | `~/.claude/projects/<slug>/` | `%APPDATA%\Claude\local-agent-mode-sessions\<install>\<mid>\local_<uuid>\` (D7) |
| lifetime | reclaimed on idle; `--session-mode resume` re-runs `claude --resume` | VM per app-run; session tree per `local_<uuid>`, cleared between sessions |
| plugin source | `~/.claude/plugins/synced/<name>/` (account sync) | `…\<install>\<mid>\rpm\plugin_<id>\` — **level 2**, above the session root (E6) |
| marketplaces | disabled (`SKIP_PLUGIN_MARKETPLACE=true`) | ? |

---

## A. Distribution & install

- **A0 [X] claude.ai-hosted plugins may not ship a top-level `bin/`.** *(Closed by the `libexec/`
  layout — the hosted `.plugin` ships no `bin/`, the validator accepted it, and every account install
  since 0.1.20 rides that shape. Re-flagged 2026-08-17; kept as originally written below.)* Measured
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
- **A1 [X] Establish the install path.** `/plugin install claudinine` is not it: cloud sessions run
  with `SKIP_PLUGIN_MARKETPLACE=true`, and plugins arrive pre-synced from the account into
  `~/.claude/plugins/synced/<name>/`. *(Closed: the supported route is the `.plugin` account-import,
  documented in the README and exercised by every release since 0.1.20 — including into already-running
  sessions, A5.)*
- **A2 [X] Executable bit AND binary integrity survive the plugin sync.** Measured 2026-08-15 with
  `claudinine-probe` v0.2 installed into the account and synced into a live cloud session: the three
  files packed `755` materialised `-rwxr-xr-x` at
  `~/.claude/plugins/synced/claudinine-probe/libexec/`, data files stayed `644`, and direct
  execution without a shell interpreter worked. A 4 KiB blob of NULs, `0x1a`, lone CR/LF, `0xff
  0xfe` and invalid UTF-8 round-tripped byte-identical (sha256 match, 4096 bytes). **A native binary
  can ship in a hosted plugin** — the 1980-mtime/`644` pattern seen on `cowork-plugin-management` was
  simply a markdown-only plugin, not evidence of stripping. Packaging constraint is A0, not this.
- **A3 [X] Sync limits.** The syncer has a `synced_file_limit_exceeded` path with constants around
  4096 files / 25 MB per file / 64 MB total. *(Closed by O10's measurement: six RIDs are 20.34 MB
  unpacked, largest single file 3.72 MB — inside every cap. The Linux-only idea suggested here was
  tried and actively broke local mode; reverted, see B1.)*
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

- **B1 [X] Only Linux binaries ever *execute* — but not only Linux binaries get *emitted*.** Cloud
  is `linux-x64`; local mode's shell is a Linux VM, so `linux-arm64` on Apple silicon hosts. Verify
  the `bin/claudinine` shim's arch dispatch works on both. **Rev 4 correction:** the claim "the
  `win-x64` publish is irrelevant to Cowork in both modes" is false for local mode on Windows. The
  shell is Linux, but the *hook that writes the launcher* is `win-x64`, and it bakes its own platform
  into the command the shell is told to run. See B5. **Rev 5: closed** — the launcher now targets the
  `libexec/claudinine` routing shim (RID from `uname` at run time) whenever the running binary sits in
  the plugin layout; the concrete-binary target survives only as the dev-tree fallback.
- **B2 [?] libc compatibility.** Confirm the Native AOT binary runs on the cloud image (glibc
  version, `libstdc++`/`libz` presence) and on the desktop's Linux VM image, which may be a
  different distro.
- **B3 [?] Cold-start cost on this hardware.** 18 ms median was measured on a dev box. Re-measure in
  the Firecracker VM; the hook budget is shared with Cowork's own Python hooks
  (`user-prompt-submit-reply-reminder.py`, `stop-hook-git-check.sh`).
- **B5 [X] ROOT CAUSE — local Cowork on Windows is the only host where generator OS ≠ shell OS.**
  *(Rev 5: fixed for every coherent host by the shim-targeting launcher — see B1; local Cowork no
  longer depends on the launcher at all — see E6.)*
  The launcher generator emits a binary path for *its own* platform. That is correct everywhere the
  shell shares the hook's OS, which is every host except local Cowork:

  | host | hook OS | shell | coherent? |
  |---|---|---|---|
  | Claude Code, native Linux | linux | same machine | yes |
  | Claude Code, Windows (Git Bash) | win32 | MSYS — translates `C:/…`, execs PE | yes |
  | Claude Code, inside WSL | linux | same WSL instance | yes (this is the Linux case, not a hybrid) |
  | Cowork cloud | linux | same VM | yes |
  | **Cowork local, Windows** | **win32** | **Linux microVM** | **no** |

  Read directly from the copied mirror, `<sid>/claudinine/run.sh` line 7 is
  `exec "C:/…/rpm/plugin_…/libexec/win-x64/claudinine.exe" "$@"` — a POSIX script exec'ing a Windows
  PE. `run.cmd` points at the same binary, which is correct for cmd. So the generator substitutes the
  host binary into both twins and only the Windows one is coherent. From the Linux VM this fails
  twice over: the `C:/` path does not exist in that VFS, and Linux cannot run a win-x64 PE without
  binfmt/wine. **Not a path-form bug — broken by construction, and reproducible with the VM healthy.**
  Note that line 5 already computes a runtime-correct `CLAUDININE_DIR` from `$0` and line 7 ignores
  it: resolving the binary relative to that anchor plus `uname -s`/`-m` selection fixes the coherent
  hosts and removes the baked host path. It does **not** fix local Cowork — see E6.
- **B6 [L] The local-mode shell is usually absent.** Local Cowork's Linux VM is gated by a
  `yukonSilver` check; when it reads `unsupported`, `startVM` short-circuits before touching the
  hypervisor and `mcp__workspace__bash` returns *"failed to start (not supported on this device)"*.
  From `%APPDATA%\Claude\logs\cowork_vm_node.log` on this machine: **4 successful boots between
  2026-04-16 and 2026-08-17** (Apr 16, Apr 17, Jun 21, Aug 15), against hundreds of
  `[startVM] VM not supported (win32/x64), skipping`. Virtualization, Hyper-V, VMP and WHP are all
  enabled — it is a gate, not a capability problem, and WSL state is irrelevant since the app boots
  its own microVM (`smol-bin.vhdx`, plan9 shares, own `vmlinuz`/`initrd`). Each `unsupported`
  evaluation also runs `cleanupVMBundleIfUnsupported` → `deleteVMBundle`, so the ~2 GB bundle is
  deleted and re-downloaded on the next flip (115 s on Apr 16, 45 s on Aug 15). Widely reported and
  unresolved upstream (anthropics/claude-code #41066 — same lifecycle bug, closed as `invalid`/
  `stale`; plus #25136, #27330, #27456, #28238, #32004, #37016, #47327, #75321, #79832). **Design
  consequence: in local mode, any retrieval mechanism that needs a shell is unavailable most of the
  time.** Treat the shell as an optimisation, never as the retrieval path.
- **B7 [X] Detection: discriminate on the shell tool's identity, not on filesystem probes.**
  *(Rev 5: the WRITE path detects local mode structurally instead — a `local_<uuid>` ancestor with
  `outputs/` and `.claude/` children, `LocalCowork.RefsDirFor` — because its question is "where can
  the model's file tools read?", which only the layout answers, and because the alternate retrieval
  surface needs `outputs/` to exist anyway; no layout, no fix, launcher fallback. The tool-name
  discriminator below remains the right way to classify a HOST from transcript evidence.)* The
  discriminator is already in the records Claudinine mirrors — the digest for this session literally
  reads `mcp__workspace__bash(echo alive; uname -a)`:

  | shell tool name | hook platform | host |
  |---|---|---|
  | `Bash` | linux | native Linux / WSL |
  | `Bash` | win32 | Windows + Git Bash |
  | `mcp__workspace__bash` | win32 | **Cowork local** |

  An MCP-namespaced shell tool means the shell is a foreign execution context, full stop. No `uname`
  round-trip (which needs the very shell that may be down), no path heuristics. Path-shape checks
  (`local-agent-mode-sessions`, `local_<uuid>`, a `/sessions/*/mnt` mount table in the system prompt)
  are useful corroboration but they are inference where the tool name is fact. Cloud Cowork exposes a
  plain `Bash` on the same machine, so it correctly classifies as the Linux case.
- **B8 [X] Is `libexec` even visible from the local VM? — YES, desk-closed 2026-08-17 from the
  2026-08-15 boot-day evidence, per Rev 6's caveat.** The level-2/level-3 traversal question was the
  wrong frame: the plugin tree does not reach the VM through the session share at all, it has its
  **own mount** — the host table's VM view `<mnt>/.remote-plugins/plugin_<id>/` is a VM-side path
  that can only have been observed from inside the VM, and it entered the doc with the same run's
  evidence. The clincher is O12's opening observation: on 2026-08-15 (a boot day, VM up 20:30–20:58)
  *"the shim runs from the sandbox printing `0.1.21`"* — a plugin binary **executed inside the VM**,
  which subsumes reachability. So all six RIDs are visible from the VM when it is up, and the
  launcher route is alive in local mode on exactly those days. Original concern, kept for the
  record: `rpm\plugin_<id>\libexec\` sits at **level 2** of the session path while the plan9
  session share appears rooted at **level 3** (`local_<uuid>`); the Apr 16 log proves `outputs/..`
  traverses up one level, not two — true, and irrelevant given the dedicated mount. Reconfirm
  opportunistically next VM window with `ls -d /sessions/*/mnt/*` (already first in O18's list).

## C. Hook events & session lifecycle

- **C1 [V] Hooks work and the payload is the expected shape.** A probe plugin shipping its own
  executable fired `SessionStart`, `UserPromptSubmit` and `SessionEnd`, each with `session_id`,
  `transcript_path`, `hook_event_name`.
- **C2 [X] `UserPromptSubmit` is the wrong steady-state trigger for Cowork.** Cowork runs long
  autonomous stretches — scheduled tasks, `/loop`, Workflow runs, Monitors — where dozens of turns
  pass with no user prompt at all. The per-turn boundary that always fires is `Stop`. *(Closed: the
  `Stop` trigger shipped with a min-interval guard and is validated on a real cloud host — O2.)*
- **C3 [X] Subagent sweep timing — with a better mechanism than sweeping.** *(Closed: per-agent
  compaction on `SubagentStop` via `agent_transcript_path` shipped and is validated on cloud — O3.)*
  `CompactSubagents` runs
  only at `SessionStart`/`SessionEnd`, while a single Cowork `Workflow` spawns dozens of agents
  inside one session, so `subagents/` grows unbounded between boundaries — and subagent transcripts
  are the best-compacting file type (82%). Measured: `SubagentStop` fires per agent and its payload
  carries **`agent_transcript_path`** (plus `agent_id`, `agent_type`, `stop_hook_active`,
  `background_tasks`, `session_crons`). So compact *that one file* the moment it completes — no
  directory enumeration, no waiting for a boundary, cost proportional to one agent's output.
- **C4 [X] `SessionEnd` fires on idle teardown.** Observed 12:42:32 after the session went idle;
  the process then exited and the container survived. So "clean at rest" is achievable in cloud.
- **C7 [X] `SessionStart` does not fire when an idle Cowork session is woken.** *(Closed: the `.end`
  end-marker turns the first prompt after a `SessionEnd` into the start boundary, so the repair pass
  runs on wake-from-idle after all — O1, validated on cloud.)* Cowork sessions live
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
- **D7 [L] Local-mode layout: no stable anchor anywhere.** The leaf is Claude Code's own convention
  (`.claude/projects/<mangled-cwd>/<sid>/`, same dash-mangling), relocated under a three-level
  Cowork-specific root. Scope of each level, pinned by comparing the Apr 16 log against this session:

  | segment | 2026-04-16 | 2026-08-17 | scope |
  |---|---|---|---|
  | 1 | `b60e81e4-…` | `b60e81e4-…` | stable ≥ 4 months — install/user |
  | 2 | `d042ebc1-…` | `f377406b-…` | varies |
  | 3 | `local_d778c0c3-…` | `local_933e2b31-…` | per session |

  Under level 3: `outputs/` (the agent's cwd), `uploads/` (read-only), and `.claude/projects/…`.
  Contrast with the CLI, where `~/.claude/projects/<slug>/` is keyed by **project** and accumulates
  sessions in one durable directory that tooling can point at forever. Cowork keys by **session
  first**, so the mangled dir encodes an outputs path that is already session-unique — the mangling
  earns nothing there — and **no path is both stable and derivable**. Level 1 is cacheable, level 3
  never is. This is the structural reason baking any absolute path into a digest header fails in local
  mode. Also: the skills tree inverts the segment order (`skills-plugin\<level2>\<level1>\skills`), so
  don't assume consistent ordering across Cowork's trees.
- **D8 [?] The session id changes mid-conversation, and headers bake it.** Digest headers emitted
  early in this session point at `79e249a1-…`; headers emitted later in the *same* conversation point
  at `3090adb5-…`. The copied mirror tree contains transcripts `79e249a1` and `0c4e203c` plus one
  colocated mirror (`79e249a1/claudinine/79e249a1.jsonl`) — and **no `3090adb5` anywhere**, although
  live headers were already citing it when the copy was taken. Ref `11699c8f` resolves 3× in
  `79e249a1.jsonl`, 3× in the colocated mirror, 3× in `0c4e203c.jsonl`, plus `.seen`/`.load` — so the
  same ref lives in two transcripts, which is either an 8-hex-char collision or a resume carrying the
  parent's stubs (the case `ForkHealRule` exists for). Open questions: does a local resume mint a new
  `sid` (and a new `local_<uuid>`, hence a *second* mangled project dir the copy never saw)? Should
  `MirrorLost` have tripped for a transcript citing `3090adb5` with no mirror on that path? Any
  `<sid>`-embedded launcher path is stale the moment this happens — another argument for
  `CLAUDININE_DIR`-relative resolution over baked paths.

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
  **Rev 4:** (a) is confirmed correct for cloud and the CLI, and **inoperable in local mode on
  Windows** — the launcher it writes cannot run there (B5), the binary it names may not even be
  visible (B8), and the shell it needs is usually gone (B6). Keep (a) as the shell path; add E6 as the
  local-mode path. **Rev 5: done exactly so** — (a) hardened by the shim-targeting launcher (B1),
  local mode routed through the refs dump (E6).
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
- **E4 [X] `MirrorFile.CollectGarbageColocated` has no caller.** *(Closed: wired — `HookRunner`
  calls it on the session-start path (HookRunner.cs:122), structural GC per O7's regression guard.)*
  At the time of writing `HookRunner` called only
  `MirrorFile.CollectGarbage()` (legacy pools) and `SessionDirGc.Run()`. A dead session's whole
  sidecar dir is reaped by `SessionDirGc`, so nothing leaks there — but files orphaned *inside a
  living session* (a deleted subagent transcript's mirror, its `.skip`/`.load`/`.seen`) are never
  swept, which is exactly the case the method was written for, and exactly the case Cowork's
  Workflow runs generate most. Wire it at `SessionStart`:
  `MirrorFile.CollectGarbageColocated(MirrorLocator.ClaudinineDirFor(transcriptPath))`.
  (Checked across all non-`Rules/` sources.)
- **E6 [X] Local mode needs a shell-free retrieval path, and the mirror is in the wrong place for
  it.** *(Rev 5: built as a refinement of option (a) — the colocated mirror STAYS canonical for
  durability/restore, and a retrieval PROJECTION of it is dumped to `outputs/.claudinine/refs/`:
  one `<ref>.txt` per mirrored record (tool_result text, or the serialized tool_use input anchor
  stubs address) plus `<ref>-media-N.*` for base64 media, length-stamped per mirror so no-op passes
  skip, regenerated wholesale if deleted. Local digest headers teach `Read`/`Grep` against that dir
  — same sentinels as the command block, so the self-heal regenerates mode-appropriately when a
  transcript moves in or out of the layout. A `mirror key: <sid>` breadcrumb keeps ForkHealRule and
  MirrorLost working; ref files themselves are sid-free, so a fork's refs stay valid unhealed.)* In local Cowork the tools that *always* work are the host-side file tools — `Read` (with
  `offset`/`limit`), `Glob`, `Grep`. They are Windows-side, they do not depend on the VM, and they
  cover the whole verb surface: `Read`+offset ≈ `--full`, `Grep` ≈ `--grep`, a stat ≈ `--info`. But
  they are gated by a **connected-folder allowlist**, and the colocated mirror is not on it. Measured:
  `Glob` on `…\local_<uuid>\.claude\projects` → *"outside this session's connected folders"*, while
  `outputs/` — a **sibling under the same parent** — is the agent's cwd and freely readable. Not an
  ACL: the process owns both paths; the tool layer declines the app-internal one. So E2's colocation
  decision, which is right for durability, puts the mirror precisely where local-mode retrieval cannot
  reach it. Options, best first: **(a)** in local mode write the mirror under `outputs/.claudinine/`
  and emit `Read`/`Grep` instructions instead of a launcher — costs nothing in durability, since
  `outputs/` and `.claude/projects/` share the same `local_<uuid>` parent and therefore the same
  lifetime (D7); **(b)** keep colocation and additionally emit a copy or hardlink under `outputs/`;
  **(c)** ask the user to mount the session root — rejected: it is application-internal, the tool
  guidance forbids requesting it, and a mount of the live session tree was observed to be dropped
  mid-session anyway. Note this makes local mode the *inverse* of A0/E5: there the problem was a shell
  that could not find the binary; here the problem is that the shell is the wrong tool entirely.
- **E7 [X] Retrieval fails silently, which is worse than failing.** *(Rev 5: resolved by removing
  the broken promise rather than warning about it — local headers no longer name a shell command at
  all, and the write path treats the refs dump as part of the mirror-first contract: dump fails →
  compaction skipped, exactly like a failed mirror append. MirrorLost gains the local form: it
  tolerates a lost mirror while the dump still serves retrieval, and trips when both are gone.)* Across a long, tool-heavy local
  session, `claudinine get` executed **zero times** and nothing anywhere reported a problem. Three
  fallbacks masked it: previews that happened to carry the entire payload (a 140 B tool result was
  fully contained in its own preview); the header's own *"if the file discussed still exists on disk,
  read IT instead"*, which correctly routes around retrieval; and re-running a query instead of
  fetching the record when a preview was insufficient (a `Glob` preview showed 2 of 7 paths). The
  header states the contract — *"`[ref]` lines are a REPORT, not observed output — retrieve, don't
  infer"* — and with retrieval unavailable and unsignalled, the only remaining behaviour is inferring
  from previews, i.e. exactly what the contract forbids. Observed in this session: the model asserted
  a verbatim source line it had only ever seen in a *preview*; the quote happened to be accurate, so
  the contract held by luck. **Wanted: a loud tripwire.** The write path can test retrievability at
  emission time (does the launcher target exist and match the shell's platform? is the mirror inside a
  reachable root?) and, when it fails, emit a header that says retrieval is unavailable and previews
  must be treated as untrustworthy — or decline to compact at all, `MirrorLost`-style.
- **E8 [X] The two header forms disagree, and the abbreviated one is unusable.** *(Rev 5: `REF` is
  bound in every form (`[ab12cd34] -> --ref ab12cd34`); the short header is now a sid-free,
  path-free pointer instead of a dead bare-`claudinine` command; and the pointer target is
  structurally guaranteed: header dedup keeps one full RETRIEVAL block per compact-boundary
  SEGMENT, so the app's context slice — which starts at the last boundary — always contains the
  instructions. Old short headers and old bare-command media stubs are healed in place.)* Block 1 of this
  session emitted the full form (`sh "C:/…/run.sh" get <sid> --ref REF …`); every later block emitted
  `claudinine get <sid> --ref REF …` with a pointer to *"full retrieval guidance in the first collapsed
  block of this session"*. There is no `claudinine` on PATH in local mode (E1 is conditional on a
  hosted install; here the binary is a plugin-internal exe), so the abbreviated form names nothing
  executable — and the pointer to an earlier block is exactly what compaction eventually drops.
  `run.sh`'s own header comment says digest headers *"invoke it by absolute path so retrieval needs no
  PATH entry"*, so the bare form contradicts the stated design. Also `--ref REF` is never bound to
  anything: refs appear as `[11699c8f]` and nothing states that the bracketed id is the `REF`
  placeholder. One clause fixes that.
- **E9 [X] Header cost can exceed header benefit.** Block 1's retrieval preamble is ~800 tokens, with
  a ~300-char absolute path repeated four times. On a turn whose mirroring saved ~2 KB, the header is
  net-negative. **Rev 5 corrections:** the parenthetical trigger inference was wrong — `MinCalls` is
  **1** and emission is gated by pure economics (`digestCost × 1.1 < replacedBytes`, priced on the
  REAL built digest with the header-dedup discount applied); single-call turns pass through when
  their digest can't pay, not because of a count floor, so the "threshold" asked for here already
  exists and is exact. The path-repetition cost is fixed structurally: the local block emits the
  refs dir ONCE as `DIR = …`, the short header carries no path at all, and anchor stubs are
  path-free pointers (the launcher-form variant was measured at ~0.5 pt of corpus tokens and
  reverted). Corpus after the whole Rev 5 rework: 68.8% tokens / 78.0% bytes vs 69.1%/77.5% before
  — the residual −0.3 pt is the REF binding and the per-segment full headers, i.e. the price of
  retrieval instructions that actually work.

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
- **G5 [L] Falsifiable predictions for the next local-mode window** (the VM gate flips rarely — B6 —
  so this list should be run in one sitting when it does). **Rev 5 note:** predictions 2–4 targeted
  the pre-Rev-5 header, which no longer exists in local mode (headers there teach file tools, and
  `run.sh` now execs the RID-dispatching shim, so 3's expected line-7 failure becomes "works iff the
  plugin path is reachable from the VM", i.e. it collapses into B8/5). Predictions 1, 5 and 6 stand
  unchanged and are still worth running. Original list, kept for the record:
  1. `Read`/`Glob`/`Grep` still report `C:\…` paths with the VM up. The VM does not relocate the agent;
     it adds a second execution context. (My system prompt carries a live host→VM translation table
     *while the VM is down* — a mapping only exists because both namespaces coexist.)
  2. `sh "C:/…/run.sh"` → `No such file or directory` from the VM.
  3. `sh /sessions/<name>/mnt/outputs/../.claude/projects/<mangled>/<sid>/claudinine/run.sh` locates
     the script and then **still fails at line 7's `exec`** (B5). *This corrects an earlier read of
     mine that predicted 3 would succeed.*
  4. Therefore the header as written fails in the healthy case too, for a different reason than in the
     degraded one.
  5. `ls /sessions/*/mnt/outputs/../../` settles B8 (is `rpm/libexec` reachable at all?).
  6. `ls -d /sessions/*/mnt/*` enumerates the real mount set; compare against the four this session
     was told about (`Claudinine`, `outputs`, `uploads`, read-only `skills`).
  Everything else in Rev 4 was measured with the shell down and does not need it: B5 (read from the
  copied mirror), B6 (from the app's own VM log), B7, D7, D8, E6, E7, E8.

---

## Suggested order

1. ~~**E2, A2, E1**~~ — all closed. The pipeline preserves exec bits and binary content, so the
   remaining problems are packaging shape and retrieval, not feasibility.
2. **A0 + E5** — repack executables outside `bin/`, and switch digest headers to a launcher next to
   the colocated mirror. These two together are what makes a hosted install work at all.
2b. **E7 + E8** — ~~cheap, and they matter regardless of which host you fix first~~ **DONE (Rev 5)**:
   REF bound everywhere, short headers are pointers with a per-boundary-segment full block to point
   at, retrieval failure fails the pass instead of silently degrading.
2c. **B7 + E6** — ~~detect the host, give local Cowork a shell-free retrieval path~~ **DONE (Rev 5)**:
   structural layout detection + the `outputs/.claudinine/refs/` dump + `Read`/`Grep` headers. Local
   mode no longer depends on the VM at all.
3. **E4** — one-line wiring, no reason to carry it.
3b. **B5** — ~~resolve the binary at run time instead of baking the generator's platform~~ **DONE
   (Rev 5)**: `run.sh`/`run.cmd` target the `libexec/` routing shims when the plugin layout is
   detected, falling back to the concrete binary in dev trees.
3. **D5 + G1** — run the compactor over real Cowork transcripts and see what breaks. (This session's
   own transcript is already a valid specimen.)
4. **C2/C3** — the trigger model, which is where Cowork differs most from the CLI.
5. **A1/A4/A5** — packaging and install, once the above says it's worth shipping.
6. **F1/F2** — product-level calls.
