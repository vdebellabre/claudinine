"""Steady-state hook latency: what a user actually waits for on each prompt.

compare.py times the COLD pass — SessionStart against an untouched transcript,
mirroring the whole file from scratch. That is the worst case Claudinine ever
faces and it is not what a user experiences per prompt.

The steady state is different: by the time you send prompt N, prompts 1..N-1
have already been compacted and mirrored, so the pass finds one turn's worth of
new work and nothing else. This measures THAT, the way it really happens:

  1. warm  — run the hook once over a corpus copy, so the file reaches the state
             a real session is in (compacted, fully mirrored). Not timed.
  2. time  — run the UserPromptSubmit hook `--repeat` more times and record each.
             The file is already at rest, so every pass does steady-state work.

Reported per file: median of the timed passes. Reported overall: the
distribution across the corpus. Process startup is INCLUDED — it is part of what
the user waits for.

USAGE
    uv run python eng/bench/steady.py
    uv run python eng/bench/steady.py --only main --repeat 5
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import statistics
import subprocess
import tempfile
import time
from pathlib import Path

from compare import DEFAULT_CLN, DEFAULT_CORPUS, stage_for_hook

# The app's documented budget for the per-prompt hook.
USER_PROMPT_BUDGET_MS = 25_000


def fire(cln: Path, hook_path: Path, data: Path, event: str) -> tuple[float, str]:
    """Run one hook invocation, return (elapsed_ms, error)."""
    payload = json.dumps({
        "hook_event_name": event,
        "transcript_path": str(hook_path),
        "session_id": hook_path.stem,
        "cwd": str(hook_path.parent),
    })
    env = dict(os.environ, CLAUDE_PLUGIN_DATA=str(data))
    t0 = time.perf_counter()
    try:
        p = subprocess.run([str(cln), "hook"], input=payload, text=True,
                           capture_output=True, timeout=600, env=env,
                           encoding="utf-8", errors="replace")
        el = (time.perf_counter() - t0) * 1000
        return el, "" if p.returncode == 0 else f"rc={p.returncode}"
    except Exception as e:
        return (time.perf_counter() - t0) * 1000, f"{type(e).__name__}"


def measure_file(cln: Path, src: Path, kind: str, repeat: int) -> dict:
    with tempfile.TemporaryDirectory(prefix="cln-steady-") as td:
        d = Path(td)
        copy, hook_path = stage_for_hook(d, src, kind)
        data = d / "_plugindata"
        data.mkdir(parents=True, exist_ok=True)

        # 1. Warm: bring the file to the state a live session is already in.
        cold_ms, err = fire(cln, hook_path, data, "SessionStart")
        if err:
            return {"name": src.name, "err": err}
        warm_size = copy.stat().st_size

        # 2. Time the steady-state pass.
        samples = []
        for _ in range(repeat):
            ms, err = fire(cln, hook_path, data, "UserPromptSubmit")
            if err:
                return {"name": src.name, "err": err}
            samples.append(ms)

        # A steady-state pass must not keep shrinking the file; if it does, the
        # "already at rest" premise is false and the number means nothing.
        stable = copy.stat().st_size == warm_size
        return {
            "name": src.name,
            "kind": kind,
            "cold_ms": round(cold_ms, 1),
            "steady_ms": round(statistics.median(samples), 1),
            "min_ms": round(min(samples), 1),
            "stable": stable,
            "size_kb": round(warm_size / 1024),
            "err": "",
        }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--corpus", type=Path, default=DEFAULT_CORPUS)
    ap.add_argument("--cln", type=Path, default=DEFAULT_CLN)
    ap.add_argument("--only", choices=["main", "agent"], default=None)
    ap.add_argument("--repeat", type=int, default=3)
    ap.add_argument("--out", type=Path, default=None)
    args = ap.parse_args()

    if not args.cln.exists():
        print(f"binary not found: {args.cln}")
        return 1

    groups = ["main", "agent"] if args.only is None else [args.only]
    files = []
    for g in groups:
        gdir = args.corpus / g
        if gdir.is_dir():
            files.extend((f, g) for f in sorted(gdir.glob("*.jsonl")))
    if not files:
        print(f"no corpus files under {args.corpus} — run curate.py first")
        return 1

    rows = []
    for i, (f, kind) in enumerate(files, 1):
        r = measure_file(args.cln, f, kind, args.repeat)
        rows.append(r)
        tag = r.get("err") or f"{r['steady_ms']:7.1f} ms steady  ({r['cold_ms']:7.1f} cold)"
        print(f"[{i}/{len(files)}] {r['name'][:34]:36} {tag}")

    ok = [r for r in rows if not r.get("err")]
    if not ok:
        print("every file errored")
        return 1

    steady = sorted(r["steady_ms"] for r in ok)
    cold = sorted(r["cold_ms"] for r in ok)
    unstable = [r["name"] for r in ok if not r["stable"]]

    def pct(xs, p):
        return xs[min(len(xs) - 1, int(p * len(xs)))]

    print(f"\n{len(ok)} files, {args.repeat} timed passes each\n")
    print("steady-state pass (UserPromptSubmit, file already at rest)")
    print(f"  median {statistics.median(steady):6.1f} ms"
          f"   p90 {pct(steady,.9):6.1f} ms"
          f"   max {max(steady):6.1f} ms")
    print("cold pass (SessionStart, untouched transcript) — for contrast")
    print(f"  median {statistics.median(cold):6.1f} ms"
          f"   p90 {pct(cold,.9):6.1f} ms"
          f"   max {max(cold):6.1f} ms")
    print(f"\nworst steady-state pass is {100*max(steady)/USER_PROMPT_BUDGET_MS:.2f}% "
          f"of the {USER_PROMPT_BUDGET_MS/1000:.0f}s UserPromptSubmit budget")
    if unstable:
        print(f"\nWARNING: {len(unstable)} file(s) still changed size after warm-up "
              f"— not at rest: {', '.join(unstable[:5])}")

    if args.out:
        args.out.write_text(json.dumps(rows, indent=1), encoding="utf-8")
        print(f"\nwrote {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
