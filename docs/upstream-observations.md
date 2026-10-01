# Upstream observations

Claudinine parses Claude Code's own session transcripts, so an upstream update can
break it silently. This file is the record of which upstream versions have actually
been looked at, and what was found. It is **append-only, hand-written, and gates
nothing** — no code reads it. Its only job is to answer "what changed since the last
time I checked?" without re-deriving the answer from scratch.

Add an entry when a version bump is noticed. An entry saying "read the changelog, no
territory touched" is worth writing: the value is in the *continuity*, not the detail.

## The review, when asked for one

Two checks. The changelog says what upstream *meant* to change; the bundle diff says
what actually moved in the binary we parse against. Neither subsumes the other — read
both, then write the entry.

1. **Changelog, from the last entry's baseline to current.** The bottom of this file's
   last entry names the previous shell and CLI versions; read the notes for everything
   in between. Online, since the bundle ships no CHANGELOG. Only the CLI matters here.
2. **Diff the CLI bundle** against the previous version, per the recipe below — but only
   when the CLI actually moved *and* both versions are still on disk (see the pruning
   caution). A shell-only bump ends at step 1.

Then append an entry, ending with the new baseline so the next review knows where to
start from.

**A corpus run is not part of this, deliberately.** `bench/corpus/` is 174 frozen JSONL
files and `compare.py` shells out only to `cln` and cozempic, never to `claude` — so a
run is a regression test on *our* parser against 2026-08-12-era inputs, and its verdict
is unchanged by whatever Claude version is installed. It cannot see an upstream format
change: new record shapes are simply absent from a frozen corpus, so everything passes
and nothing is learned. Use it to check our own changes, not upstream's.

## What to observe

Two tracks move independently, and both are read off disk:

| Track | How to read it |
|---|---|
| Desktop shell | `ls %LOCALAPPDATA%\AnthropicClaude` → `app-<ver>` dirs (newest mtime = current) |
| Embedded CLI | `%APPDATA%\Claude\claude-code\<ver>\claude.exe --version`; `.payload` holds a sha256 build identity |

Record both in every entry, but they carry different weight: a shell bump alone has never
moved anything we parse, while a CLI bump is the one to take seriously. Confirm which CLI
is actually *running* rather than trusting the highest version dir — the app can keep
several and run an older one (`Get-Process claude | Select Path`).

The thing that truly matters, the transcript format, has no version number and is not
checked by this review — it is pinned empirically in `session-file-changes.md` and
`parallel-batch-transcript-format.md`. The two checks here are early warning that those
documents may need re-verifying, not a substitute for it.

### Digging into the CLI bundle

The CLI is a single ~300 MB bundled exe with no CHANGELOG inside, but byte-regex over
it does resolve every transcript key we depend on, which beats reading release notes:

```bash
python -c "
import re
d=open('claude.exe','rb').read()
for k in [b'preservedSegment', b'preservedMessages', b'isSidechain', b'toolUseResult', b'SubagentStop']:
    print(k.decode(), len(re.findall(re.escape(k), d)))
"
```

Three cautions, all learned the hard way:

- **`strings` returns nothing useful** on these bundles (and on the shell's `app.asar`).
  Use Python `re.finditer` over the raw bytes.
- **Match counts drift between builds with no format change.** 2.1.227 → 2.1.229 moved
  `toolUseResult` 98 → 100 and `PreCompact` 45 → 48 while the format was identical. A
  count delta is a prompt to go look, never a finding on its own. Presence/absence of a
  key is the signal worth trusting.
- **Old versions are pruned eventually.** Squirrel keeps a couple of `app-*` dirs and the
  CLI keeps a couple of version dirs, so a differential read is only possible for a while
  after the bump — diff while both sides are still on disk. If the previous CLI is already
  gone, the bundle diff is simply unavailable: say so in the entry and let the changelog
  read stand alone, rather than reaching for a substitute. To keep the option open across
  a bump you expect to care about, copy the current `claude.exe` aside beforehand (~300 MB)
  — the `.payload` sha256 identifies which build it was.

## Observations

### 2026-08-18 — shell 1.30096.5 → 1.32352.1, CLI unchanged at 2.1.229

Noticed because the desktop app updated itself. **Shell-only bump; nothing to do.**

- Shell: `app-1.32352.1` (mtime 2026-08-18 08:38), replacing `app-1.30096.5`
  (2026-08-15). `app-1.30096.1` also still on disk. Package
  `AnthropicClaude-1.32352.1-full.nupkg`, 231,072,344 B.
- CLI: **untouched** — `%APPDATA%\Claude\claude-code\` still holds only `2.1.227` and
  `2.1.229`, both mtime 2026-08-14. The 2.1.229 exe is the one actually running
  (confirmed by process path), `--version` → `2.1.229 (Claude Code)`,
  sha256 `5736c66be98a372d5e5e3b3598ead89ab5a9d1aca60d347fe7b561801c58376c`,
  307,186,848 B. (2.1.227 predates the `.payload` metadata file, so no sha for it.)
- Format: not re-verified, and deliberately so — the CLI did not move, and the format
  travels with the CLI, not the shell.
- Differential read over the two retained CLI bundles found no key appearing or
  disappearing across 2.1.227 → 2.1.229: `preservedSegment` 16/16, `isSidechain` 81/81,
  `sidechain` 8/8, `compactMetadata` 54/54, with the count drift noted above on
  `toolUseResult` and `PreCompact`. `mirrorOf` absent from both, as expected — that key
  is ours, not upstream's.

**Baseline going forward: shell 1.32352.1, CLI 2.1.229.**

### 2026-08-18 (later) — changelog read 2.1.229 → 2.1.234; no local bump

Review on request. **Nothing moved on this machine since the entry above**, so this was a
changelog read only; the bundle diff did not apply.

- Shell: still `app-1.32352.1`; Squirrel checked for updates at 08:39 and stayed. Nothing
  staged in `packages/` beyond the 1.32352.1 nupkg.
- CLI: still `2.1.229` running, and **bit-identical** to the recorded baseline —
  sha256 `5736c66b…c58376c`, 307,186,848 B, matching `.payload`. Only `2.1.227` and
  `2.1.229` on disk, so the diff had no new side to compare against.
- **But upstream is ahead of this install**: 2.1.234 is current, and the desktop CLI is
  pinned five releases back (230 does not exist publicly; 231/232/233/234 do). So the
  changelog range was read even though nothing changed locally. Worth remembering: the
  desktop app's CLI can sit well behind the published version, and the version *we* parse
  against is the one on disk, not the newest released.

Two items in that range touch our territory. Both were checked against the code and are
**non-breaking**; neither needs work.

- **2.1.234: `CLAUDE_CODE_PROJECT_DIR_NAME`** — "hosts that give each session its own
  config directory can choose a short name for the per-project transcript directory". This
  is the closest thing to a real hazard in the range, because it makes the per-project
  directory name host-chosen rather than derived. We are safe by construction: the
  colocated mirror is derived from the hook's own `transcript_path` by
  `Path.GetDirectoryName` (`MirrorLocator.cs:31`), never by recomputing a project slug, and
  the verb-time fallback *enumerates* `~/.claude/projects` rather than predicting a name
  (`MirrorLocator.cs:126`), so a renamed project dir is still found. Do not "improve" either
  into slug reconstruction — this env var is exactly why that would break.
- **2.1.233 + 2.1.234: NT-namespace (`\??\`) path rejection** in session restore, remote
  file reads, CLAUDE.md includes, workflow scripts and uploads. Our digest headers emit
  launcher paths (`sh "<abs>/run.sh" get <sid>`) that are ordinary Windows paths, never the
  `\??\` device form, so nothing we write trips the new validation.

One item was flagged open and is now **measured and cleared at the context level**: 2.1.232
turned on **subagent forking by default** (`subagent_type: "fork"`) and made non-teammate
spawns background by default. First logged here as a mix-not-format change, then reopened
on the worry that a forked subagent would inherit stubs it could not resolve. Measured
2026-08-18 on a standalone CLI at 2.1.232+ (untestable on our pinned 2.1.229, which rejects
`subagent_type: "fork"` as an unknown agent type), then mechanism-corrected the same day by
a canary-divergence experiment: **the fork inherits the parent's LIVE in-memory context, not
the disk transcript** (the first run saw "the compacted view" only because the parent had
just been resumed, making memory equal disk). So a fork carries digests exactly when the
parent was loaded from a compacted transcript — and **retrieval works from inside it**: it
ran the header's command unmodified against the *parent* session's launcher and sid, and got
the full record back. Retrieval is session-addressed, not identity-scoped. The residual cost
is that inherited prose arrives as assertions whose evidence sits behind a `get`. Full
measurement, the settling experiment, plus an incidental permission-matcher finding
(appending to the retrieval command gets it denied; the bare header form runs), in
`session-file-changes.md`, section "Session forks". The on-disk shape is measured too: a
forked agent's file carries NO parent records, just a `fork-context-ref` pointer — see
`forked-subagents-analysis.md`. Also noted, not affecting us: 2.1.232 fixed fullscreen re-normalizing the whole conversation on every
update, the same family as the 2.1.227 quadratic fix, so timing comparisons across that
boundary stay suspect.

**Baseline going forward: shell 1.32352.1, CLI 2.1.229 (upstream at 2.1.234).**

### 2026-08-21 — shell 1.32885.1 → 1.34493.1, CLI 2.1.234 → 2.1.237

Noticed because the desktop app updated itself. **No compatibility break.**

- Shell: `app-1.34493.1` (mtime 2026-08-21 06:39), replacing `app-1.32885.1`
  (2026-08-19, the Rev 7 baseline). `app-1.32352.1` also still on disk. Package
  `AnthropicClaude-1.34493.1-full.nupkg`, 216,601,179 B (1.32352.1 was 231,072,344 B).
  No online changelog for the shell exists; per the standing observation, a shell bump
  has never moved anything we parse.
- CLI: **moved** — `%APPDATA%\Claude\claude-code\` now holds only `2.1.237`
  (mtime 2026-08-21 11:00); 2.1.234 was pruned by the update. No backup copy of the
  old bundle was kept, so the differential read is unavailable and the changelog read
  stands alone. New bundle: 330,167,456 B (2.1.229 was 307,186,848 B), sha256
  `406167231b3636e55a01d0ce93567256c61e7973489e645883302f14808ae668`, matching
  `.payload`. The standalone CLI on PATH still reports 2.1.235; the embedded one is
  what Cowork runs.
- Presence read over the new bundle (the old side being gone, presence is the
  trustworthy signal): `preservedSegment` 16, `isSidechain` 81, `sidechain` 8,
  `compactMetadata` 54 — identical to the 2.1.229 baseline; `toolUseResult` 100 → 106
  and `PreCompact` 48 → 48, i.e. count drift with no format change in the changelog;
  `SubagentStop` 76; `mirrorOf` absent as expected. `local-agent-mode-sessions` is 0
  in the CLI, as expected — that path is shell-side, pointed at via `CLAUDE_CONFIG_DIR`.
- Changelog 2.1.235 → 2.1.237 (online, from the 2026-08-19 measured baseline of
  2.1.234): nothing touches our territory — no hook event or payload changes, no
  transcript record-shape changes, no project-dir naming, no plugin packaging.
  Closest items, both considered and cleared: 2.1.236's "SIGTERM in print/SDK mode no
  longer records an interrupted turn or synthetic tool denials" removes a record shape
  from transcripts rather than reshaping one, and 2.1.236's fix for housekeeping after
  a session's cwd was deleted concerns Claude Code's own background tasks, not hooks.
- Not re-verified, and deliberately so: the local Cowork session layout
  (`local-agent-mode-sessions`) is shell-side and the app was not running, so the next
  local Cowork run remains the empirical check for the shell bump.

**Baseline going forward: shell 1.34493.1, CLI 2.1.237.**

### 2026-08-21 (later) — local Cowork run on the new shell; the residual is measured and cleared

The entry above left one residual: the local Cowork layout is shell-side, so "the next local Cowork
run remains the empirical check for the shell bump". That run happened (run dir
`local_01fc400e-…`, CC sessionId `82285e0d-…`). **No break.**

- Layout unchanged: `<install>\<mid>\local_<uuid>\` with `.claude\projects\<slug>\`, the slug still
  the mangled `outputs` path (188 chars, same derivation as 2026-08-19), and
  `CLAUDE_CODE_PROJECT_DIR_NAME` still absent from the session `.claude.json` — first-party Cowork
  still does not get the fixed slug. New shell-side litter in the run dir (`audit.jsonl`,
  `.audit-key`, `uploads-tmp`); the April-era `shim-lib`/`shim-perm` dirs are gone.
- **The agent no longer runs as the standalone embedded CLI.** While the session was live there was
  no process from `%APPDATA%\Claude\claude-code\2.1.237\` — every `claude.exe` on the box was an
  Electron shell process (main/GPU/renderer/utility), so the agent runs in-process in the shell.
  The transcript header pins `"version":"2.1.237"`, `"entrypoint":"local-agent"`, so the in-process
  agent is the new CLI; the 2.1.237 dir (staged 11:00) is not executed as a separate process.
  Shell-side hosting change; nothing Claudinine parses is affected, and all hooks fired.
- Claudinine ran end to end: `.lock` at SessionStart, `.pass`/`.end`/`.load`/`.seen` written,
  colocated mirror 23,379 B holding the full records, `run.sh`/`run.cmd` regenerated, refs dump with
  2 files + `.dumped` stamp. The live transcript was left uncompacted — correct economics: the two
  archived outputs are 177 B and 140 B, below the digest pay threshold.
- New upstream record type in the transcript: `{"type":"atis-latch","atis":"","sessionId":…}` —
  bookkeeping, skipped by the pass (absent from `.load`), not mentioned in the 2.1.235–237
  changelog. The parser tolerates it as any unknown type must.

**Baseline going forward: shell 1.34493.1, CLI 2.1.237 (measured live in local Cowork).**

### 2026-09-05 — shell 1.46388.4, CLI 2.1.260: Desktop update detection reads only the entry `version`

Noticed because "Check for updates" on the claudinine marketplace reported success yet the
plugin card kept 1.2.1 as current after v1.2.2 was published. **Our gap, not a Desktop bug;
fixed by stamping `version` into the marketplace entry.**

- The marketplace menu's "Check for updates" calls the app's `refreshMarketplace` bridge,
  which runs `claude plugin marketplace update <name>` and nothing else. The clone under
  `~/.claude/plugins/marketplaces/claudinine` was at the 1.2.2 release commit afterwards,
  so the toast was honest. Installed plugins are not touched by that action; there is a
  separate `updatePlugin` bridge (`claude plugin update`) the UI offers only when it has
  detected a newer version.
- Detection (app.asar, `[CustomPlugins]` listing): for each installed plugin the app
  resolves a directory inside the marketplace clone and reads a manifest there. A string
  source resolves to that path; an object source with no installed-path translation falls
  back to `<clone>/<plugin-name>`, which does not exist for us. The manifest read then
  returns null and the code falls back to the marketplace entry itself:
  `availableVersion = entry.version !== installed.version ? entry.version : undefined`.
  Our entry carried only `source.url` and `source.sha256`, so `availableVersion` was
  never set. The clone's root `.claude-plugin/plugin.json` did say 1.2.2, but nothing
  points at it for an archive entry.
- The CLI path differs: `plugin update` compares the entry `version`, or failing that the
  first 12 hex chars of `source.sha256`, with the installed version, so the pin alone does
  trigger a download there. Only the Desktop depends on the entry `version`.
- The Desktop also merges a *remote*, account-scoped plugin list (`[PluginsFetcher]
  fetchAccountScopedRemotePlugins`, hourly) and logs "exists in both remote and local.
  Using remote." for claudinine — a second source of truth for the card once the
  claude.ai registration is live; not investigated further.
- Startup plugin auto-update (`Plugin autoupdate: checking installed plugins` and its
  "skipped (auto-updater disabled)" sibling) was not observed running under the Desktop:
  `installed_plugins.json` still showed the 1.2.1 install from 2026-08-19 an hour after
  1.2.2 published. Whether the Desktop disables the CLI auto-updater is an open question.

**Baseline going forward: shell 1.46388.4, CLI 2.1.260. Marketplace entries must declare
`version` (stamped by `eng/set-archive-source.ps1`).**

### 2026-10-01 — shell 1.46388.4 → 2.16120.0, CLI 2.1.260 → 2.1.284

Asked for directly ("what changed since last time"). **No compatibility break.** Two
upstream mechanisms now overlap with ours (an on-disk transcript GC and transcript
relocation) and one budget is tighter than our manifest says (SessionEnd gets 1.5 s, not 30 s).
None of them needs a code change today.

- Shell: `app-2.16120.0` (mtime 2026-09-29 18:27); `app-2.9939.2` and `app-2.9939.4` are
  also on disk and 1.46388.4 was pruned. The shell's major version went from 1 to 2.
  Presence read over `app.asar` is stable between 2.9939.4 and 2.16120.0
  (`availableVersion` 9, `refreshMarketplace` 7, `updatePlugin` 10,
  `local-agent-mode-sessions` 5, `fetchAccountScopedRemotePlugins` 7). Update detection
  still sets `availableVersion` only when the marketplace entry's `version` differs from
  the installed one, so the 2026-09-05 fix (#27) still holds.
- CLI: `2.1.284` is the one running (confirmed by process path) and `2.1.281` is also on
  disk. 2.1.260 was pruned, so there is no diff against the baseline, but 281 ↔ 284 is
  diffable. New bundle: 246,481,056 B, sha256 `3b82b00ee9986fa2c857674dd626cd8f05716d12b83f3003ff0346d558a07408`
  (matches `.payload`), built 2026-09-27, git `16cbb4dd`. The standalone `claude` on PATH
  is a stale 2.1.252. Upstream is at **2.1.286**. 2.1.262, 2.1.264 and 2.1.279 were never
  published.
- Presence read (281 → 284; Rev 9 / 2.1.260 in brackets): `preservedSegment` 17 → 17
  [16], `microcompact_boundary` 4 → 4 [2], `compactMetadata` 66 → 66 [59],
  `toolUseResult` 154 → 158 [138], `isSidechain` 76 → 76 [63], `persisted-output` 7 → 8
  [7], `Output too large (` and `). Full output saved to: ` 4 → 4,
  `[Old tool result content cleared]` 4 → 4, `CLAUDE_CODE_PROJECT_DIR_NAME` 5 → 5,
  `mirrorOf` 0. Every key we parse is still present. The `preservedSegment` and
  `microcompact_boundary` deltas both led to the findings below. **Add
  `preservedMessages` to the key list from now on.** We depend on it
  (`TranscriptFile.MarkPreserved`), yet it was never tracked here.
- **Upstream now has its own on-disk transcript GC (`performCompactTranscript`), and it
  composes with ours.**
  - When it fires: once the file is ≥ 5 MiB, after every 20 MiB appended. The interval
    doubles, up to 160 MiB, when a run saves less than 10%.
  - What it does: rewrites `<sid>.jsonl` through a `.compact.tmp.*` file plus rename. It
    keeps every line from the last compact boundary on, plus:
    - the boundary's preserved records: `preservedMessages.uuids`, or failing that a
      `preservedSegment` tail → head parent walk;
    - their `file-history-*` records;
    - any pre-boundary parent of a post-boundary record;
    - the latest copy of each `last-wins` metadata record.
  - **It is off by default.** It requires `CLAUDE_CODE_TRANSCRIPT_LOCAL_GC` or the flag
    `tengu_transcript_local_gc` (default `false`), and it is only switched on along the
    `sdkUrl` resume path, i.e. managed cloud workers. Local Code and Desktop sessions
    don't run it. It was never observed running.
  - Why it is safe with us:
    - It drops only pre-boundary history, which the API-visible ruler already treats as
      phantom payload. The mirror keeps the originals.
    - It snapshots three 4 KiB windows and checks inode and size before and after the
      write, and aborts with `source_changed` on any difference. So our atomic swap
      (new inode) makes it back off.
    - If it wins a race, our next pass just reads the shorter file.
    - It aborts with `preserved_uuid_missing` when a `preservedMessages.uuids` entry is
      missing, and with `preserved_walk_broken` when the segment walk can't reach
      `headUuid`. Both abort without writing.
  - Residual, hardening only: `MarkPreserved` protects `allUuids`. Every writer in the
    bundle emits `uuids ⊆ allUuids`, but the snake_case wire converter makes
    `all_uuids` optional. A boundary arriving without it leaves `uuids` unprotected. The
    upstream GC would then abort rather than corrupt anything. **Done in the same PR:**
    `MarkPreserved` now protects the union of both lists
    (`BareStopHookSummary_NamedOnlyByUuids_IsKept`).
- **Our SessionEnd hook gets 1.5 s on every path, and our `timeout: 30` does not change
  it.**
  - What 2.1.268 changed: SessionEnd hooks with no `timeout` of their own get 1.5 s
    (`CLAUDE_CODE_SESSIONEND_HOOKS_TIMEOUT_MS` overrides).
  - All three callers pass `signal: AbortSignal.timeout(Bme())`: graceful shutdown,
    `/clear`, and the in-session resume switch. Shutdown also arms its failsafe at
    `max(5000, Bme() + 5000)`.
  - `Bme()` (exported as `getSessionEndHookTimeoutMs`) is
    `max(1500, min(largest declared SessionEnd timeout, 60000))`. The largest timeout is
    taken over **settings-file hooks and main-thread agent hooks only**. The snapshot it
    reads (`initialHooksConfig`) is built from policy and merged settings, so plugin
    hooks are not in it.
  - So with no settings-file SessionEnd hook (this machine has none) and the env var
    unset (the Desktop only registers it), the signal aborts our hook at 1.5 s, whatever
    `hooks.json` declares.
  - **Not biting in practice.** A SessionEnd writes `.end` before the lock and `.load`
    after the pass and `CompactSubagents`. Across all 138 colocated session dirs that
    have both a `.end` and a transcript, none has `.load` older than `.end`. The
    `.end` → `.load` span is 0.03–0.29 s, including sessions with 11–18 subagent
    transcripts, so the worst case has about 5× headroom. A scratch-copy run of the
    largest recent session (5.6 MB), process start included, took 320–515 ms.
  - Degrade-only if it ever does bite: the swap is atomic, the marker is already
    written, and the next start boundary repairs anything left.
  - Worth an upstream issue: `Bme()` should count plugin hooks' declared timeouts. As it
    stands, a plugin's SessionEnd `timeout` is dead configuration.
- **Transcript relocation moves our mirror with it.**
  - When it happens: on a cwd change to another project slug (worktree enter/exit, `cd`).
  - What moves: `relocateSessionTranscript` moves both `<sid>.jsonl` and the whole
    `<sid>/` sidecar dir. The colocated `claudinine/` mirror lives in that dir, so it
    follows. This pays off the 2026-08-15 colocation decision.
  - Failure mode: if the dir move fails (e.g. a file held open on Windows), upstream logs
    it and carries on with the transcript moved and the mirror stranded. `get` still
    resolves the mirror by sid across every project's colocated dirs. The compaction
    tripwire fails closed, which I confirmed by deleting the mirror next to a scratch
    copy: the pass refused to compact and rebuilt nothing from stubbed content.
  - Every `relocated` record found in real transcripts is a *same-target* stamp
    (`relocatedCwd` equals the project's own cwd, re-appended with the metadata). A real
    cross-project move hasn't been seen yet.
- Parallel-batch serialization at 2.1.284 (this session's own transcript): uses and
  results now interleave as `USE1 → RES1 → USE2 (parent = RES1) → RES2`. The 2.1.222
  specimen had all uses before any result. Each result still parents its own use. The
  ChainCollapse pending-set grammar has been order-agnostic since the use-while-draining
  abort was removed (2026-08-12), so this is covered.
- Record types: the transcript writer's routing table lists `api-request-shape`,
  `api-request-blob`, `api-request` and `observer-ref` as `route-by-agent`. This is the
  2.1.267 "system prompt and tool definitions recorded once" feature. None of them appear
  in the 25 most recent transcripts (2.1.258–2.1.284). Types that do appear and are new
  since the 2.1.222 notes: `bridge-session`, `relocated`, `file-history-delta`,
  `cost-state`, `mode`, `artifact-autoreact-ledger`, `artifact-comment-monitor`. They are
  all app metadata, and no rule targets them. Watch: if `api-request-blob` starts
  landing, check that the ruler doesn't count it as API-visible payload.
- `claude plugin validate .` passes under 2.1.284. This matters because 2.1.281 started
  warning on unquoted `${CLAUDE_PLUGIN_ROOT}` (ours is quoted) and 2.1.283 started
  failing invalid marketplace names.
- Changelog 2.1.261–2.1.286, other items considered and cleared:
  - 2.1.268: resume shows the conversation before SessionStart hooks finish. This was
    already assumed in `HookRunner`'s comments, which is why SessionEnd, not
    SessionStart, makes the file clean at rest.
  - 2.1.285: tolerates malformed compaction markers. We never edit boundary records,
    since they are protected.
  - 2.1.284: compacts again when still too long, which can produce adjacent boundaries.
    `MarkPreserved` iterates all of them.
  - 2.1.269: archive extraction strips world-writable bits. Not the exec bit.
  - 2.1.275: claude.ai-enabled plugins sync into terminal sessions. With both an org copy
    and a marketplace copy, two instances fire per event, and `PassLock` turns the second
    into a free skip. This machine has only the managed copy.
  - Many resume/cache replay and malformed-transcript crash fixes: upstream-internal.
  - No hook event was added, removed or repayloaded.
- Not verified, deliberately: no live Cowork run, local or cloud. The upstream GC was
  read, not run. The relocation failure path was reasoned about, not triggered.

**Baseline going forward: shell 2.16120.0, CLI 2.1.284 (upstream at 2.1.286). Track
`preservedMessages` in the presence read.**

### Earlier, reconstructed from scattered notes

These predate this file and were recorded prose-style elsewhere; kept here so the trail
starts before 2026-08-18 rather than at it. Each points at its source rather than
restating it.

- **2.1.222** — parallel-batch transcript format captured empirically (batch
  serialization, chain forks, subagent file layout). The corpus this file's format
  assumptions rest on. See `parallel-batch-transcript-format.md`,
  `session-file-changes.md:108`.
- **2.1.223–227** — changelog surveyed 2026-08-12 (online, not from the bundle). 2.1.227
  fixed a quadratic message-normalization slowdown, so **pre-227 corpus timings are not
  comparable** to later ones. Recorded in memory `live-context-vs-disk-compaction.md`.
- **2.1.224** — the floor for `set-archive-source.ps1`: 2.1.120–2.1.223 refuse the
  archive source it sets. See `eng/set-archive-source.ps1:10`.
- **2.1.229** — first local Cowork run (desktop shell 1.30096.5.0): all six hooks
  register, then every hook dies exit 127. See `cowork.md:40`, `cowork.md:409`.
- **2.1.233** — Cowork cloud verification baseline, `entrypoint: remote_cowork`. See
  `cowork-compatibility.md:3`, `cowork.md:66`.
