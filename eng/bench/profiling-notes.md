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

## 2026-08-14 (applied) — workstation GC shipped, explicit Size restored

`ServerGarbageCollection=false` is now in the csproj, with the rationale and the
"do not restore this from a `run` number" warning inline. Verified on the actual
shipped binary against a server-GC control built from identical code, agent tier
medians: workstation 46.6 / 47.2 / 46.6 ms vs server 56.3 / 56.7 / 54.8 ms. The
~8 ms / ~15% win reproduces with no overlap.

New full-corpus baseline (`aot --event UserPromptSubmit`, 174 files / 189.2 MB):
mean 83.6 ms, median 58.9 ms, p95 186.1 ms, floor 41.8 ms. Solution builds with
0 warnings; 275/275 tests pass.

### Correction: the ILCompiler default is NOT `Size`

The entry above ("`Balanced` does not exist — the default IS `Size`") is **wrong**.
That test was invalid: the csproj still contained `OptimizationPreference=Size` at
the time, so the "no explicit setting" build inherited it and the comparison was
Size against Size.

Re-measured with the property genuinely absent from the csproj — three distinct
binaries:

| setting | size |
|---|---:|
| explicit `Size` | 3,058,176 bytes |
| **default (property absent)** | **3,589,632 bytes** |
| explicit `Speed` | 3,786,240 bytes |

So there are three real options, and the default is its own middle ground. The
original "none = balanced" reading was correct.

Timing all three (workstation GC, agent tier, 3 interleaved rounds) — medians in ms:

| round | Size | default | Speed |
|---|---:|---:|---:|
| 1 | 48.1 | 48.0 | 48.7 |
| 2 | 47.8 | 48.4 | 47.7 |
| 3 | 48.7 | 48.5 | 47.2 |

All three are within noise. The choice is therefore purely about binary size, and
`OptimizationPreference=Size` is set explicitly: **531 KB smaller than the default,
728 KB smaller than `Speed`, for no measurable time cost.** Leaving the property
absent silently ships the larger middle binary.

Method lesson, twice over now: when testing whether a property matters, remove it
from the project file — do not just omit it from the command line, where the
project's own value still applies. Both invalid results in these notes came from
exactly that.

## 2026-08-14 — framework trimming feature switches: investigated, none adopted

Went through every switch on
<https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options#trim-framework-library-features>.
**Conclusion: nothing to add.** Do not re-investigate without new information —
the reason is not "probably fine", it is measured.

### Most are already set by PublishAot

Dumped from the generated runtimeconfig of the current build. Already `false` (or
already optimized) without any csproj entry:

`EventSourceSupport`, `Http3Support`, `MetadataUpdaterSupport`,
`AutoreleasePoolSupport`, `EnableUnsafeBinaryFormatterSerialization`,
`EnableUnsafeUTF7Encoding`, `CustomResourceTypesSupport`,
`BuiltInComInteropSupport`, `StartupHookSupport`, `EnableCppCLIHostActivation`,
and `UseSizeOptimizedLinq` (already on). Adding any of these is a literal no-op.

Not applicable: `UseNativeHttpHandler` (Android/iOS only),
`XmlResolverIsNetworkingEnabledByDefault` (no `System.Xml` usage in the project),
`DebuggerSupport` (symbol removal already covered by `TrimmerRemoveSymbols`),
`StackTraceLineNumberSupport` (.NET 11+, and it *adds* size).

`MetricsSupport=false` was built anyway to check: **byte-identical** output, since
nothing references `System.Diagnostics.Metrics`.

### Only two switches change anything, and both are size-only

No measurable runtime difference — agent tier, 3 interleaved rounds, medians
47.3-49.0 ms across every variant including the combined one:

| variant | size | delta |
|---|---:|---:|
| current | 3,058,176 | — |
| `StackTraceSupport=false` | 2,802,176 | −250 KB |
| `UseSystemResourceKeys=true` | 3,033,600 | −24 KB |
| both | 2,763,776 | −288 KB |

**`UseSystemResourceKeys=true` — rejected.** It strips framework exception
messages. Measured on an AOT probe:

    Could not find a part of the path 'Z:\missing.txt'.  ->  IO_PathNotFound_Path, Z:\missing.txt
    'n' is an invalid start of a property name...        ->  ExpectedStartOfPropertyNotFound, n
    Index was outside the bounds of the array.           ->  Arg_IndexOutOfRangeException

The code surfaces `e.Message` in 9 user-facing places (`restore failed writing the
transcript:`, `clone failed:`, `get failed:`, several `Dbg.Log` calls), so a bug
report would arrive as an opaque resource ID. 24 KB is not worth that.

**`StackTraceSupport=false` — rejected.** It preserves `e.Message` and only
degrades frame names to raw addresses (`at claudinine!<BaseAddress>+0x3c3b`), and
nothing in the codebase reads `.StackTrace` or `Exception.ToString()`, so it looks
free. It is not: `Compactor.cs` has `catch when (!Dbg.Enabled)` specifically so a
misbehaving rule **crashes** under `CLAUDININE_DEBUG=1` rather than being
swallowed. In that path the runtime-printed stack trace is the whole diagnostic for
finding which rule broke. Trading that for 250 KB in a plugin-cache binary is not
worth it.

General rule this establishes: these switches trade binary size for diagnosability
and buy **no throughput** on this workload. Size is not a constraint here, so the
default answer is no.

## 2026-08-14 — reconciling `aot` (~50-80 ms) with steady.py (~16-19 ms)

Not a discrepancy. The two harnesses measure different passes, and both are right.

`eng/bench/steady.py` fires `SessionStart` once as an **untimed warm-up**, then times
`UserPromptSubmit` over the now-compacted file — it even asserts the size stopped
changing, because if the file is still shrinking the "at rest" premise is false.
That is prompt N with 1..N-1 already done: one turn of new work.

`aot` gave every invocation a **pristine, uncompacted** copy (AotVerb invariant 2),
so it could only ever measure the cold pass — a fresh or resumed session mirroring
the whole file from scratch.

Same corpus half, same binary, both numbers from steady.py's own output:

| pass | median |
|---|---:|
| steady (`UserPromptSubmit`, file at rest) | 16.2 ms |
| cold (`SessionStart`, untouched) | 78.8 ms |

The cold figure lines up with `aot`'s 83.6 ms mean, so nothing was mismeasured —
`aot` simply had no steady-state mode. **Added `--steady [N]`**: warm each copy once
untimed, then time N passes over the settled file and take the median.

| harness | agent tier median |
|---|---:|
| `steady.py --only agent --repeat 3` | 16.2 ms |
| `aot --only agent --steady 3` | **16.3 ms** |

Agreement to 0.1 ms, with 77/77 and 174/174 files verified still at rest. Full
corpus steady: mean 22.5 ms, median 17.6 ms, p95 44.7 ms, floor 14.4 ms.

**Which number to quote.** Steady state is what a user waits for on almost every
prompt, so ~17 ms is the honest headline. Cold (~59 ms median, ~84 ms mean) is the
worst case, paid once per session start or resume. Recall that `claudinine version`
alone is ~12-15 ms: in steady state process startup is most of the cost, so
optimizing the pass has little left to win there. The decode work identified in the
first profile pays off in the COLD pass, which is where the remaining time is.

In steady mode the report replaces the byte-reduction line with an `at rest` count —
a byte delta there would mean the premise broke, not that compaction went well.

### Detection trap found and fixed while doing this

Newest-first picked `src/Claudinine/bin/Release/net10.0/win-x64/native/claudinine.exe`
— the ILCompiler intermediate, which is a genuine AOT binary and so passed the
>1 MB apphost filter. It was the 2,763,776-byte **rejected** trimming experiment
(`StackTraceSupport=false` + `UseSystemResourceKeys=true`) from the section above,
left behind by a command-line publish. The verb was silently benchmarking a binary
that was deliberately not shipped. `FindAotBinary` now skips any directory named
`native`.

## 2026-08-14 — can process startup be reduced? No: it is not ours

Steady state is ~17 ms/invocation and `claudinine version` is ~12 ms of that, so
startup looked like the obvious remaining target. It is not — the cost does not
belong to the binary.

Measured with a dedicated spawn harness (`Process.Start` + drain + `WaitForExit`,
80 iterations, medians), against a Native AOT executable whose entire body is
`return 0`, published with the same properties as the real one:

| target | min | median | p90 |
|---|---:|---:|---:|
| empty AOT exe (`return 0`) | 10.4 | 11.3 | 12.0 ms |
| `claudinine.exe version` | 9.6 | **10.9** | 11.9 ms |

**The real binary is not slower than a do-nothing AOT binary of the same shape.**
Cross-checked from PowerShell, where `cmd /c rem` — an unrelated process — costs
11.3 ms median. So ~11 ms is the Windows process-creation floor on this machine and
our managed startup is ~0 on top of it. There is nothing in `Program.cs` to trim:
the entry point is a `switch` over `args` with no DI, config, or logging init, and
`Dbg.Enabled` is a single `GetEnvironmentVariable`.

Not a first-touch antivirus scan either. Defender real-time protection is enabled
(`DisableRealtimeMonitoring: False`), but freshly-copied never-before-executed
binaries spawn at the same speed as warm ones (11.5 / 11.3 / 11.3 ms), so the cost
is the steady per-spawn path, not image scanning.

### What this rules out

Ideas that cannot help, so they should not be tried:

- **Smaller binary.** The 816 KB empty exe and the 3.06 MB real one spawn at the
  same speed. Confirms the trimming-switch conclusion from a second direction:
  those 250 KB were never going to buy latency.
- **Trimming managed startup.** Already ~0.
- **`OptimizationPreference` / GC flags.** Startup was equal across every variant
  measured earlier.

### What could actually help, none of it in this project

The only real lever is **spawning fewer processes**, which is the app's call, not
the plugin's: hooks are invoked one process per event by design. A persistent
daemon that hooks talk to over a pipe would amortize the 11 ms, but that trades a
stateless, crash-safe design for a resident process, and the mirror-first invariant
is much easier to reason about when every invocation is independent. Not worth it
for 11 ms.

Environment-side, a Defender exclusion for the plugin binary might shave part of the
floor, but it needs admin rights, cannot be shipped, and would be measuring the
user's machine rather than the code.

### Where the remaining time actually is

Steady state ~17 ms = ~11 ms OS floor + ~6 ms of pass. The pass half is nearly
floor-bound already. The cold pass (~59 ms median, ~84 ms mean) is where the ~48 ms
of real work lives, and that is where the `TryGetValue` decode traffic from the
first profile pays off. **Optimization effort belongs in the cold pass, not in
startup.**

### Follow-up: 11 ms is not AOT startup, it is this machine's spawn path

11 ms did look wrong — published AOT startup is usually 1-3 ms — so it was worth
decomposing rather than accepting. It is not AOT, and not our binary.

**1. The time is real, not harness overhead.** A binary that self-reports
`GetProcessTimes` creation timestamp → `Main` measures ~9.7 ms median internally,
with no external timer involved.

**2. But it burns no CPU.** Same measurement, reading kernel and user CPU at entry:

    wall (create -> Main)   kernel CPU   user CPU
    median   9.67 ms          0.00 ms     0.00 ms

Zero CPU on both counters. The process is not initializing during those 9.7 ms, it
is **blocked**. That rules out AOT runtime init, static constructors, and image
loading as the cause — all of those would burn user CPU.

**3. It is not .NET at all.** Spawning `C:\Windows\System32\where.exe` — a plain
native Win32 binary, nothing to do with .NET — from the same harness:

| target | median | min |
|---|---:|---:|
| `where.exe` (native Win32) | **47.21** | 44.94 ms |
| empty AOT exe | 12.04 | 10.90 ms |
| `claudinine.exe version` | **11.33** | 10.52 ms |

Our AOT binary is **4× faster to spawn than a Microsoft-shipped native exe** on this
machine. Whatever the ~10 ms floor is, AOT is already at the good end of it.

**4. Fixed per launch, not a caching artifact.** 200 back-to-back launches: first-20
median 8.66 ms, last-20 median 8.66 ms — perfectly flat, no warm-up trend. Location
moves it slightly (temp 9.14 ms vs user profile 8.24 ms). Consistent with a
per-launch filter driver (Defender real-time monitoring is enabled; it is the only
AV present) rather than anything cacheable. The earlier fresh-copy-vs-warm-copy test
was the wrong probe for this: a per-launch hook charges every launch equally, so
identical timings there did not exonerate it.

**Conclusion unchanged, reasoning corrected.** Nothing to optimize in the binary —
managed startup is ~0 and the AOT image already spawns faster than native system
exes here. The ~10 ms is environmental (OS + security filter), it would be lower on
a machine without real-time scanning, and it is not something the plugin can or
should try to fix. The earlier note framed this as "the Windows process-creation
floor"; more precisely it is *this machine's* floor, and a CI runner or a
Defender-excluded path would likely show less.

### Correction: the endpoint agent is Cortex XDR, not Defender

The section above attributed the per-launch block to Defender real-time monitoring.
Wrong. Defender is **not** the active engine on this machine — Palo Alto Cortex XDR
is. `Get-MpPreference`'s `DisableRealtimeMonitoring: False` reports Defender's
*setting*, not whether Defender is the engine actually enforcing anything, and
`root/SecurityCenter2` listed only "Windows Defender" because XDR's filter driver
does not register there the way an AV product does.

This does not change the measurements or the conclusion — a process-creation filter
driver blocks the same way whoever ships it, and the zero-CPU-against-9.7 ms-wall
signature stands. It does change the attribution, and it means the "a Defender
exclusion might shave the floor" remark is moot: any exclusion would have to be
configured in XDR, which is centrally managed.

Because the floor is an artifact of this machine's endpoint agent, the local numbers
cannot serve as the AOT baseline. `.github/workflows/startup-baseline.yml` (throwaway,
`workflow_dispatch` only) measures `claudinine version` against an empty AOT binary
and a native exe on all six shipped RIDs, on clean runners with no endpoint agent.

Local reference for comparison, win-x64, 40 timed spawns after 20 warm-up:

| target | min | median | p90 |
|---|---:|---:|---:|
| `claudinine version` | 10.29 | 11.34 | 11.97 ms |
| empty AOT exe | 9.17 | 9.97 | 10.54 ms |
| `where.exe` (native Win32) | 43.07 | 45.14 | 49.09 ms |

Two bugs the local dry run caught before spending six runners on them, both worth
remembering for any future cross-platform probe workflow:

- A probe project directory named `nul` makes MSBuild fail with **MSB1025** on
  Windows — reserved DOS device name. Renamed to `tinyexe`.
- `File.Exists("publish/win-x64/claudinine.exe")` succeeds while
  `Process.Start` on the same relative forward-slash path throws
  **Win32Exception(2) "file not found"**: `CreateProcess` will not take it. The
  harness now calls `Path.GetFullPath` before spawning.

## 2026-08-14 — cross-platform startup baseline (CI, six RIDs)

Run 31800672920, `workflow_dispatch --ref develop`, 200 timed spawns per target
after 20 discarded warm-ups. Medians in ms:

| RID | runner | claudinine | empty AOT | **delta** | native |
|---|---|---:|---:|---:|---:|
| linux-arm64 | ubuntu-24.04-arm | 2.56 | 1.68 | **+0.88** | 0.73 |
| linux-x64 | ubuntu-latest | 3.86 | 2.49 | **+1.37** | 0.71 |
| win-x64 | windows-latest | 11.28 | 10.16 | **+1.12** | 83.61 |
| win-arm64 | windows-11-arm | 14.36 | 12.67 | **+1.69** | 71.77 |
| osx-x64 | macos-15-intel | 14.53 | 11.16 | **+3.37** | 5.93 |
| osx-arm64 | macos-latest | 22.78 | 20.92 | **+1.86** | 17.53 |

**Our binary costs ~1-3.4 ms over a do-nothing AOT exe, on every platform.** That is
the only figure here attributable to this code; everything else is the platform's
process-creation floor. Nothing to optimize — the earlier conclusion holds, now with
a real baseline instead of one machine's reading.

### Correction: the ~10 ms Windows floor is Windows, not Cortex XDR

The previous two sections blamed the local ~10 ms on an endpoint agent — first
Defender, then Cortex XDR. Both attributions were wrong.

Clean CI win-x64 with **no endpoint agent** spawns an empty AOT exe in 10.16 ms.
The local machine measured 9.97 ms for the same thing. Identical. **XDR is costing
essentially nothing**; ~10 ms is simply what Windows charges for process creation,
against 1.68-2.49 ms on Linux for the same binary.

The zero-CPU-with-9.7 ms-wall observation was accurate, and the process really is
blocked rather than computing — but that block is Windows' own creation path, not a
third-party filter. Do not re-open this looking for an AV exclusion.

The local `where.exe` control was also misleading: it costs **83.61 ms on a clean
win-x64 runner** and 71.77 on win-arm64. So the 45 ms measured locally was not
XDR overhead, it is just an expensive binary (it walks PATH). Using it as "the native
floor" understated how good AOT looks on Windows. The tiny C exe on Unix is the
honest control: 0.71 ms on linux-x64.

### Platform notes

- **Linux is ~4-6x faster to spawn than Windows** for the identical AOT binary
  (1.68 ms vs 10.16 ms empty). Purely OS, not codegen.
- **osx-arm64 is the slowest floor** (20.92 ms empty AOT, 17.53 ms native C) —
  runner virtualization, most likely; the delta over empty AOT is still only 1.86 ms.
- **`cpu` reads `n/a` on Unix**: `TotalProcessorTime` is unavailable after exit
  there. Windows legs show cpu ~= wall (12.03 vs 11.28), i.e. mostly busy, not
  blocked — a useful contrast to the pre-Main window measured locally.
- Steady-state hook latency of ~17 ms measured locally is therefore **pessimistic
  for Linux/macOS users** and roughly right for Windows. The ~11 ms Windows floor is
  most of it, and it is not ours.

Workflow deleted after recording: `.github/workflows/startup-baseline.yml` was
throwaway by construction (`workflow_dispatch` only, never referenced by ci/cd).
To re-run it, recover from git history — commit 6db28d4 on develop, or PR #7 on main.

### Why Windows is ~4-6x slower to spawn (mechanism, partly inferred)

Recorded because the numbers above otherwise look like an unexplained anomaly. The
architectural reasons are well established; the exact split for THIS binary is not
instrumented — see the caveat at the end.

**Linux has `fork()`, Windows does not.** A Linux child starts as a copy-on-write
page-table clone of the parent: no address space built from scratch, no image parsed.
`execve` then swaps the image in. Process creation is the core Unix idiom (every
shell pipeline forks), so it has been the optimized path for decades.

`CreateProcess` builds everything from nothing each time: process object, address
space, PE image mapping, import resolution, PEB/TEB setup, then loader
initialization walking `DllMain` for every dependency. Windows' design assumption is
few long-lived processes with threads for concurrency, so this was never the hot
path.

**Loader work is real even for Native AOT.** The shipped binary imports 11 DLLs
(read out of its import table): `KERNEL32`, `ADVAPI32`, `ole32`, `bcrypt`, and seven
`api-ms-win-crt-*` shims. Each is mapped, import-resolved and initialized per
launch. The empty AOT exe pays essentially the same set, which is exactly why it
also costs ~10 ms while our delta over it is only ~1 ms.

**Creation is not purely in-kernel.** `CSRSS` (the Win32 subsystem process) is
notified for each new process, and the kernel dispatches
`PsSetCreateProcessNotifyRoutine` callbacks — the AV/EDR hook. Even with no
third-party agent, Windows registers its own consumers (Defender platform, ETW,
AppLocker/WDAC, AppCompat). Cross-process IPC plus callback dispatch inside the
creation path explains wall-time-with-no-CPU: the child is blocked while work
happens in components whose CPU is charged elsewhere.

Three cross-checks from the CI data that support this reading:

- `where.exe` costs **83.61 ms** on a clean win-x64 runner. It is a small native
  binary, so the ~73 ms over an empty AOT exe is not image size — it is PATH
  traversal through Windows' I/O stack (filter drivers over NTFS).
- **osx-arm64's empty AOT is 20.92 ms, worse than Windows.** So this is not
  "Windows slow, Unix fast": macOS pays `dyld` plus code-signature validation,
  amplified by runner virtualization. **Linux is the outlier on the fast side.**
- ARM64 is slower than x64 on both Windows (12.67 vs 10.16) and macOS — runner
  hardware and emulation, unrelated to OS design.

**Caveat, deliberately not resolved:** this explains a measurement with mechanism
that was not instrumented here. Attributing the ~10 ms to specific components would
need ETW on a clean Windows box (`perfview /threadTime` with the `PROC_THREAD` and
`LOADER` providers) to separate loader time from callback dispatch. That is a
Windows-internals question, not a Claudinine one, and it does not change the
actionable split: ~10 ms belongs to the platform, ~1 ms to this code.

## 2026-08-14 — startup is not just "not ours", it is architecturally unreachable

Closes the question for good. The earlier daemon remark ("a persistent daemon that
hooks talk to over a pipe would amortize the 11 ms") overstated what a daemon could
buy, and correcting that removes the last apparent lever.

### A daemon cannot recover the floor

`hooks/hooks.json` registers `type: "command"` hooks — the only hook mechanism
Claude Code offers. It spawns **one process per event, unconditionally**. A resident
daemon would still need a spawned client per event to reach it over the pipe, and
that client pays the same ~10 ms Windows process-creation floor as `claudinine.exe`
does today (the CI baseline proved the floor is indifferent to what the process
contains — an empty AOT exe costs 10.16 ms).

So a daemon's maximum theoretical saving is the **pass** portion of a steady-state
invocation (~6 ms), not the ~11 ms floor. The floor is charged to the client spawn
either way. On top of the design costs already recorded (resident process, losing
the stateless crash-safe model, mirror-first invariant harder to reason about),
the payoff ceiling is ~6 ms per prompt. **Excluded by design decision** — do not
re-propose it.

### Hidden cost the harness does not see

Claude Code runs the hook command string through a shell, so production likely pays
a shell spawn on top of the claudinine spawn per event. The `aot` harness does a
direct `Process.Start` of the binary and never measures this. Not instrumented, and
not actionable from this codebase regardless — the shell is upstream's choice.

### The complete lever list, none worth pulling

1. **Upstream: persistent/socket hooks in Claude Code.** The only true fix, and the
   only party that can remove the per-event spawn. Feature-request territory; the
   2026-08-10 upstream survey found zero Anthropic replies on that class of issue.
2. **Register fewer events.** Dropping `UserPromptSubmit` removes the per-prompt
   spawn entirely, but trades away per-prompt compaction freshness — the product's
   point — to save ~17 ms per prompt. Bad trade.
3. **OS choice.** Linux floor is 1.7–2.5 ms. A fact, not a lever.

Binary-side levers were all measured dead earlier in these notes: size, trimming
switches, `OptimizationPreference`, GC flags — zero effect on startup; the delta
over an empty AOT exe is ~1–3.4 ms on every RID and that delta is the entire budget
this code could ever recover.

### Perspective on "startup dominates"

True but with a tiny denominator: ~11 ms of a ~17 ms steady invocation, inside a
prompt turn that costs seconds of model latency — under half a percent of what the
user actually waits for. And the macOS floor is probably pessimistic: the 20.9 ms
osx-arm64 number is GitHub's virtualized runner; bare Apple Silicon spawns in a few
ms, so real macOS users likely sit closer to Linux than to Windows.

**Verdict: startup optimization is closed.** The hook-command architecture
guarantees one process spawn per event no matter what is built on this side.
Remaining optimization budget belongs to the cold pass (~48 ms of real work).

## 2026-08-14 — harness verbs reworked: `profile`/`aot` × `--full`/`--steady`

The CLI grew organically and its vocabulary had become a trap: `run --warmup`
sounded like a warm/steady measurement but always fed pristine, uncompacted
text (cold *workload*, warm *JIT* — two different axes), while `aot` defaulted
silently to the cold pass. Reworked so the input-state axis is explicit and
shared by both verbs:

- `run` is renamed **`profile`** (it is the profiler target, not "the" run).
- Both `profile` and `aot` now REQUIRE a mode: **`--full`** (uncompacted input,
  the fresh/resumed-session worst case) or **`--steady`** (input settled by one
  untimed pass, the per-prompt common case). No default is guessed — the
  numbers differ several-fold and misquoting them is the recurring mistake this
  file documents.
- `profile --steady` is NEW: settles each file in memory (parse → rules →
  `TryComputeRewrite` text) before the measured passes, and reports an
  `at rest` count instead of a byte saving, same premise guard as `aot
  --steady` / steady.py.

Follow-up the same day: `--warmup` is REMOVED — an unmeasured warm pass now
always runs before the measured region (in steady mode the settling pass plays
that role), because a measurement polluted by first-call JIT is never the
number anyone wants — and `profile`'s `-n` default went 1 → 20, the
profiler-sized value the README always told you to pass anyway. `-n 1` still
works for a quick pass and still prints the thin-profile reminder.

Reading older entries: `run --warmup [-n N]` = today's `profile --full [-n N]`;
bare `aot` = today's `aot --full`; `aot --steady` unchanged. VS launch profile
is now just `profile --full`. Smoke-tested all four verb×mode combinations
(steady premise held 10/10 in-process, 3/3 subprocess); 275/275 tests pass.

## 2026-08-14 — headless CPU profile via dotnet-trace: GC waits are 40% of the pass

First profile taken without the VS GUI: `dotnet-trace collect -- <bench exe>
profile --full` (EventPipe sampling, ~1 kHz), converted to speedscope and
analyzed per-thread with a script. Environment: JIT bench build, **server GC**
(the bench csproj still sets it — remember the shipped binary is workstation),
21 passes over the full corpus, main thread 32.5 s attributed.

### Method trap: the raw report is unusable

`dotnet-trace report topN` mixes ALL threads and EventPipe samples blocked
threads too, so the finalizer and GC-poll threads' idle waits (~70% of samples)
drown the main thread — the naive top-5 is `GC.RunFinalizers`, `PollGCWorker`,
finalizers, and looks like a GC catastrophe it isn't. Convert to speedscope and
attribute per-thread (skipping the synthetic `CPU_TIME`/`UNMANAGED_CODE_TIME`
leaves) before believing anything. Script kept in the session scratchpad;
trivially re-writable.

### Where the main thread's time goes (share of 32.5 s, inclusive)

- **~40% parked at GC poll points** (`Thread.PollGCWorker` as self time) — the
  thread waiting for GCs it triggered, 22% of it while allocating inside
  `JsonObject.InitializeDictionary` under `TranscriptRecord.TryParse`. This is
  the "pass is GC-bound" conclusion measured directly rather than inferred from
  A/B, and it pins the allocation flood to load-parse node materialization.
- **Load parse ~32%**: `TryParseText` 32.5% = `TranscriptRecord.TryParse` 29.4%
  (of which `InitializeDictionary` 16% direct, memmove 4.4%, zeroing 2.4%) plus
  the line split 5.6%.
- **Rules ~50%** (GC waits included): tool-result-age 11.3% (Minify 4.8%),
  chain-collapse 10.7%, read-supersession 9.2%, system-reminder-dedup 5.2%,
  image-strip 4.4%, mega-block-trim 3.8%, everything else ≤2%.
- `TryComputeRewrite` 8.6%, `GetUnescapedString` 5.6% (+4.7% buffer zeroing
  under it), UTF-16 validation 3.7%.

### Two new findings, both actionable

**1. The `GetStringMemo` ConditionalWeakTable is now a top-ten cost center.**
It shows up three ways: 7.1% of the main thread inside `GetStringMemo` itself
(5.0% of that from system-reminder-dedup alone — the memo IS that rule's cost),
`Monitor.Enter_Slowpath` at 6.6% self (CWT's internal lock on every add,
contending with the finalizer thread), and **4.6 s of finalizer-thread time
finalizing CWT containers** across the run. The memo's ~12% win was real, but
CWT is an expensive way to get it: a per-pass `Dictionary<JsonNode, string>`
with a reference-equality comparer, dropped after each file, has the same
lifetime semantics (tree dies with the pass) with no lock, no GC handles, no
finalizable containers. Worth an interleaved A/B; the payload-only split stays.

**2. The Minify exception probe is no longer a rounding error.** The 2026-08-13
entry dismissed the ~50 `JsonException`/pass at ~0. At full scale it is
`ThrowJsonReaderException` → `EH.DispatchEx` 1.7% plus
`ResourceManager.GetFirstResourceSet` 1.6% (exception-message resource lookups,
which also take a lock) — ~3% combined. The `Utf8JsonReader` probe already
suggested there would erase it.

Unattributed curiosity, parked: 1.8 s of `DynamicResolver+DestroyScout`
finalizers (Reflection.Emit debris) on the finalizer thread. All our regexes
are `[GeneratedRegex]`, so the source is somewhere in the framework; finalizer
thread only, does not gate anything.

### Caveats

Server-GC JIT in one long process is NOT the shipped shape (AOT, workstation
GC, one ~50 ms process per file); the 40% GC share is inflated by 21
back-to-back full-corpus passes churning the whole 189 MB corpus. Use the
ranking and the two findings, not the absolute percentages. The earlier
interleaved A/B evidence says allocation pressure hurts in every shape, so the
interventions above should transfer — but verify each with `aot`, as always.

## 2026-08-14 — reconciling `profile --full` (~7 ms/file) with `aot --full` (~84 ms)

Same corpus, same rules, an order of magnitude apart: profile per-file mean
6.8 ms / median 2.2 ms against aot mean 83.6 ms / median 58.9 ms. Both are
right; they differ in **span** and in **environment**, and each difference is
now measured rather than assumed. Budget for the median (450 KB) file's cold
`aot` invocation of ~59 ms:

| component | ~ms | evidence |
|---|---:|---|
| process creation | 11 | `claudinine version`, CI baseline |
| **filter-driver rescan on first read** | **14** | probe below |
| mirror append (450 KB) + fsync | 2.5 | probe at median volumes |
| rewrite (~104 KB) + fsync + rename | 2.5 | probe at median volumes |
| the pass, in production conditions | ~29 | remainder; see multiplier below |

`profile` measures ONLY the last row's algorithmic content — by design (text
pre-loaded, no mirror, no file writes; `Harness.SerializeAndValidate` stops
before the file half). Everything else is span that `profile` deliberately
excludes. The environment then multiplies the pass row itself: a fresh process
pays heap growth from zero and first-touch page faults, AOT-`Size` codegen has
no dynamic PGO, and the JIT number comes from a 20-iteration-warmed server-GC
process. The multiplier is visible on the settled pass too: `aot --steady`
minus startup leaves ~5-6 ms where the warmed in-process equivalent is
~0.4 ms — same ~10x, so the cold ratio (29 vs 2.2 ms) is the same effect, not
a mystery.

### New finding: the endpoint agent taxes file READS ~14 ms, not spawns

Measured with a Python probe in the temp dir (medians of 25):

| operation | median |
|---|---:|
| read an unchanged file | 0.18 ms |
| read after copy/write/1 KB-append (any modification) | **13-15 ms** |
| same, 8 MB / 15 MB file | 21 / 25 ms |
| 450 KB append + fsync | 2.5 ms (0.4 without fsync) |

The rescan-on-modified-read is flat ~14 ms up to ~2 MB. This CORRECTS the
scope of the earlier "XDR is costing essentially nothing" conclusion — that
was measured for process CREATION only. File reads are a separate channel,
and production pays it on every prompt: the app appends to the transcript,
then the hook opens it. On a machine without an endpoint agent the whole gap
narrows by that much.

Consequence for the harness: **`aot --steady` slightly understates the real
per-prompt cost on scan-taxed machines.** Its warm pass rewrites the file, so
only timed pass 1 pays the rescan and the median of 3 excludes it — while
production, where the file changes between every pair of hook invocations,
pays it every time. Real per-prompt latency here is closer to steady + ~14 ms
than to steady.

Two fsyncs per invocation (`Jsonl.cs` mirror append + atomic rewrite, both
`Flush(flushToDisk: true)`) cost ~4-5 ms combined at median volumes. They are
the durability the mirror-first invariant exists for; noted as a component,
not a target.

## 2026-08-14 — static PGO for the AOT binary: considered, rejected

Prompted by the JIT-vs-AOT multiplier above ("the JIT number benefits from
dynamic PGO"). Three independent reasons, any one of which suffices:

1. **It does not work.** Dynamic PGO is JIT-only (needs tiering to instrument
   and recompile). The AOT equivalent — a `.mibc` profile fed to ILCompiler —
   is a real pipeline for ReadyToRun/crossgen2 but NOT for Native AOT:
   dotnet/runtime#95236 reports MIBC passed to ILC is silently ignored, and
   there is no supported publish property. Still true as of .NET 10.
2. **Our own A/B bounds the gain at ~zero.** Speed vs Size vs default, all
   confounds removed (settled entry above): within noise. PGO is a subtler
   codegen lever than `OptimizationPreference=Speed`; it will not beat a knob
   that measurably did nothing.
3. **The pass is not codegen-bound.** ~40% GC waits, plus memmove / zeroing /
   UTF validation — intrinsic and native helpers PGO cannot touch. The
   fresh-process ~10x multiplier applies equally to the settled pass, which
   runs almost no rule logic — it is heap growth and page faults, not
   instruction selection.

If a codegen lever is ever wanted, the one with real headroom is
`IlcInstructionSet` (AOT compiles to a conservative x64 baseline; the JIT uses
the machine's full ISA, and the profile's UTF helpers are SIMD-sensitive) — a
compat decision (raises the CPU floor on six RIDs) for a bounded few percent.
Parked. Optimization budget stays on allocation reduction: memo storage,
exception probe, read-layer refactor.
