# Benchmark corpus

`bench/` is **gitignored and must stay that way**. It holds real session
transcripts — private conversation data, client names, source code. Never commit
it, never attach it to an issue, never publish a file from it.

## Why a snapshot

Benchmarks used to read `~/.claude/projects/**` in place. That is unusable for a
published number, for two independent reasons:

- **The population drifts.** Sessions get deleted or age out. Between
  2026-08-09 and 2026-08-12, 19 of 95 measured files disappeared — so a rerun
  silently measures a different corpus and no two numbers are comparable.
- **Live files are contaminated.** Claudinine compacts sessions in normal use, so
  its own past work is baked into what you would call the "baseline". Those files
  score ~0% for Claudinine while the other tool gets credit for shrinking an
  already-reduced file. **Benchmarking live biases against Claudinine.**

So the corpus is snapshotted once, with provenance recorded, and every future run
measures that fixed set.

## Curating

```bash
python eng/bench/curate.py --dry-run
```

Classifies every session and reports what it would keep, without copying. Then:

```bash
python eng/bench/curate.py
```

Copies the chosen baselines into `bench/corpus/{main,agent}/` and writes
`manifest.json`. Sources are opened read-only and never modified.

Options: `--projects` (session store), `--out` (destination), `--min-tokens`
(default 1000; `0` keeps tiny sessions).

## The fair-baseline rule

A baseline must be an **uncompacted** transcript. Each candidate is classified by
the markers it carries and handled accordingly:

| classification | detection | baseline used |
|---|---|---|
| **raw** | no marker from either tool | the file itself |
| **claudinine** | `claudinine` envelope key, or the chain-collapse carrier prefix | the session **mirror**, if it covers the session — else skip |
| **cozempic** | `[cozempic…]` text in content (it writes no envelope key) | its timestamped `.jsonl.bak`, if present and itself clean — else skip |
| **both** | both of the above | the mirror, same tests as `claudinine`; a clean covering mirror predates cozempic's pass and is valid |

Two subtleties, both learned by getting them wrong first:

**Mirror coverage is a uuid-set test, not a size test.** A mirror holding pristine
full outputs for 56 of 71 records is *smaller* than the compacted file whose 15
uncovered records are still verbatim — using it would silently drop those records
from the baseline. A mirror can also legitimately be far larger, since it
accumulates across resumes. Only size-independent coverage is meaningful.

**Stubs are expected to be absent from the mirror.** `MirrorFile` skips records
that already carry the `claudinine` key, because the original each one replaced
was mirrored under that same uuid on the pass that created it. Requiring stub
uuids to reappear rejects every healthily-mirrored session. Only records the live
file still holds **verbatim** must be present.

Also note that `mirrorOf`, `mergedFromFork`, `skipCompactionOf` and `loadStampOf`
are mirror **bookkeeping** (`MirrorFormat.Line`), not evidence of compaction — only
a `rule`-stamped record means compacted content, and that must never appear in a
baseline.

**A mirror is not a transcript.** Those bookkeeping lines have to be stripped when
copying a mirror into the corpus, and not merely for tidiness:
`TranscriptFile.IsSidechainFile` is true only if EVERY record carries
`isSidechain: true`, so a single `mirrorOf` header at the top declassifies a
subagent transcript as a MAIN one — which silently disarms chain-collapse on
exactly the files it is strongest on. Measured cost of getting this wrong: 6 agent
baselines scored **0.0%** instead of ~75%, with exit code 0 and no error anywhere.
`curate.py` strips bookkeeping lines and then asserts that every file under
`agent/` is still fully sidechain-flagged, aborting rather than shipping a corpus
that would quietly under-report.

## Comparing

```bash
uv run --with tiktoken python eng/bench/compare.py
```

Needs the release binary (`pwsh eng/publish-win.ps1` → `publish/win-x64/`) and, for
the cozempic column, a checkout at `~/source/cozempic-quiet` (`--cozempic` to point
elsewhere). Writes `bench/results.json` and prints the README table.

Options: `--rx` (cozempic prescription, default `aggressive` — its strongest),
`--only main|agent`, `--jobs`, `--report-only` (re-print from an existing results
file without re-running).

Each file is copied twice, once per tool, into separate work dirs so neither tool
sees the other's output. Both are measured with the same ruler: cl100k_base over
payload text — text and thinking blocks, tool_result content, tool_use input —
with the JSON envelope excluded, since that is not what the model is billed for.
Byte percentages understate the token saving by roughly 3.5x through envelope
dilution, so the token column is the one that supports a context claim.

Two guards worth knowing about:

- **Idempotence.** Every Claudinine file gets a second pass whose output must be
  byte-identical. A rewrite that keeps shrinking on re-run is a bug, and would
  otherwise silently flatter the score.
- **A harness failure counts as zero saving** rather than dropping the row, so a
  crash can never be mistaken for a win. Errors are printed above the table.

Wall-clock is reported but is not like-for-like: Claudinine is one native process
per file, cozempic pays Python startup per file.

## Manifest

`bench/corpus/manifest.json` records, for every scanned session: its
classification, the decision (`keep`/`skip`) with reason, the baseline path and
kind (`raw`/`mirror`/`bak`), and the `sha256` of the copy. That makes a result row
traceable to its source and lets you re-verify the snapshot has not drifted.

`main/` and `agent/` keep main and subagent transcripts separable — a subagent run
is one long uninterrupted tool chain, so it compacts very differently and is worth
reporting on its own.

## Re-snapshotting

Rerunning `curate.py` rebuilds the corpus from the current session store, which
**changes the population** and breaks comparability with earlier numbers. Do it
deliberately, and re-measure every tool afterwards — never compare a number from
one snapshot against a number from another.
