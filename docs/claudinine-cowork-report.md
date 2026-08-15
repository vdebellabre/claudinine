# Claudinine × Cowork — compatibility probe

Session: `2adf0db2-2355-520b-875d-89830000eb02` · host: Anthropic Cowork cloud container
Date: 2026-08-15 · plugin `0.1.20` (marketplace version `0001`), installed by plugin-file import.

Environment: Linux `6.18.5-fc-v20`, `x86_64`, Ubuntu **glibc 2.39**. Project dir `-home-claude`, cwd `/home/claude`, running as `root`.

---

## 1. B2 — binary runs on the cloud image · **PASS**

```
$ ~/.claude/plugins/synced/claudinine/libexec/claudinine version
1.0.0                                    exit=0
```

Shim resolved `uname -s`=Linux / `uname -m`=x86_64 → `linux-x64/claudinine`. Dynamic linkage is
minimal and satisfied:

```
linux-vdso.so.1 · libm.so.6 · libc.so.6 · ld-linux-x86-64.so.2
```

No missing symbols, no glibc version floor problem on 2.39. Arch dispatch and glibc are both fine.

> **Finding V1 (cosmetic).** `version` prints `1.0.0` while `plugin.json` declares `0.1.20`.
> Assembly version is not tracking the plugin version. Harmless, but it makes "which build is
> deployed" unanswerable from inside a session — which is exactly the question a compatibility
> probe wants to answer.

## 2. SessionStart pass · **PASS**

`~/.claude/projects/-home-claude/<sid>/claudinine/` was present before any manual intervention,
created by the real hook, containing:

| file | role |
| --- | --- |
| `<sid>.jsonl` | mirror |
| `<sid>.jsonl.seen` | ledger (`claudinine-seen v1`, user uuids + content hashes + `len:`) |
| `run.sh` / `run.cmd` | retrieval launchers, regenerated each pass |

`run.sh` had already been rewritten with the **resolved absolute binary path**
(`.../libexec/linux-x64/claudinine`) — the no-PATH path. Confirmed `claudinine` is *not* on PATH in
this container, so the launcher is load-bearing here, not a nicety.

## 3. Compaction · **PASS**

After feeding the session tool-heavy work (11-call and 12-call turns, a 433 KB offloaded output, a
subagent, a device-bridge call, `SendUserFile`):

| metric | value |
| --- | --- |
| digest headers (`[claudinine: this turn originally ran`) in main transcript | **9** |
| live transcript | 192,969 B |
| mirror (= uncompacted equivalent) | 280,900 B |
| live as % of full | **69%** |
| single observed pass | 62 → 51 records, 131,175 → 125,360 B |

Chain-collapse behaves as documented: one pass took the header count from 5 to 4 while shrinking the
file, i.e. adjacent digests merged rather than accumulating.

Subagent sweep at `SessionEnd` fired correctly:

| | before | after |
| --- | --- | --- |
| `subagents/agent-af516fbb5642b1cf2.jsonl` | 11 records / 34,386 B | **7 records / 19,835 B** |

with its own sidecar `claudinine/agent-af516fbb5642b1cf2.jsonl{,.seen}`.

Idempotency confirmed byte-exact: `sha256` of the transcript unchanged across a repeat pass
(`8c89be515fe3053e` → `8c89be515fe3053e`).

## 4. W3 — retrieval end-to-end · **PASS**

The digest header spells the launcher form, not a bare command:

```
sh "/root/.claude/projects/-home-claude/<sid>/claudinine/run.sh" get <sid> --ref REF --grep PATTERN
```

Executed **exactly as written**, all forms work:

| form | result |
| --- | --- |
| `--ref 8b78067b --info` | `5854 bytes, 49 lines (~1462 tokens)` · exit 0 |
| `--ref 8b78067b --grep 'claudinine-seen'` | matched line returned · exit 0 |
| `--grep 'GLIBC'` (all archived outputs) | 3 hits across 2 refs · exit 0 |
| `--ref 7987e0e2 --full` | full output · exit 0 |
| subagent sidecar, `get agent-af516fbb5642b1cf2 --ref e227a267 --info` | `12385 bytes, 110 lines` · exit 0 |

**Control:** the bare form the header no longer uses —

```
$ claudinine get <sid> --ref 8b78067b --info
bash: claudinine: command not found            exit=127
```

So W3 was necessary, and W3 is landed. Subagent digests correctly address their own sidecar by agent
id rather than the session id.

**Fidelity check:** retrieved the archived `Read(README.md)` output via `--full` and diffed against
the file on disk — **69 / 69 non-blank lines recovered, 0 missing**. Content is intact, not
lossy-summarised.

## 5. D5 — record shape · **PASS, with one behavioural finding**

Record types exercised before validating: `SendUserFile` (file_uuid returned), device-bridge
`device_list_dir`, a `general-purpose` subagent, and a `tool-results/` offload (433.6 KB →
`tool-results/bjgaeuk1q.txt`).

Every line of all three transcripts JSON-parses, after 20+ compaction passes:

| file | lines | parse errors | dangling `parentUuid` | orphan `tool_result` | unanswered `tool_use` |
| --- | --- | --- | --- | --- | --- |
| live | 67 | **0** | 0 | 0 | 1 (in-flight call) |
| mirror | 92 | **0** | 0 | 0 | 1 (same) |
| subagent | 11 → 7 | **0** | 0 | 0 | 0 |

`tool_use` / `tool_result` pairing survives compaction intact — no orphaned results, no broken
parent chains. The `SendUserFile` tool_use and its `file delivered to user … file_uuid: …` result
are both preserved verbatim in the mirror. The offloaded `tool-results/bjgaeuk1q.txt` is untouched
(444,000 B) and the record referencing it is preserved, so the pointer still resolves.

> **Finding D5-a (needs a decision).** Claudinine **drops `queue-operation` records** from the live
> transcript: 8 present in the mirror, **0** in live. They are archived, not lost, but they are the
> one record class removed outright rather than digested. These are Cowork/Claude-Code queue
> bookkeeping records, not conversation. Two questions worth answering in code rather than by
> observation: (a) is the removal deliberate or are they falling through a type filter as
> unrecognised? (b) does anything replay them on resume? Also seen only in the mirror: one `system`
> record and extra `last-prompt` records — same question class.

> **Finding D5-b (worth a guard).** Claudinine's GC covers sidecars and orphaned session dirs.
> `tool-results/` now lives inside the same session directory and is referenced by live records via
> an absolute path. It was correctly left alone here, but confirm the GC can never treat it as
> orphan debris — deleting one of those files silently breaks a live transcript reference.

## 6. B3 — cost on this hardware · **PASS, matches the dev-box number**

Transcript 187,655 B / 74 records, mirror 218,950 B. Five runs per event, wall clock incl. process
start:

| hook event | avg |
| --- | --- |
| `UserPromptSubmit` | **16 ms** |
| `SessionStart` | **17 ms** |
| `PreCompact` | **14 ms** |
| `SessionEnd` | **15 ms** |

The 18 ms dev-box figure holds on cloud hardware — 14–17 ms here. Against hook timeouts of 25–60 s
that is ~0.06% of budget. Native AOT startup is not penalised by this container.

---

## Verdict

Claudinine is **functionally compatible with Cowork**. All six checks pass. Nothing is mangled; the
one substantive finding (D5-a, `queue-operation` removal) is a deliberate-or-not question rather
than an observed breakage.

### Cowork-specific caveat worth stating in the README

The container is **ephemeral** — reclaimed after inactivity. The headline benefit ("resuming is
faster and cheaper") depends on the transcript surviving to be re-read, which in cloud Cowork it
largely does not. What *does* apply here is the `PreCompact` path: a leaner transcript before
Claude's own compaction is a real in-session win. Relatedly, `SessionEnd` may never fire if the
container is reclaimed rather than exited; the `SessionStart` repair pass is what carries the load,
which is the right design for this host but is currently justified in the README as crash recovery
rather than as the primary path.

### Punch list

1. **V1** — make `version` report the plugin version (`0.1.20`), not `1.0.0`.
2. **D5-a** — confirm `queue-operation` (and `system` / `last-prompt`) removal is intended; document it.
3. **D5-b** — assert in GC that `tool-results/` is never a deletion candidate.
4. **Docs** — add the Cowork caveat above; the ephemeral-container case changes which hook matters.
