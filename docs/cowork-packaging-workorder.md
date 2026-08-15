# Work order — packaging for claude.ai-hosted install, and retrieval without PATH

**Status (2026-08-15): implemented on develop — W1–W6 all landed** (W6's Cowork-corpus point aside:
the CLI corpus re-ran clean; a Cowork corpus is G1's job). Deviations from the plan, all deliberate:
the hosted bundle ships all six RIDs (A3's limits allow it; no wrong-platform failure mode); header
commands QUOTE the launcher path (spacey Windows profiles), so the `claudinine get <sid>` literal
could not be preserved and every matcher learned both forms instead — `MirrorLost` matches the
JSON-escaped `\" get <sid>` in raw lines; the self-heal lives in `CarrierHeaderDedupRule` and also
upgrades pre-launcher headers in place. Acceptance tests 1–3 (live account upload + Cowork session)
remain to be run. Kept for reference; the checklist below is historical.

Context: `docs/cowork-compatibility.md` (items A0, E1, E5). Two facts drive everything here, both
measured 2026-08-15 in a live Cowork cloud session:

1. The claude.ai plugin validator **refuses any plugin with a top-level `bin/`**: *"they are added to
   PATH on the CLI but are not shown on the admin approval surface. Declare executable entry points
   via hooks, commands, or mcpServers instead."* Claudinine ships `bin/claudinine` +
   `bin/<rid>/claudinine`, so it cannot be installed into an account today — and account install is
   the only route into cloud Cowork.
2. Claude Code appends `<pluginRoot>/bin` to the **Bash tool's** PATH *unconditionally, even when no
   such directory exists*. A directory under any other name is never added. Everything else about
   the pipeline is fine: exec bits and binary bytes both survive the sync intact.

Consequence: the archive/CLI install can keep its PATH convenience, the hosted install cannot, and
**machine retrieval must stop depending on PATH in both** — a transcript compacted on one install
context gets read in the other (fork, cloud↔local, resume elsewhere), so one mechanism must work
everywhere.

---

## Decision

Canonical layout is `libexec/`. Two artifacts from it:

| | CLI archive (`claudinine-<v>.zip`) | hosted bundle (`claudinine-<v>.plugin`) |
|---|---|---|
| `libexec/claudinine`, `libexec/claudinine.cmd` | routing shims | POSIX shim only (`.cmd` has no win-* binary to route to) |
| `libexec/<rid>/claudinine[.exe]` | 6 RIDs | **linux-x64 + linux-arm64** (20.34 MB → 7.27 MB; `-HostedRids` overrides) |
| `bin/claudinine`, `bin/claudinine.cmd` | 2-line forwarders to `../libexec/claudinine` | **absent** |
| install route | marketplace / `archive` source | `.plugin` uploaded to the account, synced to sessions |

`bin/` survives in the CLI archive purely for the human verbs (`claudinine restore-compaction-off
<sid>`, `claudinine version`). Nothing machine-facing may rely on it.

*Alternative if you prefer one artifact:* drop `bin/` everywhere and document the human verbs via the
launcher (W2). Simpler CI, worse ergonomics for CLI users. Pick one before starting W1.

---

## W1 — Move executables out of `bin/`

**`eng/pack-plugin.ps1`**

- Add `[switch] $Hosted`.
- Line ~67: stage `libexec/` instead of `bin/`.
- Lines ~80-90: copy `eng/shims/claudinine` → `libexec/claudinine`, `eng/shims/claudinine.cmd` →
  `libexec/claudinine.cmd`; per-RID dirs become `libexec/<rid>/`.
- When **not** `-Hosted`: also stage `bin/claudinine` and `bin/claudinine.cmd` (new forwarders, W1b).
- Lines ~112-116 (`chmod +x`): apply to `libexec/claudinine` and every `libexec/<rid>/claudinine`,
  plus `bin/claudinine` when present.
- Line ~116 `$entries`: `@('.claude-plugin', 'hooks', 'libexec', 'README.md')`, plus `'bin'` when not
  `-Hosted`.
- Output name: `claudinine-<version>.zip` as today; `claudinine-<version>.plugin` under `-Hosted`
  (the account upload expects the `.plugin` extension).
- Keep the `zip` CLI path — it is what stores the Unix mode bits, and the probe confirmed the account
  pipeline honours them.

**`eng/shims/claudinine`** — logic unchanged (it resolves `$dir/<os>-<arch>/claudinine`); it simply
lives at `libexec/` now. Same for the `.cmd`.

**W1b — new forwarders** (CLI artifact only), `eng/shims/bin-forward` and `eng/shims/bin-forward.cmd`:

```sh
#!/bin/sh
exec "$(CDPATH= cd -- "$(dirname -- "$0")/../libexec" && pwd)/claudinine" "$@"
```

```bat
@echo off
"%~dp0..\libexec\claudinine.cmd" %*
```

**`hooks/hooks.json`** — all four commands: `"${CLAUDE_PLUGIN_ROOT}/libexec/claudinine" hook`.

**`.github/workflows/build.yml`** — verify step (~lines 186-197): `verify/bin/...` →
`verify/libexec/...`; run `./verify/libexec/linux-x64/claudinine version`. Add a hosted pack + a hard
assertion: `test ! -e verify-hosted/bin` (this is the check that would have caught A0 in CI).

**`.github/workflows/cd.yml`** — attach both artifacts to the release. `marketplace.json` keeps
pointing at the CLI zip; its sha256 pinning is unaffected.

---

## W2 — Retrieval launcher next to the mirror

New `src/Claudinine/Mirror/Launcher.cs`.

```
<project>/<sid>/claudinine/run.sh    #!/bin/sh + exec "<absolute binary>" "$@"
<project>/<sid>/claudinine/run.cmd   Windows twin
```

- Path: `Path.Combine(MirrorLocator.ClaudinineDirFor(transcriptPath), "run.sh")` — subagent
  transcripts already map to their session's dir, so one launcher serves the session and all its
  agents.
- Target: `Environment.ProcessPath` — the actually-running AOT binary, not the shim. No env
  dependency, correct under both install contexts, and re-resolved on every pass.
- **Idempotent**: read the existing file, compare, write only on difference. Same temp-file +
  atomic-rename discipline as the rest of the codebase. A no-op pass must not touch mtime.
- `chmod 755` best-effort, but **the header invokes it as `sh <path>`**, so a lost exec bit is
  survivable. (Belt and braces: the probe showed modes do survive, but a launcher that only works
  with the bit set is a needless failure mode.)
- Called from `Compactor.Run` **and** `Compactor.MirrorOnly`, after the `MirrorLost` guard. A frozen
  session still needs retrieval to work.
- Optional but worth it: have `run.sh` pass its own directory to the binary (env var or `--dir`) so
  `GetVerb` addresses the mirror directly instead of searching. Faster, and immune to two sessions
  with colliding id prefixes.
- **GC**: `MirrorFile.CollectGarbageColocated` only acts on `.jsonl`/`.skip`/`.load`/`.seen` and
  ignores everything else, so `run.sh` is safe as written. Do not "fix" that into deleting unknown
  files — and note the whole dir dies with the session anyway via `SessionDirGc`.

---

## W3 — Digest headers stop invoking a bare command

`src/Claudinine/Rules/ChainCollapseRule.cs`, `Header()` (~lines 406-421): the five retrieval lines
become `sh <abs>/claudinine/run.sh get {sid} --ref …` etc.

**Trap — read this before touching the header text.** `Compactor.MirrorLost` builds its tripwire
phrase as `"claudinine get " + sid` and looks for it in marked records. Change the header wording
without changing that phrase and the fail-closed mirror-loss tripwire silently stops firing — the
exact class of bug the tripwire exists to prevent. Update it, and **match both forms**: transcripts
compacted by 0.1.x/0.2.x carry the old phrasing and must keep tripping it. Suggest matching on
`" get " + sid` anchored to a record carrying the `claudinine` marker, or keeping a literal
`claudinine get <sid>` substring inside the new command line so both remain true.

Same both-forms requirement applies to:

- `CloneVerb`'s retrieval-command rewrite (see `RuleHelpers` ~line 70).
- `ForkHealRule`'s ref retargeting.
- Any test fixture asserting header text.

---

## W4 — Self-healing header path

An absolute path baked into a transcript goes stale when the tree moves — cloud↔local, home rename,
project re-slug — which is exactly when the colocated mirror has moved *with* the transcript and
retrieval should still work.

On every pass: if the teaching header's launcher path differs from the current
`Launcher.PathFor(transcript)`, rewrite that substring. Natural home is the existing rule that
compacts retrieval instructions down to the first digest per file. Must be idempotent and must not
disturb any other byte of the record.

---

## W5 — Docs

- `README.md`: the human verbs still work on PATH for CLI installs. Add the hosted/Cowork form —
  `sh ~/.claude/projects/<slug>/<sid>/claudinine/run.sh restore-compaction-off <sid>` — and stop
  implying `claudinine` is universally on PATH.
- `docs/session-file-changes.md`: document `run.sh` / `run.cmd` as files Claudinine writes into
  `<sid>/claudinine/`.
- `docs/cowork-compatibility.md`: flip A0 and E5 to `[X]` when this lands.

---

## W6 — Tests

- Launcher: content, idempotence (second call writes nothing), atomic replace, target = current
  `ProcessPath`, survives a missing target with a legible error.
- Header: emission, and refresh after the transcript is moved to a new project dir.
- `MirrorLost`: trips on old-form *and* new-form headers; does not trip on a fork carrying the
  parent's phrase.
- Clone/fork: rewrite handles both forms.
- Pack: hosted zip has no top-level `bin/`; `libexec/claudinine` and `libexec/<rid>/claudinine` are
  `755` in the archive; CLI zip has both trees.
- Corpus: re-run `eng/bench/` — the retrieval block grows by ~5 × the launcher path length, once per
  file. Confirm the median saving does not move.

---

## Acceptance

1. `pack-plugin.ps1 -Hosted` output uploads to a Claude account without validator complaint.
2. In a fresh Cowork session, a digest header's command, pasted into Bash verbatim, returns content.
3. Move a project directory; the next pass rewrites the header and retrieval works again.
4. CLI archive install still yields `claudinine version` on PATH.
5. CI fails if a top-level `bin/` ever reappears in the hosted artifact.

## Do not do

Do **not** have a hook create `$CLAUDE_PLUGIN_ROOT/bin/` at runtime. The directory is already on the
Bash tool's PATH even when absent, so populating it from `SessionStart` would work — and would
reproduce precisely the un-reviewed PATH content the validator ban exists to prevent. It is
circumvention, it is likely to be closed, and W2 is both compliant and more robust.

## Adjacent, separate commits

- **C3** — add a `SubagentStop` hook. Its payload carries `agent_transcript_path`; compact that one
  file on the spot instead of sweeping `subagents/` at session boundaries.
- **C2** — add `Stop` with a min-interval guard, for autonomous stretches where `UserPromptSubmit`
  never fires.
