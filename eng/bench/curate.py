"""Snapshot a curated benchmark corpus from the live Claude Code session store.

WHY THIS EXISTS
---------------
Benchmarks used to read `~/.claude/projects/**` in place. Two problems with that:

  * The population drifts. Sessions get deleted or age out, so a rerun silently
    measures a different corpus than the last one (19 of 95 files vanished
    between 2026-08-09 and 2026-08-12) and no number is comparable to any other.
  * Live files are contaminated. Claudinine compacts sessions in normal use, so
    by the time you benchmark, its own past work is baked into the "baseline" —
    which scores Claudinine at ~0% on those files while crediting the other tool
    for shrinking an already-reduced file. Benchmarking live biases AGAINST us.

So: snapshot once, with provenance recorded, and benchmark the snapshot forever.

THE FAIR-BASELINE RULE
----------------------
A benchmark baseline must be an UNCOMPACTED transcript. Each candidate is
classified and handled accordingly:

  raw       — no compaction marker from any tool. Use the file as-is.
  claudinine— carries our marker. The uncompacted original is the session MIRROR,
              which is byte-for-byte the pre-compaction record; use it when it
              COVERS the session (uuid-set test, see mirror_is_usable), else SKIP.
              Never use the compacted file itself.
  cozempic  — carries a [cozempic...] text marker. Its only backup is a whole-file
              .bak; use that if present and itself clean, else SKIP. (Rare.)
  both      — treated like `claudinine`: our mirror is checked, and if it covers
              the session and is itself clean it predates cozempic's pass and is a
              valid baseline. Skipped only if the mirror fails those tests.
  unusable  — empty, unparseable, or too small to measure. SKIP.

Everything is COPIED; sources are opened read-only and never written.

USAGE
    python eng/bench/curate.py                 # scan + copy into bench/corpus/
    python eng/bench/curate.py --dry-run       # classify and report, copy nothing
    python eng/bench/curate.py --min-tokens 0  # keep tiny sessions too
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

# ---------------------------------------------------------------- detection

# Claudinine stamps a `claudinine` envelope key on every record it rewrites
# (RuleHelpers.SetReplacement) and on mirror/skip/load-stamp header lines
# (MirrorFormat.Line). The chain-collapse carrier also has a text prefix.
CLN_ENVELOPE_KEY = "claudinine"
CLN_CARRIER_PREFIX = "[claudinine: this turn originally ran "

# Envelope fields that are BOOKKEEPING, not evidence of compaction. A mirror
# legitimately carries these (MirrorFormat.Line): its `mirrorOf` header, and a
# `mergedFromFork` separator where fork-heal spliced a forked session's records in.
# A record stamped by a RULE carries `rule` instead — that IS compacted content and
# must never appear in a baseline.
CLN_BOOKKEEPING_FIELDS = ("mirrorOf", "mergedFromFork", "skipCompactionOf", "loadStampOf")

# Cozempic writes NO envelope key — its only trace is digest text in content.
# Literals confirmed in cozempic-quiet/src/cozempic/ (standard.py:513,
# stale_reads:131, digest:84).
COZ_TEXT_MARKERS = ("[cozempic", "[cozempic.digest]")


def iter_records(path: Path):
    """Yield (lineno, dict) for parseable lines; skip blank/torn ones."""
    with path.open("r", encoding="utf-8", errors="replace") as fh:
        for i, line in enumerate(fh, 1):
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except Exception:
                continue
            if isinstance(rec, dict):
                yield i, rec


def _texts(rec: dict):
    """Every string in a record that a tool could have written a marker into."""
    msg = rec.get("message")
    content = msg.get("content") if isinstance(msg, dict) else None
    if isinstance(content, str):
        yield content
    elif isinstance(content, list):
        for b in content:
            if isinstance(b, str):
                yield b
            elif isinstance(b, dict):
                for k in ("text", "thinking"):
                    if isinstance(b.get(k), str):
                        yield b[k]
                c = b.get("content")
                if isinstance(c, str):
                    yield c
                elif isinstance(c, list):
                    for sub in c:
                        if isinstance(sub, dict) and isinstance(sub.get("text"), str):
                            yield sub["text"]
    for k in ("summary", "content"):
        v = rec.get(k)
        if isinstance(v, str):
            yield v


def classify(path: Path) -> dict:
    """Detect which tools have touched a transcript, plus basic shape."""
    n_records = n_lines = 0
    cln = coz = False
    cln_hits: list[str] = []
    has_boundary = False
    for lineno, rec in iter_records(path):
        n_records += 1
        n_lines = lineno
        if CLN_ENVELOPE_KEY in rec:
            cln = True
            inner = rec.get(CLN_ENVELOPE_KEY)
            if isinstance(inner, dict) and any(f in inner for f in CLN_BOOKKEEPING_FIELDS):
                tag = "bookkeeping"
            elif isinstance(inner, dict):
                tag = inner.get("rule") or "rule:unknown"
            else:
                tag = "rule:unknown"
            if tag not in cln_hits:
                cln_hits.append(tag)
        if rec.get("subtype") == "compact_boundary":
            has_boundary = True
        for t in _texts(rec):
            if not cln and CLN_CARRIER_PREFIX in t:
                cln = True
                cln_hits.append("carrier-text")
            if not coz and any(m in t for m in COZ_TEXT_MARKERS):
                coz = True
    tool = ("both" if cln and coz else "claudinine" if cln
            else "cozempic" if coz else "raw")
    return dict(records=n_records, lines=n_lines, tool=tool,
                cln_hits=cln_hits, app_compacted=has_boundary)


# ------------------------------------------------------------------ baselines

def mirror_search_dirs() -> list[Path]:
    """Mirror the C# MirrorLocator.SearchDirectories() probe order."""
    dirs: list[Path] = []
    seen: set[str] = set()

    def add(d: Path):
        key = str(d.resolve()).lower()
        if d.is_dir() and key not in seen:
            seen.add(key)
            dirs.append(d)

    pd = os.environ.get("CLAUDE_PLUGIN_DATA")
    if pd:
        add(Path(pd) / "mirrors")
    home = Path.home()
    data_root = home / ".claude" / "plugins" / "data"
    if data_root.is_dir():
        for plugin in sorted(data_root.glob("claudinine-*")):
            add(plugin / "mirrors")
    add(home / ".claudinine" / "mirrors")
    return dirs


def find_mirror(stem: str, dirs: list[Path]) -> Path | None:
    """Largest mirror for this stem across all known dirs (cross-context resume
    can leave several; the longest is the most complete history)."""
    best: Path | None = None
    for d in dirs:
        c = d / f"{stem}.jsonl"
        if c.is_file() and (best is None or c.stat().st_size > best.stat().st_size):
            best = c
    return best


def mirror_is_usable(mirror: Path, compacted: Path) -> tuple[bool, str]:
    """A mirror substitutes for the baseline only if it is clean and actually
    COVERS the session.

    Coverage is a uuid-set test, not a size test: a size comparison looks
    reasonable and is wrong — a mirror holding pristine full outputs for 56 of 71
    records is *smaller* than the compacted file whose 15 uncovered records are
    verbatim, yet using it would silently drop those records from the baseline.
    Conversely a mirror can legitimately be far larger (it accumulates across
    resumes).

    Only records the live file still carries VERBATIM are required to be present.
    A stub (one carrying the `claudinine` key) is deliberately absent from the
    mirror — MirrorFile skips already-stubbed records because the original they
    replaced was mirrored, under that same uuid, on the pass that created them
    (`if (rec.Node["claudinine"] is not null) continue`). Requiring stub uuids
    would reject every healthily-mirrored session.

    A mirror only ever grows by append, so a genuinely missing verbatim record
    means the plugin was installed (or the data dir changed) partway through, or
    the newest turn is not yet mirrored.
    """
    info = classify(mirror)
    # Bookkeeping lines (mirrorOf header, mergedFromFork separator) belong in a
    # mirror by design; a RULE mark would mean compacted content leaked in, which
    # must never happen.
    rule_marks = [h for h in info["cln_hits"] if h != "bookkeeping"]
    if rule_marks:
        return False, f"mirror carries rule marks {rule_marks}"
    if info["records"] < 2:
        return False, "mirror empty"

    mirror_uuids = {r.get("uuid") for _, r in iter_records(mirror) if r.get("uuid")}
    missing = [r["uuid"] for _, r in iter_records(compacted)
               if r.get("uuid")
               and CLN_ENVELOPE_KEY not in r  # stub: original already mirrored
               and r["uuid"] not in mirror_uuids]
    if missing:
        return False, (f"mirror covers only part of the session "
                       f"({len(missing)} verbatim records absent)")

    # Cozempic marks are judged on the SUBSTITUTED baseline (the mirror), not on
    # the live file: a session compacted by both tools is still fair game if the
    # mirror predates cozempic and is itself clean.
    if info["tool"] in ("cozempic", "both"):
        return False, "mirror carries cozempic marks"
    return True, ""


def find_bak(path: Path) -> Path | None:
    """Cozempic's timestamped whole-file backup: <stem>.<ts>.jsonl.bak."""
    cands = sorted(path.parent.glob(f"{path.stem}.*.jsonl.bak"),
                   key=lambda p: p.stat().st_mtime, reverse=True)
    return cands[0] if cands else None


# ----------------------------------------------------------------- payload

def payload_tokens_estimate(path: Path) -> int:
    """Cheap proxy for payload size used only for the --min-tokens filter.
    The real benchmark measures with cl100k_base; this just needs to rank."""
    total = 0
    for _, rec in iter_records(path):
        for t in _texts(rec):
            total += len(t)
        msg = rec.get("message")
        content = msg.get("content") if isinstance(msg, dict) else None
        if isinstance(content, list):
            for b in content:
                if isinstance(b, dict) and b.get("type") == "tool_use":
                    total += len(json.dumps(b.get("input", ""), ensure_ascii=False))
    return total // 4  # ~4 chars/token


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# -------------------------------------------------------------------- main

def main() -> int:
    repo = Path(__file__).resolve().parents[2]
    ap = argparse.ArgumentParser()
    ap.add_argument("--projects", default=str(Path.home() / ".claude" / "projects"))
    ap.add_argument("--out", default=str(repo / "bench" / "corpus"))
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--min-tokens", type=int, default=1000,
                    help="skip sessions below this payload estimate (default 1000)")
    args = ap.parse_args()

    projects = Path(args.projects)
    if not projects.is_dir():
        print(f"no session store at {projects}", file=sys.stderr)
        return 1
    out = Path(args.out)
    mirrors = mirror_search_dirs()

    print(f"scanning {projects}")
    print(f"mirror dirs: {[str(d) for d in mirrors] or 'none found'}")

    files = sorted(projects.rglob("*.jsonl"))
    # A subagent transcript lives in <session>/subagents/; keep the distinction so
    # the benchmark can report main and sidechain populations separately.
    print(f"{len(files)} .jsonl files found\n")

    entries: list[dict] = []
    reasons: Counter = Counter()
    for src in files:
        if src.suffix != ".jsonl" or src.name.endswith(".bak"):
            continue
        kind = "agent" if src.parent.name == "subagents" else "main"
        info = classify(src)
        rec = dict(name=src.stem, kind=kind, source=str(src), tool=info["tool"],
                   records=info["records"], app_compacted=info["app_compacted"])

        if info["records"] < 2:
            rec.update(decision="skip", reason="empty or unparseable")
            reasons["empty"] += 1
            entries.append(rec)
            continue

        baseline: Path | None = None
        if info["tool"] == "raw":
            baseline, rec["baseline_kind"] = src, "raw"
        elif info["tool"] in ("claudinine", "both"):
            # "both" is not fatal: if OUR mirror covers the session and is itself
            # clean, it predates cozempic's pass and is a valid baseline. The
            # mirror is the authority, so it is checked either way.
            m = find_mirror(src.stem, mirrors)
            if m is None:
                rec.update(decision="skip", reason=f"{info['tool']}-compacted, no mirror")
                reasons["cln-no-mirror"] += 1
                entries.append(rec)
                continue
            ok, why = mirror_is_usable(m, src)
            if not ok:
                rec.update(decision="skip", reason=f"{info['tool']}-compacted, {why}")
                reasons["cln-bad-mirror"] += 1
                entries.append(rec)
                continue
            baseline, rec["baseline_kind"] = m, "mirror"
            rec["mirror"] = str(m)
        elif info["tool"] == "cozempic":
            bak = find_bak(src)
            if bak is None:
                rec.update(decision="skip", reason="cozempic-compacted, no .bak")
                reasons["coz-no-bak"] += 1
                entries.append(rec)
                continue
            if classify(bak)["tool"] != "raw":
                rec.update(decision="skip", reason="cozempic .bak not clean")
                reasons["coz-bad-bak"] += 1
                entries.append(rec)
                continue
            baseline, rec["baseline_kind"] = bak, "bak"
            rec["bak"] = str(bak)
        else:
            rec.update(decision="skip", reason=f"unhandled classification {info['tool']}")
            reasons["unclassified"] += 1
            entries.append(rec)
            continue

        est = payload_tokens_estimate(baseline)
        rec["payload_tokens_est"] = est
        if est < args.min_tokens:
            rec.update(decision="skip", reason=f"below --min-tokens ({est})")
            reasons["too-small"] += 1
            entries.append(rec)
            continue

        rec.update(decision="keep", baseline=str(baseline),
                   baseline_bytes=baseline.stat().st_size)
        entries.append(rec)

    keep = [e for e in entries if e["decision"] == "keep"]
    print(f"{'would keep' if args.dry_run else 'keeping'} {len(keep)} of {len(entries)}")
    by_kind = Counter(e["kind"] for e in keep)
    by_base = Counter(e["baseline_kind"] for e in keep)
    print(f"  by kind:     {dict(by_kind)}")
    print(f"  by baseline: {dict(by_base)}")
    print(f"  skipped:     {dict(reasons)}")
    tot = sum(e["payload_tokens_est"] for e in keep)
    print(f"  est payload: {tot/1e6:.2f}M tokens, "
          f"{sum(e['baseline_bytes'] for e in keep)/1e6:.1f} MB\n")

    if args.dry_run:
        for e in entries:
            if e["decision"] == "skip" and e["tool"] != "raw":
                print(f"  skip {e['name'][:8]} [{e['tool']}] {e['reason']}")
        return 0

    # Copy. main/ and agent/ keep the two populations separable; names are
    # session stems so a result row can always be traced back to its source.
    for sub in ("main", "agent"):
        (out / sub).mkdir(parents=True, exist_ok=True)
    for e in keep:
        dst = out / e["kind"] / f"{e['name']}.jsonl"
        shutil.copy2(e["baseline"], dst)
        e["corpus_path"] = str(dst.relative_to(out))
        e["sha256"] = sha256(dst)

    manifest = dict(
        created=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        projects_root=str(projects),
        mirror_dirs=[str(d) for d in mirrors],
        min_tokens=args.min_tokens,
        kept=len(keep), scanned=len(entries),
        skip_reasons=dict(reasons),
        entries=entries,
    )
    (out / "manifest.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
    print(f"copied {len(keep)} files into {out}")
    print(f"manifest: {out / 'manifest.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
