"""Head-to-head: Claudinine vs cozempic over the curated corpus.

Run `eng/bench/curate.py` first to snapshot `bench/corpus/`; this measures that
fixed snapshot so successive runs are comparable. See eng/bench/README.md.

METHOD
------
Each transcript is copied TWICE, once per tool, into separate work dirs, so
neither tool ever sees the other's output. Baseline, Claudinine result and
cozempic result are then measured with the SAME ruler:

  bytes  — file size on disk
  tokens — cl100k_base BPE over the *payload text* of each record: text and
           thinking blocks, tool_result content, tool_use input. The JSON
           envelope (uuid, timestamp, parentUuid…) is EXCLUDED, because that is
           not what the model is billed for. Byte percentages understate the
           token saving by roughly 3.5x through envelope dilution, which is why
           the token column is the honest one for a context claim.

Claudinine is driven through its SessionStart hook — its only whole-file entry
point, and the one a benchmark can invoke deterministically. Each file also gets
a SECOND pass whose output must be byte-identical, so a non-idempotent rewrite
fails loudly here instead of silently inflating the score. Cozempic runs
`treat -rx <prescription> --execute`; its `.bak` siblings are removed before
measuring so its own backup never counts toward its result.

FAIRNESS NOTES
--------------
* Claudinine finds subagent files itself from the session directory; cozempic has
  no session-directory concept, so agent transcripts are handed to it explicitly.
  Both therefore see every file.
* Wall-clock is reported but is not a like-for-like comparison: Claudinine is one
  native process per file, cozempic pays Python interpreter startup per file.

USAGE
    uv run --with tiktoken python eng/bench/compare.py
    uv run --with tiktoken python eng/bench/compare.py --rx standard --jobs 4
    uv run --with tiktoken python eng/bench/compare.py --only main
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import statistics
import subprocess
import sys
import time
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import tiktoken

ENC = tiktoken.get_encoding("cl100k_base")

REPO = Path(__file__).resolve().parents[2]
DEFAULT_CORPUS = REPO / "bench" / "corpus"
DEFAULT_WORK = REPO / "bench" / "work"
DEFAULT_OUT = REPO / "bench" / "results.json"
DEFAULT_CLN = REPO / "publish" / "win-x64" / "claudinine.exe"
DEFAULT_COZ = Path.home() / "source" / "cozempic-quiet"


# ---------------------------------------------------------------- measurement
def _blocks(content):
    if isinstance(content, str):
        yield content
        return
    if not isinstance(content, list):
        return
    for b in content:
        if not isinstance(b, dict):
            if isinstance(b, str):
                yield b
            continue
        t = b.get("type")
        if t == "text" and isinstance(b.get("text"), str):
            yield b["text"]
        elif t == "thinking" and isinstance(b.get("thinking"), str):
            yield b["thinking"]
        elif t == "tool_use":
            yield json.dumps(b.get("input", ""), ensure_ascii=False)
        elif t == "tool_result":
            yield from _blocks(b.get("content"))


def payload_text(path: Path) -> str:
    """Tokens the MODEL sees — `message.content` blocks and nothing else.

    Verified against the CLI bundle (11.8 MB, npm @anthropic-ai/claude-code): the
    only two record→API converters both build the request from `message.content`
    alone —

        function a_z(A,...) { ... return {role:"user",      content: A.message.content} }
        function o_z(A,...) { ... return {role:"assistant", content: A.message.content} }

    Three envelope keys were previously counted here and are NOT API-visible:

    * `toolUseResult` — a top-level sibling of `message.content` holding the same
      tool output again (plus Edit `structuredPatch`/`originalFile` diff data the
      inline block omits). Never referenced by either converter; of 39 occurrences
      in the bundle, 8 are UI rendering and 20 are record construction, and the
      display path `renderToolResultMessage` appears 35 times. Every record
      carrying it also carries an inline `tool_result` block, so the API always
      has its copy. This was NOT a rounding error: the field is ~47% of measured
      payload on tool-heavy sessions, and cozempic's `tool-use-result-strip`
      deletes it wholesale — scoring 61.2% on 03ea3e5e while cutting
      `message.content` by 0.0%. Counting it credited both tools for tokens the
      model never reads.
    * top-level `content` — only on `queue-operation` and `system` records, which
      carry no `message` at all and are written straight to disk (`enqueueWrite`),
      never converted.
    * `summary` — absent from this corpus entirely; compact summaries carry their
      text in `message.content`, already counted below.

    Records before the last `compact_boundary` are excluded, except any the
    boundary names as preserved. The app reconstructs context by slicing from the
    last boundary — verified in the bundle:

        function F_z(A){for(let q=A.length-1;q>=0;q--){if(A[q]&&CR(A[q]))return q}return -1}
        function tN(A){let q=F_z(A);if(q===-1)return A;return A.slice(q)}   // CR = is compact_boundary

    so pre-boundary records are permanently out of context and compacting them is
    token-free by construction. Counting them was the single worst distortion in
    the corpus: on d8aa7b17 the boundary sits at record 4060 of 5312, putting 70.1%
    of the file's API-visible text behind it. Cozempic deletes that region
    wholesale, which scored it 75.1% against our 50.0% — measuring a whole-file
    ruler. Live-context: 52.5% for us, 17.4% for cozempic, i.e. the loss was an
    artifact and the file is a 35-point win.

    `preservedMessages.allUuids` records are added back because they load alongside
    the summary. Two cautions, both verified on the real 2.1.217 boundary in this
    corpus: the uuids sit BEFORE the boundary in file order (6 of 8 here, at
    4043–4050), and the app names uuids it never wrote (2 of 8 absent from the
    file), so a missing one is normal and must not be treated as an error.

    Byte totals still reflect the whole file, which is the right measure for disk
    and mirror cost. Only the token column is API-visible.
    """
    records = []
    with path.open("r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except Exception:
                # An unparseable line is not an API message; count nothing.
                continue

    cut, preserved = 0, set()
    for i, rec in enumerate(records):
        if rec.get("type") == "system" and rec.get("subtype") == "compact_boundary":
            cut = i
            meta = rec.get("compactMetadata") or {}
            pm = meta.get("preservedMessages") or {}
            preserved = set(pm.get("allUuids") or [])

    out = []
    for i, rec in enumerate(records):
        if i < cut and rec.get("uuid") not in preserved:
            continue
        msg = rec.get("message")
        if isinstance(msg, dict):
            out.extend(_blocks(msg.get("content")))
    return "\n".join(out)


def measure(path: Path) -> tuple[int, int]:
    if not path.exists():
        return (0, 0)
    return (path.stat().st_size,
            len(ENC.encode(payload_text(path), disallowed_special=())))


# ------------------------------------------------------------------- runners
def run_claudinine(copy: Path, cfg: dict) -> str:
    """Drive the SessionStart hook against `copy`.

    A subagent transcript is never named by a hook payload: it gets no hook events
    of its own, and HookRunner.CompactSubagents finds it by walking
    `<session-dir>/<session-stem>/subagents/agent-*.jsonl` from the MAIN transcript
    path. So handing an agent file directly as `transcript_path` compacts nothing —
    it is not where the sweep looks. stage_for_hook() puts agent files in that
    layout and returns the main path to name; see its docstring.
    """
    data = copy.parent / "_plugindata"
    data.mkdir(parents=True, exist_ok=True)
    payload = json.dumps({
        "hook_event_name": "SessionStart",
        "transcript_path": str(cfg["hook_path"]),
        "session_id": Path(cfg["hook_path"]).stem,
        "cwd": str(copy.parent),
    })
    env = dict(os.environ, CLAUDE_PLUGIN_DATA=str(data))
    try:
        p = subprocess.run([cfg["cln"], "hook"], input=payload, text=True,
                           capture_output=True, timeout=600, env=env,
                           encoding="utf-8", errors="replace")
        return "" if p.returncode == 0 else f"rc={p.returncode} {(p.stderr or '')[:200]}"
    except subprocess.TimeoutExpired:
        return "timeout"
    except Exception as e:
        return f"{type(e).__name__}: {e}"[:200]


def run_cozempic(copy: Path, cfg: dict) -> str:
    env = dict(os.environ,
               COZEMPIC_NO_AUTO_INIT="1", COZEMPIC_NO_GLOBAL_INIT="1",
               COZEMPIC_NO_AUTO_UPDATE="1", PYTHONIOENCODING="utf-8")
    try:
        p = subprocess.run(
            ["uv", "run", "--quiet", "--project", cfg["coz"],
             "python", "-m", "cozempic", "treat", str(copy.resolve()),
             "-rx", cfg["rx"], "--execute"],
            capture_output=True, text=True, timeout=1800, env=env,
            cwd=cfg["coz"], encoding="utf-8", errors="replace")
        if p.returncode != 0:
            return f"rc={p.returncode} {((p.stderr or '') + (p.stdout or ''))[:300]}"
        return ""
    except subprocess.TimeoutExpired:
        return "timeout"
    except Exception as e:
        return f"{type(e).__name__}: {e}"[:200]


def stage_for_hook(d: Path, src: Path, kind: str) -> tuple[Path, Path]:
    """Lay a corpus file out the way the product expects, and say what to name.

    Returns (file_to_measure, path_to_pass_as_transcript_path).

    main:  the copy itself is the session transcript — measure it, name it.
    agent: HookRunner.CompactSubagents derives the sweep directory from the MAIN
           transcript path, as `<dir>/<main-stem>/subagents/`. So the agent file is
           placed there and a stub main transcript is named instead. The stub is
           deliberately inert (one user record, no tool calls, so no rule fires on
           it) and is never measured — only the agent file is.

    Getting this wrong is silent: naming an agent file directly as transcript_path
    exits 0 and changes nothing, which reads as "compacts badly" rather than
    "was never visited" (measured: 12 agent files reported 0.0%).
    """
    if kind == "main":
        copy = d / src.name
        shutil.copy2(src, copy)
        return copy, copy

    main_stem = "benchmain"
    main_path = d / f"{main_stem}.jsonl"
    sub = d / main_stem / "subagents"
    sub.mkdir(parents=True, exist_ok=True)
    copy = sub / src.name
    shutil.copy2(src, copy)
    main_path.write_text(
        json.dumps({
            "type": "user",
            "uuid": "00000000-0000-0000-0000-000000000001",
            "parentUuid": None,
            "sessionId": main_stem,
            "message": {"role": "user", "content": "benchmark stub"},
        }) + "\n",
        encoding="utf-8", newline="\n")
    return copy, main_path


def strip_backups(keep: Path):
    """Cozempic writes .bak siblings next to the file it treats; they must not count
    toward its result. Only backups are removed — the staging layout around an agent
    file must survive, and the second (idempotence) pass still needs it."""
    for f in keep.parent.iterdir():
        if f != keep and f.is_file() and ".bak" in f.name:
            try:
                f.unlink()
            except Exception:
                pass


# --------------------------------------------------------------------- work
def one(args) -> dict:
    src, kind, workroot, cfg = Path(args[0]), args[1], Path(args[2]), args[3]
    rec: dict = {"name": src.stem, "kind": kind}
    base_b, base_t = measure(src)
    rec.update(base_bytes=base_b, base_tokens=base_t)

    for tool, runner in (("cln", run_claudinine), ("coz", run_cozempic)):
        d = workroot / f"{src.stem[:40]}_{tool}"
        try:
            shutil.rmtree(d, ignore_errors=True)
            d.mkdir(parents=True, exist_ok=True)
            # Claudinine is driven through the hook, so an agent file must sit where
            # the subagent sweep looks. Cozempic takes a path directly, so it needs
            # no staging.
            if tool == "cln":
                copy, hook_path = stage_for_hook(d, src, kind)
                cfg = {**cfg, "hook_path": hook_path}
            else:
                copy = d / src.name
                shutil.copy2(src, copy)
            t0 = time.time()
            err = runner(copy, cfg)
            el = time.time() - t0
            if tool == "cln" and not err:
                # Second pass must be a no-op: a rewrite that keeps shrinking on
                # re-run is a bug, and would flatter the score.
                first = copy.read_bytes()
                err = runner(copy, cfg)
                rec["cln_idempotent"] = (copy.read_bytes() == first)
            strip_backups(copy)
            b, t = measure(copy)
            rec[f"{tool}_bytes"], rec[f"{tool}_tokens"] = b, t
            rec[f"{tool}_err"], rec[f"{tool}_secs"] = err, round(el, 2)
        except Exception as e:
            # Count a harness failure as ZERO saving rather than dropping the row,
            # so a crash can never look like a win.
            rec[f"{tool}_bytes"], rec[f"{tool}_tokens"] = base_b, base_t
            rec[f"{tool}_err"] = f"harness: {type(e).__name__}: {e}"[:200]
            rec[f"{tool}_secs"] = 0.0
        finally:
            shutil.rmtree(d, ignore_errors=True)
    return rec


def pct(after: int, before: int) -> float:
    return 100.0 * (1 - after / before) if before else 0.0


def report(results: list[dict], rx: str) -> None:
    def band(rows, label):
        if not rows:
            return
        bb = sum(r["base_bytes"] for r in rows)
        bt = sum(r["base_tokens"] for r in rows)
        cb = sum(r["cln_bytes"] for r in rows)
        ct = sum(r["cln_tokens"] for r in rows)
        zb = sum(r["coz_bytes"] for r in rows)
        zt = sum(r["coz_tokens"] for r in rows)
        print(f"| **{label}** (n={len(rows)}) "
              f"| {bb/1048576:.1f} MB / {bt/1e6:.2f} M tok "
              f"| {cb/1048576:.1f} MB ({pct(cb,bb):.1f}%) / **{ct/1e6:.2f} M tok "
              f"({pct(ct,bt):.1f}%)** "
              f"| {zb/1048576:.1f} MB ({pct(zb,bb):.1f}%) / {zt/1e6:.2f} M tok "
              f"({pct(zt,bt):.1f}%) |")

    errs = [r for r in results if r.get("cln_err") or r.get("coz_err")]
    noni = [r for r in results if r.get("cln_idempotent") is False]
    print(f"\nerrors: {len(errs)}   non-idempotent (claudinine): {len(noni)}")
    for r in errs[:10]:
        print(f"  {r['name'][:8]} cln={r.get('cln_err','')[:60]} coz={r.get('coz_err','')[:60]}")
    for r in noni[:10]:
        print(f"  NON-IDEMPOTENT {r['name'][:8]}")

    print(f"\ncozempic prescription: {rx}\n")
    print("| | baseline | Claudinine | Cozempic |")
    print("|---|---|---|---|")
    band(results, "All sessions")
    band([r for r in results if r["base_bytes"] > 1048576], "Over 1 MB")
    band([r for r in results if 102400 <= r["base_bytes"] <= 1048576], "100 KB – 1 MB")
    band([r for r in results if r["base_bytes"] < 102400], "Under 100 KB")
    band([r for r in results if r["kind"] == "main"], "Main transcripts")
    band([r for r in results if r["kind"] == "agent"], "Subagent transcripts")

    # Stratified by TOKEN size, and per-file central tendency.
    #
    # Why both: the token-weighted total above answers "across this exact pile of
    # files, what fraction of all tokens went away", so it is dominated by whichever
    # files happen to be largest — the top 10 sessions are ~41% of corpus tokens, and
    # the single largest (11.7%) is saturated at 97.7% vs 95.1%, where no gap is
    # arithmetically possible. That drags the corpus-wide gap to roughly half the
    # per-session reality. Session sizes are log-normal, so this is weight
    # domination, NOT contamination by anomalies: on a linear 1.5xIQR test 22 files
    # (58% of tokens) look like "outliers", which is only evidence the test is wrong
    # for the distribution. Trimming is the wrong fix — the low-gap tail IS the
    # toolUseResult blind spot, i.e. the finding, not noise. Stratifying keeps every
    # file and still reports what a user should expect on one session.
    def gap(r):
        return pct(r["cln_tokens"], r["base_tokens"]) - pct(r["coz_tokens"], r["base_tokens"])

    tok_bands = [("Under 30k tokens", 0, 30_000), ("30k – 100k", 30_000, 100_000),
                 ("100k – 400k", 100_000, 400_000), ("Over 400k", 400_000, 1 << 62)]
    print("\n| band | n | baseline | Claudinine | Cozempic | gap |")
    print("|---|---|---|---|---|---|")
    band_gaps = []
    for label, lo, hi in tok_bands:
        rows = [r for r in results if lo <= r["base_tokens"] < hi]
        if not rows:
            continue
        bt = sum(r["base_tokens"] for r in rows)
        ct = pct(sum(r["cln_tokens"] for r in rows), bt)
        zt = pct(sum(r["coz_tokens"] for r in rows), bt)
        band_gaps.append(ct - zt)
        print(f"| **{label}** | {len(rows)} | {bt/1e6:.2f} M tok "
              f"| **{ct:.1f}%** | {zt:.1f}% | +{ct - zt:.1f} |")

    gaps = sorted(gap(r) for r in results)
    k = int(0.05 * len(gaps))
    trimmed = gaps[k:len(gaps) - k] or gaps
    cln_each = sorted(pct(r["cln_tokens"], r["base_tokens"]) for r in results)
    coz_each = sorted(pct(r["coz_tokens"], r["base_tokens"]) for r in results)
    print(f"\nstratified (mean of band gaps): +{statistics.mean(band_gaps):.1f}")
    print(f"median per-file gap:            +{statistics.median(gaps):.1f}")
    print(f"5%-trimmed mean gap:            +{statistics.mean(trimmed):.1f}")
    print(f"median per-file: claudinine {statistics.median(cln_each):.1f}%  "
          f"cozempic {statistics.median(coz_each):.1f}%")

    # Strict: a file both tools leave untouched (single call, below MinCalls) is a
    # tie, not a win — `<=` counted it as one.
    wins = sum(1 for r in results if r["cln_tokens"] < r["coz_tokens"])
    ties = sum(1 for r in results if r["cln_tokens"] == r["coz_tokens"])
    print(f"\nper-file token wins: Claudinine {wins} / {len(results)}"
          f"  (ties: {ties})")
    big = [r for r in results if r["base_bytes"] > 1048576]
    if big:
        bw = sum(1 for r in big if r["cln_tokens"] < r["coz_tokens"])
        print(f"  over 1 MB: {bw} / {len(big)}")
    print(f"wall clock: claudinine {sum(r['cln_secs'] for r in results):.0f}s, "
          f"cozempic {sum(r['coz_secs'] for r in results):.0f}s")

    losses = sorted((r for r in results if r["coz_tokens"] < r["cln_tokens"]),
                    key=lambda r: -r["base_tokens"])
    print(f"\nfiles where cozempic saves more: {len(losses)}")
    if losses:
        print(f"  {'name':38} {'base_tok':>9} {'cln%':>7} {'coz%':>7} {'gap':>6}")
        for r in losses:
            print(f"  {r['name'][:36]:38} {r['base_tokens']:9d} "
                  f"{pct(r['cln_tokens'],r['base_tokens']):6.1f}% "
                  f"{pct(r['coz_tokens'],r['base_tokens']):6.1f}% "
                  f"{pct(r['cln_tokens'],r['base_tokens'])-pct(r['coz_tokens'],r['base_tokens']):+6.1f}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--corpus", default=str(DEFAULT_CORPUS))
    ap.add_argument("--work", default=str(DEFAULT_WORK))
    ap.add_argument("--out", default=str(DEFAULT_OUT))
    ap.add_argument("--claudinine", default=str(DEFAULT_CLN))
    ap.add_argument("--cozempic", default=str(DEFAULT_COZ))
    ap.add_argument("--rx", default="aggressive",
                    help="cozempic prescription (default: its strongest)")
    ap.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) - 2))
    ap.add_argument("--only", choices=["main", "agent"], help="limit to one population")
    ap.add_argument("--report-only", action="store_true",
                    help="re-print the report from an existing results file")
    args = ap.parse_args()

    out = Path(args.out)
    if args.report_only:
        report(json.loads(out.read_text(encoding="utf-8")), args.rx)
        return 0

    corpus = Path(args.corpus)
    manifest = corpus / "manifest.json"
    if not manifest.is_file():
        print(f"no corpus at {corpus} — run eng/bench/curate.py first", file=sys.stderr)
        return 1
    if not Path(args.claudinine).is_file():
        print(f"no claudinine binary at {args.claudinine} "
              f"— run eng/publish-win.ps1", file=sys.stderr)
        return 1

    man = json.loads(manifest.read_text(encoding="utf-8"))
    kept = [e for e in man["entries"] if e["decision"] == "keep"]
    jobs = [(str(corpus / e["corpus_path"]), e["kind"]) for e in kept
            if (args.only is None or e["kind"] == args.only)]
    print(f"corpus snapshot {man['created']}: {len(jobs)} files "
          f"({args.only or 'main+agent'})")

    cfg = {"cln": args.claudinine, "coz": args.cozempic, "rx": args.rx}
    work = Path(args.work)
    work.mkdir(parents=True, exist_ok=True)
    results: list[dict] = []
    with ProcessPoolExecutor(max_workers=args.jobs) as ex:
        payload = [(f, k, str(work), cfg) for f, k in jobs]
        for i, r in enumerate(ex.map(one, payload), 1):
            results.append(r)
            print(f"[{i}/{len(jobs)}] {r['name'][:30]:32} "
                  f"base={r['base_bytes']/1048576:7.2f}MB "
                  f"cln={pct(r['cln_tokens'],r['base_tokens']):5.1f}% "
                  f"coz={pct(r['coz_tokens'],r['base_tokens']):5.1f}%"
                  f"{'  CLN-ERR ' + r.get('cln_err','')[:40] if r.get('cln_err') else ''}"
                  f"{'  COZ-ERR ' + r.get('coz_err','')[:40] if r.get('coz_err') else ''}",
                  flush=True)
    out.write_text(json.dumps(results, indent=1), encoding="utf-8")
    print(f"\nwrote {out}")
    report(results, args.rx)
    shutil.rmtree(work, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
