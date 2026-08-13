"""Content census over the curated corpus: where the API-visible tokens live.

Answers "what is a transcript actually made of?" using the SAME ruler as
compare.py — `message.content` blocks only, from the last compact_boundary
onward. Any number this prints is therefore directly comparable to the
percentages in the head-to-head, and excludes the two phantom payloads
(`toolUseResult`, pre-boundary history) by construction.

Measures the BASELINE corpus (untouched transcripts), which is what a
"transcripts are X% tool output" claim is about.

USAGE
    uv run --with tiktoken python eng/bench/census.py
    uv run --with tiktoken python eng/bench/census.py --only main
"""
from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

import tiktoken

from compare import DEFAULT_CORPUS, ENC


def classify(block) -> str:
    """Bucket one message.content block by what the model reads it as."""
    if isinstance(block, str):
        return "text"
    if not isinstance(block, dict):
        return "other"
    t = block.get("type")
    if t == "text":
        return "text"
    if t == "thinking":
        return "thinking"
    if t == "tool_use":
        return "tool_use"
    if t == "tool_result":
        return "tool_result"
    if t in ("image", "document"):
        return "media"
    return "other"


def block_text(block) -> str:
    """Same extraction compare.py._blocks performs, per block."""
    if isinstance(block, str):
        return block
    if not isinstance(block, dict):
        return ""
    t = block.get("type")
    if t == "text":
        return block.get("text") or ""
    if t == "thinking":
        return block.get("thinking") or ""
    if t == "tool_use":
        return json.dumps(block.get("input", ""), ensure_ascii=False)
    if t == "tool_result":
        content = block.get("content")
        if isinstance(content, str):
            return content
        if isinstance(content, list):
            return "\n".join(block_text(b) for b in content)
        return ""
    return ""


def census(path: Path, tally: Counter, roles: Counter) -> None:
    records = []
    with path.open("r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except Exception:
                continue

    # Identical boundary handling to compare.payload_text.
    cut, preserved = 0, set()
    for i, rec in enumerate(records):
        if rec.get("type") == "system" and rec.get("subtype") == "compact_boundary":
            cut = i
            meta = rec.get("compactMetadata") or {}
            pm = meta.get("preservedMessages") or {}
            preserved = set(pm.get("allUuids") or [])

    for i, rec in enumerate(records):
        if i < cut and rec.get("uuid") not in preserved:
            continue
        msg = rec.get("message")
        if not isinstance(msg, dict):
            continue
        role = msg.get("role") or "unknown"
        content = msg.get("content")
        blocks = content if isinstance(content, list) else [content]
        for b in blocks:
            kind = classify(b)
            text = block_text(b)
            if not text:
                continue
            n = len(ENC.encode(text, disallowed_special=()))
            tally[kind] += n
            roles[role] += n


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--corpus", type=Path, default=DEFAULT_CORPUS)
    ap.add_argument("--only", choices=["main", "agent"], default=None)
    args = ap.parse_args()

    groups = ["main", "agent"] if args.only is None else [args.only]
    files = []
    for g in groups:
        d = args.corpus / g
        if d.is_dir():
            files.extend(sorted(d.glob("*.jsonl")))

    if not files:
        print(f"no corpus files under {args.corpus} — run curate.py first")
        return 1

    tally: Counter = Counter()
    roles: Counter = Counter()
    for f in files:
        census(f, tally, roles)

    total = sum(tally.values())
    if total == 0:
        print("no API-visible tokens found")
        return 1

    print(f"corpus: {len(files)} files ({', '.join(groups)})")
    print(f"API-visible tokens: {total:,}\n")
    print("by block type")
    for kind, n in tally.most_common():
        print(f"  {kind:<12} {n:>12,}  {100*n/total:5.1f}%")
    print("\nby role")
    for role, n in roles.most_common():
        print(f"  {role:<12} {n:>12,}  {100*n/total:5.1f}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
