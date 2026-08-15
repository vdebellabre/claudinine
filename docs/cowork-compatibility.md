# Claudinine × Cowork — compatibility checklist

Status legend: **[V]** verified live in a Cowork cloud session (2026-08-15, Claude Code 2.1.233,
`entrypoint: remote_cowork`) · **[?]** unknown, needs a test · **[!]** known gap, needs work.

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

- **A1 [!] Establish the install path.** `/plugin install claudinine` is not it: cloud sessions run
  with `SKIP_PLUGIN_MARKETPLACE=true`, and plugins arrive pre-synced from the account into
  `~/.claude/plugins/synced/<name>/`. Decide the supported route — `.plugin` bundle installed into
  the account from the desktop app (then synced), vs. local-mode-only marketplace install.
- **A2 [?] Executable bit through the sync pipeline.** `build.yml` packs with the `zip` CLI
  specifically because it stores Unix permission bits, and verifies `test -x` after extract. The
  account→session sync is a different pipeline. If it drops the mode bits, every hook fails
  silently (fail-closed = invisible). Fallback: `sh -c 'chmod +x "$0" && exec "$0" hook'`.
- **A3 [?] Sync limits.** The syncer has a `synced_file_limit_exceeded` path with constants around
  4096 files / 25 MB per file / 64 MB total. Six RIDs ≈ 19 MB unpacked — probably fine, but confirm.
  Consider a Linux-only bundle for Cowork (see B1): smaller, and no dead mac/win binaries.
- **A4 [?] `CLAUDE_PLUGIN_ROOT` for synced plugins.** Synced plugins sit one level deeper than the
  documented `~/.claude/plugins/<name>/`. Confirm `${CLAUDE_PLUGIN_ROOT}` resolves to
  `~/.claude/plugins/synced/claudinine` and that `bin/` survives the round trip intact.
- **A5 [?] Plugin hooks are registered for *synced* plugins under the cowork entrypoint.** Proven
  [V] for a skills-dir plugin in a headless run in the Cowork container (see C4); the synced +
  interactive-cowork combination is the one that actually ships. No env flag disables plugin hooks
  (only `CLAUDE_CODE_SKIP_PLUGIN_MCP_SERVERS=1`, which we don't care about — we register no MCP).
- **A6 [V] `CLAUDE_PLUGIN_DATA` naming — moot for mirrors** since colocation (2026-08-15): mirrors,
  skip markers and load stamps are written to `<sid>/claudinine/`, derived from `transcript_path`
  alone, so the data-dir name no longer matters on the write path. The
  `~/.claude/plugins/data/claudinine-*` glob survives only as a legacy READ fallback for
  pre-colocation mirrors, which a Cowork container never has.

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
- **C3 [!] Subagent sweep timing.** `CompactSubagents` runs only at `SessionStart`/`SessionEnd`. A
  single Cowork `Workflow` can spawn dozens of agents inside one session, so `subagents/` grows
  unbounded between boundaries — and subagent transcripts are the best-compacting file type (82%).
  Consider `SubagentStop`, or sweeping on the `Stop` trigger from C2.
- **C4 [?] `SessionEnd` reliability.** In cloud the container is reclaimed on idle; a clean
  `SessionEnd` may never fire. That shifts weight onto the `SessionStart` repair pass — which only
  pays off if the mirror and transcript both survive (see E3).
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

- **E1 [!] `claudinine` is not on `PATH`.** Digest headers instruct bare `claudinine get <sid> --ref …`,
  run from the Bash tool, which inherits neither `CLAUDE_PLUGIN_ROOT` nor `CLAUDE_PLUGIN_DATA`. If
  the binary isn't resolvable, every retrieval instruction in every digest is dead on arrival.
  Options: absolute path in the header, a `~/.local/bin/claudinine` symlink dropped at
  `SessionStart`, or a `PATH` addition. (Worth checking whether this already bites in the CLI.)
  Since colocation, the env-var half of this item is gone: `get` resolves mirrors from the
  transcript layout alone, no `CLAUDE_PLUGIN_DATA` needed. Only binary resolution remains.
- **E2 [V-by-construction, verify live] Mirrors live inside whatever gets snapshotted** — resolved
  2026-08-15 by making `<project>/<sid>/claudinine/` the canonical mirror location. The sidecar dir
  already holds state a resumed session cannot function without (`workflows/journal.jsonl` is
  replayed on Workflow resume, `tool-results/*.txt` are pointed at by transcript stubs), so any
  snapshot/sync that preserves the session preserves the mirror, by placement. Legacy flat-pool
  mirrors are migrated on first touch. Backstop for the residual case (a restore that drops the
  claudinine dir anyway): the missing-mirror tripwire — a transcript whose stubs name its OWN sid
  with no mirror findable anywhere gets NO further passes, not even mirror writes, so the loss stays
  visible instead of compounding. Remaining live checks: Cowork tolerates a foreign `claudinine/`
  subdir in `<sid>/` (UI, sync, snapshot round-trip), and F2's re-upload behavior with the mirror
  present.
- **E3 [V] Mirror GC vs. cloud lifetime — restructured by colocation.** The mirror now dies exactly
  when its session dir dies (`SessionDirGc`, 24 h grace), and the colocated sweep is structural — it
  tests sibling existence, never the absolute paths recorded in headers, precisely because
  cloud restore and cross-device sync relocate the tree and make recorded paths stale. The legacy
  header-based sweep still runs, but only over the old flat pools.

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

1. **E2 (done 2026-08-15 — colocated mirrors + missing-mirror tripwire; one live round-trip check
   left), A2, E1** — the three that decide whether this is safe and useful at all.
2. **D5 + G1** — run the compactor over real Cowork transcripts and see what breaks. (This session's
   own transcript is already a valid specimen.)
3. **C2/C3** — the trigger model, which is where Cowork differs most from the CLI.
4. **A1/A4/A5/A6** — packaging and install, once the above says it's worth shipping.
5. **F1/F2** — product-level calls.
