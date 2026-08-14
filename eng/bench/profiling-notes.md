# Profiling notes

Findings from profiling the compaction pass. The harness that produces them is
`src/Claudinine.Benchmarks` (see its README for how to run it); this file is the
running record of what the profiles actually said.

Speed only. The effectiveness numbers (token reduction) come from `compare.py`
and are a different measurement — never quote a byte figure from a profiling run
as a context saving.

## 2026-08-13 — first CPU profile

VS Performance Profiler, CPU Usage, `run --warmup -n 20` (~44 s of measured CPU,
174 files, 189 MB corpus). Steady-state pass: 2704 ms, median 3.7 ms/file, p95
81 ms, max 232 ms (the 14.9 MB session), 69.9 MB/s.

Ranked by **self** CPU:

| self | frame |
|---:|---|
| 13.7% | `JsonValueOfElement.TryGetValue<T>` |
| 11.8% | `JsonDocument.Parse` |
| 7.9% | `String.SplitInternal` |
| 7.8% | `JsonObject.InitializeDictionary` |
| 3.0% | `Utf16Utility.GetPointerToFirstInvalidChar` |

### It is not the parser

The obvious reading is "JSON deserialization is the bottleneck". That is not
quite what the profile says, and the distinction changes what is worth fixing.

`System.Text.Json.Nodes` materializes **lazily**. A `JsonValue` holds a
`JsonElement` (raw UTF-8 bytes) until someone asks for its value, and
`TryGetValue<string>` is where those bytes are decoded into a `string`. That
decode is **not cached**. Measured directly:

```
ReferenceEquals across two reads of the same JsonValue: False
20k reads of one 20 KB value: 61 ms, 764 MB allocated
```

Every read re-decodes and allocates a fresh string.
`Utf16Utility.GetPointerToFirstInvalidChar` in the profile is the UTF-16
validation of exactly those repeated decodes.

So `TryGetValue` outranking `Parse` is a statement about **access patterns**, not
about the JSON stack: 16 rules each walk every record, across ~94 `.GetString()`
call sites, so the same fields are decoded many times per pass.

**Implication:** swapping JSON libraries would buy little. Caching decoded text
per record for the life of a pass — or hoisting repeated `GetString()` reads into
locals inside a rule — targets the actual cost. The chokepoints are
`RuleHelpers.TextOf` and `RuleHelpers.ResultText` (5 call sites between them).

`JsonObject.InitializeDictionary` is the sibling cost: every `node["key"]` lookup
on a not-yet-materialized object builds the whole property dictionary first.

### Not bottlenecks, despite looking like candidates

- **`DocumentDedupRule` hashing** (8.0% total, 2.6% self). SHA-256 over ≥1 KB
  blocks is visible but modest; most of that total is the `TextOf` decode feeding
  the hash, not the hash itself — same root cause as above.
- **`Minify`'s exception-driven control flow.** `tool-result-age` throws ~50
  `JsonException`/pass detecting non-JSON tool results
  (`ToolResultAgeRule.Minify`, via `catch (JsonException)` on the common path).
  Worth replacing with a `Utf8JsonReader` probe, but at ~50 per pass it is a
  rounding error next to the decode traffic. Fix the decodes first.

### Per-rule ranking

From `bench --filter *RuleBenchmarks*` on the median main transcript — cold-start
job, so treat the absolute numbers as indicative and the ordering as the signal:

`chain-collapse` dominates at ~22 ms, roughly 4x the next rule, and is also the
largest allocator (~1.4 MB). Everything else lands between 0.4 and 5.4 ms.

### Caveat

BenchmarkDotNet and the profiler both measure JIT-compiled managed code. The
shipped binary is Native AOT with `OptimizationPreference=Size`, so absolute
numbers differ from production. Use these for relative comparisons and for
finding hot paths. The 2704 ms pass time above was also measured UNDER the
profiler; standalone runs of the same build land around 2060–2215 ms — never
compare a profiled number against an unprofiled one.

## 2026-08-14 — decode memoization

Intervention for the `TryGetValue` finding: `Json.GetStringMemo`, a per-node
memo of decoded strings (`ConditionalWeakTable` keyed by node reference — sound
because rules replace nodes, never mutate them; weak keys let each file's tree
collect with its pass). Used at payload call sites only (block text/content:
`TextOf`, `ResultText`, and the rules' inline content reads). Plus two free
fixes: `IsUserPrompt` / `IsRealUserMessage` decoded the ENTIRE user prompt just
to test "is it a string" — now a `GetValueKind()` check, no decode.

Measured by interleaved A/B (same machine, alternating exes, `run --warmup
-n 3`, median of 3 steady-state numbers), byte-identical output on both sides:

| variant | median pass |
|---|---:|
| baseline | 2187 ms |
| memo on EVERY `GetString` | parity (2152 ms vs 2158 — a wash) |
| memo on payload reads only | **1919 ms (~12% faster)** |

The wash is the finding worth keeping: small-field reads (`type`, ids) are so
numerous that the weak-table lookup+insert cancels everything the payload memo
saves. Don't "fix" this by routing all `GetString` traffic through the memo
again — the split (plain `GetString` for small fields, `GetStringMemo` for
payloads) is load-bearing for the win.

## 2026-08-14 — rewrite-path re-parse, split traffic, dedup hashing

Post-memo profile (Report20260814-0021.diagsession): `TryGetValue` self fell
13.7% → 8.0%; new ceiling is `JsonDocument.Parse` 12.4%, `SplitInternal` 8.9%,
`InitializeDictionary` 8.8%. Three interventions, each interleaved-A/B'd with
byte-identical output:

1. **`TryRewrite` re-validation no longer re-parses untouched lines** (~9%).
   The old flow joined the output into one giant string, split it back, and
   `TryParse`d EVERY line — but an untouched line is byte-identical to its
   load-time `RawLine`, whose parse (`rec.Node`) we already hold; re-parsing it
   proved nothing and roughly doubled parse volume. Serialized lines (the only
   round-trip risk) are still independently re-parsed, every semantic check
   (chain, dangling-*, reachability delta, tail) is preserved, and the
   join-then-split count check survives as an embedded-newline guard on
   serialized lines. The compute half is now `TryComputeRewrite`, which the
   bench harness calls directly (the hand-rolled copy in `Harness` is gone —
   it can no longer drift). Production writes stream the line list to the temp
   file; the giant joined string no longer exists.
2. **Split-once `PreviewRenderer`, gated `TrimOversized`** (folded into the
   same measurement). RenderPreview split the same result text up to 4 times
   per preview; TrimOversized split every oversized payload just to find most
   never exceed the line cap (now an allocation-free `Count('\n')` gate).
3. **`DocumentDedupRule` buckets by text length before hashing** (~4%). Equal
   text implies equal length, so only length-colliding blocks get SHA-256'd;
   the length-unique majority skips hashing entirely. File order (first
   occurrence wins) is preserved by bucket insertion order.

Net effect this round: median pass 1936 → 1853 ms in the final A/B (and the
memo round before it: 2187 → 1919), per-file median 3.9 → 2.4 ms, per-file
mean ~10.4 ms unprofiled. Remaining ceiling is the load parse itself plus
`InitializeDictionary` — further real gains mean the JsonElement read-layer
refactor (read via pooled `JsonDocument`, build `JsonObject` only on rewrite),
or attacking `chain-collapse` rule internals (~4x the next rule).

## 2026-08-14 — the pass is GC-bound: server GC

With `TranscriptRecord.TryParse` the top total-CPU frame (26%, mostly
`JsonDocument.Parse`), the obvious move was parallelizing the load parse —
lines parse independently. **It made things WORSE** (interleaved A/B: 2014 →
2069 ms median), and the 14.9 MB file didn't move at all. The tell: under
server GC the same parallel build jumped to 1512 ms, and the SEQUENTIAL build
beat it at 1276 ms. Conclusion: the pass is GC-bound, not CPU-bound — parsing
allocates a JsonNode graph per record, and workstation GC's small gen0 budget
collects constantly under that allocation rate; adding threads only added
allocation contention. Parallel parse is reverted; do not re-attempt it
without re-checking the GC picture first.

`<ServerGarbageCollection>true</ServerGarbageCollection>` is now set on the
shipped binary AND the benchmark project (numbers measured under different
collectors are not comparable). Interleaved A/B, byte-identical output:

| | workstation GC | server GC |
|---|---:|---:|
| median pass | 1813 ms | **1376 ms (-24%)** |
| per-file mean | 10.2 ms | **7.8 ms** |
| per-file p95 / max | 61 / 192 ms | 36 / 113 ms |
| peak working set (bench, 189 MB corpus in memory) | ~592 MB | ~699 MB |

The memory cost is transient working set in a process that lives well under a
second; on production single files (≤15 MB) the absolute delta is a fraction
of the bench's. Cumulative since the 2026-08-13 baseline: pass ~2187 → ~1376
ms on the same machine, per-file mean into single digits.

## 2026-08-14 — end-to-end wall clock of the shipped AOT binary

Everything above measures JIT-compiled code inside one warm, long-lived process.
Production is nothing like that: the app spawns the Native AOT binary once per
hook event, it compacts one file and exits. The `aot` verb closes that gap —
subprocess invocation, hook JSON on stdin, one process per event.

Binary: `publish/win-x64/claudinine.exe` (AOT, 3.0 MB, built 2026-08-12 — so it
predates the server-GC commit; re-measure after the next release build).
Full corpus, 174 files, 189 MB, one iteration:

| | UserPromptSubmit | SessionStart |
|---|---:|---:|
| | steady state, session file only | adds subagent sweep + mirror GC + session-dir GC |
| total wall clock | 15.94 s | 19.04 s |
| mean / invocation | 91.6 ms | 109.4 ms |
| median | 56.4 ms | 71.0 ms |
| p95 | 209.2 ms | 287.2 ms |
| min / max | 33.3 / 560.3 ms | 48.1 / 798.9 ms |

Output was byte-identical to the in-process `run` (77.4% smaller both ways),
which is the cross-check that the subprocess path does the same work.

### The floor is not process startup

Tempting conclusion from a 33 ms minimum: "startup dominates, the pass is
free". Measured directly, it does not hold. `claudinine version` — start, print
a string, exit — is **~12 ms** (median of 15 runs, file cache warm). So on the
smallest real session the split is roughly 12 ms of process creation and ~21 ms
of actual pass. Process start is real and unavoidable, but it is the minority
of the floor; the reporting line is deliberately labelled `floor`, not
`startup`, to keep the two from being conflated.

Useful framing for the numbers above: the median session costs ~56 ms of hook
latency per prompt, of which ~12 ms is process creation. Cutting the decode
traffic (see 2026-08-13) attacks the other ~44 ms; nothing in this codebase can
attack the 12 ms short of not spawning a process.

### Three invariants the harness must keep, and why

1. **Runs on a copy.** `Compactor.Run` rewrites in place; pointing this at
   `bench/corpus/` would destroy the baseline on first use. Verified after a
   full run: corpus mtimes and byte total (198,496,208) unchanged.
2. **Pristine copy per invocation.** The pass is idempotent — a second run over
   the same file finds its work done and reports a time no real hook would see.
3. **`CLAUDE_PLUGIN_DATA` redirected into the temp workspace.** Mirrors live
   outside the transcript dir, so without this the harness appends megabytes
   into the real mirror pool. Verified untouched after a full run.

Related trap, not currently a problem: mirror *reads* (`MirrorLocator.SearchDirectories`)
also probe `~/.claudinine/mirrors`, so `ForkHealRule` could in principle read a
real mirror for a session id that exists there. Read-only and it changed nothing
observable here, but it is why the corpus copies keep their original session ids
rather than being renamed — renaming would make this silently more likely, not
less.

### Local AOT builds are broken on this machine (not a code problem)

`dotnet publish -r win-x64` fails with "Platform linker not found". Diagnosed:
VS 18 Enterprise ships `link.exe`, but `VC/Tools/MSVC/*/lib/x64` is empty (only
the `onecore` variant is present) and the Windows SDK has no `kernel32.lib` — the
C++ libs/headers are not installed. `Launch-VsDevShell.ps1` also fails (no
`vswhere.exe`), and `vcvars64.bat` delegates to a `vcvarsall.bat` that is not
present in this layout. CI does the AOT builds; locally, point `--exe` at a
release archive or the plugin cache
(`~/.claude/plugins/cache/claudinine/claudinine/<ver>/bin/win-x64/`).

Size is the reliable AOT tell and the harness filters on it: a real AOT binary
is ~3.0 MB, while the framework-dependent apphost of the identical name in
`bin/Release/net10.0/` is ~162 KB. Timing the apphost would measure a `dotnet`
launch, not the shipped artifact — the reason auto-detection ignores anything
under 1 MB and there is no JIT fallback.

## 2026-08-14 — AOT binary from the bench publish profile

`bench/bin/claudinine.exe` (bench publish profile, `PublishDir=bench/bin`) is now
the first place `aot` looks, ahead of `publish/` and `src/.../bin/Release`.
Candidates are still ordered newest-first and still filtered to >1 MB, so a
framework-dependent apphost of the same name can never be timed by mistake.

Full corpus, `--event UserPromptSubmit`, 174 files / 189.2 MB:

| | new (`bench/bin`, Speed) | previous (`publish/win-x64`, Size) |
|---|---:|---:|
| mean | 96.4 ms | 77.1 ms |
| median | 66.8 ms | 51.8 ms |
| p95 | 262.7 ms | 198.9 ms |
| max | 543.5 ms | 416.4 ms |
| floor | 43.3 ms | 39.1 ms |
| start only (`version` × 30) | 15.7 ms | 12.4 ms |
| size | 3.87 MB | 2.90 MB |

**The Speed-optimized binary measured slower than the Size-optimized one.**
Counter-intuitive, so it was checked before being believed: two interleaved A/B
rounds over the same 60-file subset (cancels machine drift) gave NEW 53.1 / 53.7 ms
median against OLD 47.0 / 48.0 ms. The gap reproduces.

Startup accounts for only part of it — ~3.3 ms of a ~15 ms median regression. The
remainder is in the pass itself.

Attribution is **not** established, and the difference is not necessarily
`OptimizationPreference` at all. Confounds, all changing at once between the two
binaries:

- `OptimizationPreference` Size → Speed
- ILCompiler 10.0.10 → 10.0.11 (different codegen entirely)
- the old binary predates the server-GC and code-quality commits

Bisecting locally is currently blocked: `dotnet publish` still fails at the link
step from the command line (`vswhere.exe` not found, MSB3073 code 123), even though
publishing from inside Visual Studio succeeds — VS supplies the environment the
batch files cannot find. To attribute this properly, publish `Speed` and `Size`
from VS into two directories and run `aot --exe` against each, holding ILCompiler
constant.

Two csproj properties in the current publish setup are no-ops worth removing:

- `PublishTrimmed` — ILCompiler errors if you try to disable it: "PublishTrimmed
  is implied by native compilation and cannot be disabled."
- `PublishReadyToRun` (in `FolderProfile.pubxml`) — R2R pre-JITs managed IL, which
  a Native AOT publish does not emit. Both output directories contain only
  `claudinine.exe` + `.pdb`, confirming it was ignored.

## 2026-08-14 (later) — Size restored, and server GC turns out to be a pessimization

Rebuilt from VS with `OptimizationPreference=Size` and the redundant
`PublishTrimmed` removed. Binary 3.87 → 3.17 MB.

Full corpus, `--event UserPromptSubmit`, 174 files:

| | Size (new) | Speed (new) | Size (old, 08-12) |
|---|---:|---:|---:|
| mean | 85.8 ms | 96.4 ms | 77.1 ms |
| median | 58.5 ms | 66.8 ms | 51.8 ms |
| p95 | 208.1 ms | 262.7 ms | 198.9 ms |
| floor | 39.1 ms | 43.3 ms | 39.1 ms |
| start only | 15.7 ms | 15.7 ms | 12.2 ms |

So `Size` recovers most of the Speed regression (median 66.8 → 58.5 ms) and is the
right setting. But it does **not** explain the gap to the old binary: with the flag
now identical on both, interleaved A/B still gives new 52.9 / 51.2 ms against old
47.5 / 46.9 ms, and startup is still +3.5 ms. `OptimizationPreference` was not the
main cause. Remaining candidates: ILCompiler 10.0.10 → 10.0.11, and the code
changes since 08-12.

### Server GC is slower here, in every size tier

`ServerGarbageCollection=true` (csproj line 26) was shipped on the reasoning that
the pass is GC-bound. That holds **in-process**, but not for the subprocess path
the hooks actually use. Toggled at runtime via `DOTNET_gcServer`, same binary, so
nothing else varies:

| corpus slice | gcServer=0 (workstation) | gcServer=1 (server) |
|---|---:|---:|
| full, median | ~50 ms | ~60 ms |
| 60 smallest, median | 48.4 ms | 52.0 ms |
| agent half, median | 46.8 ms | 55.3 ms |
| main half (big sessions), median | 59.5 ms | 73.0 ms |
| main half, mean | 92.2 ms | 104.4 ms |

Workstation GC wins **every** tier, by 10–15 ms at the median — including the large
sessions server GC was meant to help. With `gcServer=0` the new binary beats the
old one outright (44.6 vs 44.5 ms on the 60-file subset, and better on the full
corpus), which also closes the regression above.

Why the in-process result inverted: server GC sizes its heap and per-core heaps for
a long-lived process amortizing startup across many collections. A hook invocation
lives ~50 ms and exits, so it pays server GC's setup on every single call and never
reaches the point where the larger gen0 budget pays off. The one measurement where
server GC looked good (`run`, in-process, 174 files in one process) is exactly the
shape production never has.

Caveat on the aggregate: on the full corpus `gcServer=0` shows a *worse* mean and
p95 in some rounds despite the better median — variance is high on the few 10 MB+
files. The per-tier split above is the reliable read; the aggregate mean is
dominated by a handful of outliers.

**Not changed in the csproj** — this contradicts a deliberate, documented decision
(see the comment above line 26 and the 2026-08-14 speed round in memory), so it is
reported, not applied. Verifying before flipping it: confirm with `PerfView` that
GC pause time actually drops, and re-check the in-process `run` number, which may
legitimately prefer server GC.

## 2026-08-14 (settled) — Speed vs Size vs default, all confounds removed

Local AOT publish works after all. The blocker was never the linker: ILCompiler
shells out to `vswhere.exe` (in `C:\Program Files (x86)\Microsoft Visual Studio\Installer`),
which is not on the default PATH in a non-VS shell. Prepend it and
`dotnet publish -c Release -r win-x64` succeeds from `src/Claudinine`:

    export PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH"

That makes proper A/B possible: same ILCompiler (10.0.11), same commit, one variable.

### `Balanced` does not exist — the default IS `Size`

Publishing with no `OptimizationPreference` and with `Size` produced
**byte-identical binaries** (SHA-256 `1ef0ac19…`, 3,058,176 bytes both). So there is
no three-way choice; omitting the property is exactly equivalent to `Size`.

### Speed vs Size: indistinguishable

Workstation GC baked into both, three interleaved full-corpus rounds
(`--event UserPromptSubmit`), medians in ms:

| round | Speed | Size |
|---|---:|---:|
| 1 | 53.1 | 54.1 |
| 2 | 53.1 | 53.2 |
| 3 | 61.2 | 54.7 |

Startup is equal too (40 × `version`: Speed 15.7 ms median, Size 16.0 ms). The only
robust difference is **size: 3.61 MB vs 2.92 MB**. Per-tier runs flip direction
between rounds (`main` tier: Speed 74.9 then 56.6; Size 58.5 then 79.0), which is
variance on the few 10 MB+ files, not codegen.

**Conclusion: keep `Size`.** It is the ILCompiler default, ~0.7 MB smaller, and
equal on speed. `Speed` buys nothing measurable on this workload.

This also corrects the 2026-08-14 entry above, which read Speed as ~15 ms slower.
That comparison was confounded — the two binaries differed in ILCompiler version
and in code, not just the flag. Held constant, the flag does nothing.

### Server GC: the finding stands, but the earlier method was invalid

The earlier GC numbers came from toggling `DOTNET_gcServer` on a binary with
`ServerGarbageCollection=true` baked in. That is unreliable: the csproj flag is
genuinely embedded (workstation build 3,058,176 bytes vs server build 3,328,512 —
different binaries), so the env var was fighting an embedded setting. On a clean
workstation-GC binary, toggling the env var changed nothing (medians 49.2 / 50.2 /
49.0 vs 49.9 / 49.8 / 48.5) — the var was not taking effect.

Redone honestly: two binaries differing **only** in `ServerGarbageCollection`, one
discarded warm-up round (round 1 is a cold-cache outlier, ~106 ms vs ~63 warm),
then interleaved rounds. Medians in ms:

| round | workstation | server |
|---|---:|---:|
| agent r1 | 51.2 | 58.7 |
| agent r2 | 50.3 | 58.0 |
| agent r3 | 48.9 | 56.4 |
| agent r4 | 47.8 | 56.2 |
| agent r5 | 48.7 | 56.2 |
| main r1 | 63.7 | 71.1 |
| main r2 | 60.2 | 72.1 |
| main r3 | 60.7 | 69.0 |

**Workstation wins every warm round in both tiers, with no overlap between the
ranges** — ~8 ms / ~15% at the median, and better mean and p95 on `main` too. The
conclusion from the previous entry survives a valid test.

Recommended csproj change, still **not applied** (it reverses a documented
decision, and the in-process `run` path may legitimately prefer server GC):

    <ServerGarbageCollection>false</ServerGarbageCollection>

Worth noting the asymmetry before flipping it: server GC was chosen from an
in-process measurement (174 files in one process), which is the one shape
production never has — every hook invocation is a fresh ~50 ms process.

## 2026-08-14 (settled, part 2) — server GC IS a real win in-process

Confirms the other half of the asymmetry. `run --warmup -n 5`, one discarded
cold-cache round, then 5 interleaved rounds. Steady-state pass time (last
iteration of each run) in ms:

| round | server | workstation |
|---|---:|---:|
| 1 | 1289 | 1693 |
| 2 | 1356 | 1694 |
| 3 | 1238 | 1758 |
| 4 | 1321 | 1777 |
| 5 | 1214 | 1766 |

**Server GC wins every round with no overlap** — ~1280 vs ~1740 ms, a ~26-30%
reduction. It also halves the tail: p95 ~33 ms vs ~59 ms, and max on the 14.9 MB
session 108-118 ms vs 162-166 ms. Per-file median barely moves (2.3-2.6 ms both
ways), so the gain is concentrated exactly where allocation pressure is highest —
the large sessions, which is what the original decision predicted.

Method note: unlike the AOT binaries, `DOTNET_gcServer` **is** honoured here. The
bench host is a JIT build with an external `runtimeconfig.json`
(`System.GC.Server: true`), and the env var takes precedence over that. Verified
with a probe reading `GCSettings.IsServerGC`: default True, `DOTNET_gcServer=0`
→ False, `=1` → True. That is why this measurement is valid where the earlier
AOT one was not — an embedded AOT config is not overridable the same way.

### The two paths genuinely disagree

| | in-process (`run`, 174 files, one process) | subprocess (`aot`, one process per file) |
|---|---|---|
| winner | **server GC**, ~26-30% faster | **workstation GC**, ~15% faster |
| where the gain lands | large sessions (p95 halved) | all tiers, median |

Both results are real and reproducible; they are measuring different things. Server
GC's larger gen0 budget and per-core heaps pay off across 174 files in one process
and never pay their setup cost twice. A hook invocation lives ~50 ms, so it pays
that setup on every call and exits before the budget matters.

**Production is the subprocess shape** — every hook event is a fresh process. So the
subprocess measurement is the one that describes shipped behaviour, and `run` is the
in-process proxy that motivated the original server-GC decision.

Still not changed in the csproj. The decision is now a clear trade with numbers on
both sides rather than an open question:

- ship `ServerGarbageCollection=false` → ~8 ms (~15%) off every real hook invocation
- keep `true` → ~460 ms off a full-corpus in-process pass, which only the bench does

If the goal is user-visible hook latency, workstation wins. Note that flipping it
makes the `run` verb's numbers ~30% worse, so re-baseline the notes above if it
changes; `bench`/`run` remain useful for relative comparisons either way.
