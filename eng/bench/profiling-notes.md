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
