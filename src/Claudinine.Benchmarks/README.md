# Claudinine.Benchmarks

Performance measurement for the compaction pass — **how fast**, not how much it
saves. The effectiveness number (token reduction) is a different tool entirely:
`eng/bench/compare.py`. Do not conflate the two, and do not quote a byte
percentage from here as a context saving.

Three entry points, for three different questions. `profile` and `aot` both
require a mode, and the two modes share one vocabulary:

- **`--full`** — uncompacted input. The workload of a fresh or resumed session;
  the worst case, and where the compaction work actually lives.
- **`--steady`** — input settled by one untimed pass first. The workload of
  prompt N once 1..N-1 are compacted; the common case, several times faster.

The mode is required rather than defaulted because the numbers differ
several-fold and quoting one for the other is the recurring trap in
`eng/bench/profiling-notes.md`. (Entries there predating the rename say
`run --warmup` — that is today's `profile --full`.)

## `profile` — full-corpus pass in-process, the profiler target

```bash
dotnet run -c Release --project src/Claudinine.Benchmarks -- profile --full
```

Compacts all 174 corpus transcripts in ONE process: no child processes, no
generated runner assembly, no JIT-warmup orchestration. That is what makes it
usable under a sampling profiler — every sample lands in our own call tree,
attributable to a rule.

`profile --steady` settles each file in memory first (parse, apply rules, take
the rewritten text), then measures passes over that settled text — useful for
profiling what a per-prompt invocation actually does. The report replaces the
byte-saving line with an `at rest` count: if files are still shrinking during
timed passes, the steady premise broke and the report says so.

An unmeasured warm-up pass always runs before the measured region, so
first-call JIT compilation is never attributed to whichever rule happened to
execute first (in `--steady` mode the settling pass plays that role). There is
no flag for it: a measurement polluted by first-call JIT is never the number
anyone wants.

### Under the Visual Studio profiler

1. Set **Claudinine.Benchmarks** as the startup project.
2. Set its launch arguments to **`profile --full`** (the `-n 20` default is
   already profiler-sized, see below).
3. **Debug > Performance Profiler** (`Alt+F2`), tick **CPU Usage**, then Start.
4. Build configuration must be **Release** — the project defaults to it, so
   `dotnet run` without `-c` is already correct here, but the VS configuration
   dropdown is per-solution and needs setting once.

When run in a real console the summary ends with "Press any key to close…", so a
profiler-launched window does not vanish before you have read it. The wait is
skipped automatically when stdin or stdout is redirected, so pipes and scripts
never hang on it.

### Why the default is `-n 20`

One pass over the whole corpus is only ~2.7 s of CPU — **too thin to profile**.
A ~1 kHz sampling profiler collects a few thousand samples from that, and since
`chain-collapse` alone takes roughly half of them, every cheap rule lands inside
the noise band and their relative ranking is not trustworthy.

20 iterations give ~44 s of measured CPU (~44k samples), enough that the small
rules are statistically solid. The corpus is read into memory once before the
measured region, so extra iterations are pure compute and add no disk time to
the profile. Pass `-n 1` for a quick single pass — the CLI prints a
thin-profile reminder when you do.

Note that `pass time` and the per-file stats describe the **last** iteration —
the fully warmed one ("steady" is reserved for the input mode, a different
axis). `wall clock` covers all iterations, so at `-n 20` the two legitimately
differ by ~20x.

Options: `-n/--iterations N` (repeat the corpus, default 20), `--limit N` (only
the N smallest files — a fast edit/profile loop), `--only main|agent`,
`-v/--verbose` (per-file lines).

Output reports mean/median/p95/max per file, throughput, and the five slowest
files, so a regression shows up as a shifted tail rather than a moved average.

## `aot` — wall clock of the shipped binary

```bash
dotnet run -c Release --project src/Claudinine.Benchmarks -- aot --full
```

The only measurement here that sees what a user actually waits for. `profile`
and `bench` both time JIT-compiled code in a warm, long-lived process;
production spawns the Native AOT binary once per hook event, feeds it a JSON
payload on stdin, and it exits. This verb reproduces that: one subprocess per
invocation, process startup included.

`aot --full` gives every invocation a pristine, uncompacted copy. `aot --steady
[N]` warms each copy once untimed, then times N passes (default 3) over the
settled file and reports the median — the same method as `eng/bench/steady.py`,
and the two agree to ~0.1 ms.

It times two events, because they do different amounts of work —
`UserPromptSubmit` (the per-prompt critical path, session file only) and
`SessionStart` (adds the subagent sweep, mirror GC and session-dir GC). Use
`--event` for one of them.

Needs an AOT binary. Auto-detection takes the newest one under `publish/` or
`src/Claudinine/bin/Release/`, ignoring anything under 1 MB — the
framework-dependent apphost of the same name is ~162 KB against ~3.0 MB for a
real AOT build, and timing it would measure a `dotnet` launch instead of the
shipped artifact. There is deliberately **no JIT fallback**: silently answering a
different question is worse than failing. Point `--exe` at a release archive or
the plugin cache when a local publish is not available (AOT needs the C++
workload for the platform linker; see `eng/bench/profiling-notes.md` if it fails
on your machine).

Never touches `bench/corpus/`: every invocation gets a pristine copy in a temp
workspace, and `CLAUDE_PLUGIN_DATA` is redirected there too so mirrors never
reach the real pool. `--keep` retains the workspace for inspection.

The reported `floor` is the smallest session, **not** process startup — measure
that separately with `claudinine version` (~12 ms here) and see the notes file
before drawing conclusions from it.

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

Profiling findings are recorded in `eng/bench/profiling-notes.md`, next to the
rest of the benchmark tooling — not here.

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
change help — and `profile` under a profiler for the shape of the real thing.

## Corpus

Both entry points need `bench/corpus/`, which is **gitignored real session data**
(see `eng/bench/README.md`). Build it with `python eng/bench/curate.py`.

`PipelineBenchmarks` selects its inputs by percentile — small (p10), median,
largest, plus a median subagent file — rather than hard-coding session ids, which
would be both meaningless to a reader and private. A subagent transcript is one
long uninterrupted tool chain and compacts very differently from a main session,
so it is worth keeping visible as its own case.
