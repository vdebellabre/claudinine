# Claudinine.Benchmarks

Performance measurement for the compaction pass — **how fast**, not how much it
saves. The effectiveness number (token reduction) is a different tool entirely:
`eng/bench/compare.py`. Do not conflate the two, and do not quote a byte
percentage from here as a context saving.

Two entry points, for two different questions.

## `run` — full-corpus pass, the profiler target

```bash
dotnet run -c Release --project src/Claudinine.Benchmarks -- run --warmup
```

Compacts all 174 corpus transcripts in ONE process: no child processes, no
generated runner assembly, no JIT-warmup orchestration. That is what makes it
usable under a sampling profiler — every sample lands in our own call tree,
attributable to a rule.

### Under the Visual Studio profiler

1. Set **Claudinine.Benchmarks** as the startup project.
2. Set its launch arguments to `run --warmup` (add `-n 3` for a longer sample).
3. **Debug > Performance Profiler** (`Alt+F2`), tick **CPU Usage**, then Start.
4. Build configuration must be **Release** — the project defaults to it, so
   `dotnet run` without `-c` is already correct here, but the VS configuration
   dropdown is per-solution and needs setting once.

`--warmup` runs one unmeasured pass first so first-call JIT compilation is not
attributed to whichever rule happened to execute first. Use it whenever the
profile is what you care about.

Options: `-n/--iterations N` (repeat the corpus), `--limit N` (only the N
smallest files — a fast edit/profile loop), `--only main|agent`, `-v/--verbose`
(per-file lines).

Output reports mean/median/p95/max per file, throughput, and the five slowest
files, so a regression shows up as a shifted tail rather than a moved average.

## `bench` — BenchmarkDotNet, statistically rigorous

```bash
dotnet run -c Release --project src/Claudinine.Benchmarks -- bench
```

Slow (tens of minutes for the full matrix). Filter while iterating:

```bash
dotnet run -c Release --project src/Claudinine.Benchmarks -- bench --filter *RuleBenchmarks* --job short
```

Extra arguments pass straight through to BenchmarkDotNet (`--filter`, `--job`,
`--list`, `--exporters`, …).

- **`PipelineBenchmarks`** — parse / rules / full pass, across four size tiers.
  Answers "what does a pass cost, and how does it scale with session size".
- **`RuleBenchmarks`** — one case per rule in `RuleCatalog.All`. The ranking that
  tells you where to optimize.

## Things that are load-bearing, and were learned the hard way

**Do not rename the assembly.** `AssemblyName` must stay `Claudinine.Benchmarks`.
BenchmarkDotNet's toolchain locates the project by assuming the output exe is
named after the `.csproj`; renaming it to `claudinine-bench` made every single
benchmark fail with *"Unable to find … Most probably the name of output exe is
different than the name of the .(c/f)sproj"* — and it fails as an empty results
table, not as an error you can miss reading.

**Every iteration re-parses from a cached string.** Rules mark records via
`Replacement`/`Removed`, so reusing one `TranscriptFile` would have iteration 2
measuring an already-compacted input — the wrong workload, and silently much
faster. The text is read from disk once in `[GlobalSetup]`, so no benchmark
measures disk time or OS cache state. `PipelineBenchmarks`' "parse only" case
exists so the parse cost can be subtracted when reading the rule numbers.

**Each rule is measured in its catalog position.** `RuleBenchmarks` re-runs every
preceding rule in `[IterationSetup]` (unmeasured) before timing rule N. The
catalog order is deliberate and later rules read earlier rules' pending edits
through `RuleHelpers.CurrentNode`, so timing a rule against a pristine file would
measure a workload that never occurs — generally a larger one, since nothing has
shrunk yet.

**Nothing here writes to the corpus.** `Harness.SerializeAndValidate` does the
compute half of `TranscriptFile.TryRewrite` — build the text, re-parse to
validate — but never the atomic file swap. The corpus is the fixed, private,
hard-to-rebuild baseline the effectiveness numbers depend on; a benchmark that
compacted it in place would destroy it on first run.

**No synthetic fallback.** A missing corpus prints how to build one and exits 1.
Synthetic transcripts compact at rule hit-rates unlike anything real, so a number
measured on them is not comparable to a real one — and a fallback that silently
switched between the two would be worse than no number at all.

**BDN numbers are not AOT numbers.** The shipped binary is Native AOT with
`OptimizationPreference=Size`; BenchmarkDotNet measures JIT-compiled managed
code and cannot host an AOT build (it must emit and compile a runner assembly).
Use these numbers for *relative* comparisons — which rule dominates, did this
change help — and `run` under a profiler for the shape of the real thing.

## Corpus

Both entry points need `bench/corpus/`, which is **gitignored real session data**
(see `eng/bench/README.md`). Build it with `python eng/bench/curate.py`.

`PipelineBenchmarks` selects its inputs by percentile — small (p10), median,
largest, plus a median subagent file — rather than hard-coding session ids, which
would be both meaningless to a reader and private. A subagent transcript is one
long uninterrupted tool chain and compacts very differently from a main session,
so it is worth keeping visible as its own case.
