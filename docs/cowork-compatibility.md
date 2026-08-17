# Claudinine × Cowork — compatibility checklist

Status legend: **[V]** verified live in a Cowork cloud session (2026-08-15, Claude Code 2.1.233,
`entrypoint: remote_cowork`) · **[L]** verified live in a Cowork **local** session on Windows
(2026-08-17, desktop app 1.30096.5.0, `local_<uuid>` layout, VM shell **down** throughout) ·
**[?]** unknown, needs a test · **[!]** known gap, needs work · **[X]** closed.

Rev 4 (2026-08-17): first observations from **local mode on Windows**, which turns out to be
structurally unlike both cloud Cowork and the CLI, and is the only host where Claudinine's
generator and its retrieval shell run on **different operating systems**. Consequences: B1's "only
Linux binaries ever execute" is true of the *shell* and false of the *generator* (B5); E5(a) — the
launcher, the fix Rev 3 recommended — cannot work there at all (E6); the colocated mirror from E2 is
unreachable by the only tools that always exist (E7); and the VM that owns the sole shell booted 4
times in 5.5 months on this machine (B6). Retrieval never once executed during a long, tool-heavy
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

- **B1 [V/!] Only Linux binaries ever *execute* — but not only Linux binaries get *emitted*.** Cloud
  is `linux-x64`; local mode's shell is a Linux VM, so `linux-arm64` on Apple silicon hosts. Verify
  the `bin/claudinine` shim's arch dispatch works on both. **Rev 4 correction:** the claim "the
  `win-x64` publish is irrelevant to Cowork in both modes" is false for local mode on Windows. The
  shell is Linux, but the *hook that writes the launcher* is `win-x64`, and it bakes its own platform
  into the command the shell is told to run. See B5.
- **B2 [?] libc compatibility.** Confirm the Native AOT binary runs on the cloud image (glibc
  version, `libstdc++`/`libz` presence) and on the desktop's Linux VM image, which may be a
  different distro.
- **B3 [?] Cold-start cost on this hardware.** 18 ms median was measured on a dev box. Re-measure in
  the Firecracker VM; the hook budget is shared with Cowork's own Python hooks
  (`user-prompt-submit-reply-reminder.py`, `stop-hook-git-check.sh`).
- **B5 [L] ROOT CAUSE — local Cowork on Windows is the only host where generator OS ≠ shell OS.**
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
- **B7 [L] Detection: discriminate on the shell tool's identity, not on filesystem probes.** The
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
- **B8 [?] Is `libexec` even visible from the local VM?** `rpm\plugin_<id>\libexec\` sits at **level
  2** of the session path while the VM's plan9 share appears rooted at **level 3** (`local_<uuid>`).
  The Apr 16 log proves `outputs/..` traverses up one level — a VM-side `python3` successfully read
  `/sessions/<name>/mnt/outputs/../.claude/projects/<mangled>/<sid>/tool-results/…txt`. It does
  **not** prove `../../rpm/…` reaches level 2. If the share is rooted at level 3 then all six shipped
  binaries are unreachable from inside the VM and the launcher route is dead in local mode regardless
  of which platform it targets. One command settles it next time the gate flips:
  `ls /sessions/*/mnt/outputs/../../`.

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
  local-mode path.
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
- **E6 [!] Local mode needs a shell-free retrieval path, and the mirror is in the wrong place for
  it.** In local Cowork the tools that *always* work are the host-side file tools — `Read` (with
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
- **E7 [!] Retrieval fails silently, which is worse than failing.** Across a long, tool-heavy local
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
- **E8 [!] The two header forms disagree, and the abbreviated one is unusable.** Block 1 of this
  session emitted the full form (`sh "C:/…/run.sh" get <sid> --ref REF …`); every later block emitted
  `claudinine get <sid> --ref REF …` with a pointer to *"full retrieval guidance in the first collapsed
  block of this session"*. There is no `claudinine` on PATH in local mode (E1 is conditional on a
  hosted install; here the binary is a plugin-internal exe), so the abbreviated form names nothing
  executable — and the pointer to an earlier block is exactly what compaction eventually drops.
  `run.sh`'s own header comment says digest headers *"invoke it by absolute path so retrieval needs no
  PATH entry"*, so the bare form contradicts the stated design. Also `--ref REF` is never bound to
  anything: refs appear as `[11699c8f]` and nothing states that the bracketed id is the `REF`
  placeholder. One clause fixes that.
- **E9 [?] Header cost can exceed header benefit.** Block 1's retrieval preamble is ~800 tokens, with
  a ~300-char absolute path repeated four times. On a turn whose mirroring saved ~2 KB, the header is
  net-negative. Consider emitting full instructions only when a turn's measured saving clears a
  threshold, and a one-line pointer otherwise — noting E8's warning that the pointer target must
  survive compaction. (Digest emission appears to trigger at ≥ 2 tool calls per turn; single-call turns
  passed through unmirrored. Inferred from 4-, 6- and 2-call turns vs. several single-call turns, not
  confirmed against the source.)

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
  so this list should be run in one sitting when it does). Predictions 1–4 were derived with the shell
  **down** and each one, if it holds, shows the defect is independent of the gate:
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
2b. **E7 + E8** — cheap, and they matter regardless of which host you fix first: bind `REF` in the
   header, drop the unusable bare-`claudinine` form, and make retrieval failure loud. Right now a
   broken read path is indistinguishable from a working one, which is the condition under which the
   whole scheme quietly degrades into inference-from-previews.
2c. **B7 + E6** — detect the host from the shell tool's name, and give local Cowork a shell-free
   retrieval path (mirror under `outputs/`, `Read`/`Grep` instructions). This is the only change that
   makes local mode work at all, and it removes the dependency on a VM that boots ~7% of the time.
   Cheap to build, and it does not wait on B8.
3. **E4** — one-line wiring, no reason to carry it.
3b. **B5** — resolve the binary from `CLAUDININE_DIR` + `uname -s`/`-m` instead of baking the
   generator's own platform. Fixes the CLI-on-Windows and native-Linux launchers properly; does not
   help local Cowork.
3. **D5 + G1** — run the compactor over real Cowork transcripts and see what breaks. (This session's
   own transcript is already a valid specimen.)
4. **C2/C3** — the trigger model, which is where Cowork differs most from the CLI.
5. **A1/A4/A5** — packaging and install, once the above says it's worth shipping.
6. **F1/F2** — product-level calls.
