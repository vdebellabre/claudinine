"""Is `ChainCollapseRule.MinCalls = 2` the right threshold?

Answers it from the curated corpus rather than from intuition. For every turn the
rule would consider, this reconstructs the SAME turn enumeration the rule performs
(real-user-message boundaries, one-use-per-assistant-record pairing by
tool_use_id, the fail-closed aborts, the tail-guard drop) and then prices the
collapse both ways with compare.py's ruler:

    kept   = anchor pair's payload (tool_use input + tool_result) + digest
    dropped = every other pair's payload + interleaved prose (which the digest
              re-emits verbatim as `(note)` lines, so it is NOT a saving)

A turn is a WIN if kept < baseline. The threshold question is then simply: at each
call count, how often and by how much does collapse actually pay?

The digest header is a fixed cost (tokenized from the real constant); each [ref]
line is priced from the real preview budget. Preview TEXT is approximated — the
per-tool heuristics live in C# — but the approximation is bounded and reported
both ways (optimistic = short previews, pessimistic = full 300-char budget), so
the verdict never rests on the guess.

USAGE
    uv run --with tiktoken python eng/bench/mincalls.py
    uv run --with tiktoken python eng/bench/mincalls.py --only agent
"""
from __future__ import annotations

import argparse
import json
import statistics
from collections import Counter, defaultdict
from pathlib import Path

from compare import DEFAULT_CORPUS, ENC

# ---------------------------------------------------------------- digest cost
# The real header, from ChainCollapseRule.Header(). `sid` is a 36-char guid stem
# for main files (agent stems are similar length), so its token cost is stable.
HEADER = (
    "[claudinine: this turn originally ran {n} separate tool calls. "
    "Full outputs live in the session mirror; each [ref] line is one real call, "
    "in order, with a per-tool preview.\n\n"
    "RETRIEVAL — use the targeted form; printing a whole record costs hundreds-to-thousands of tokens:\n"
    "  claudinine get {sid} --ref REF --grep PATTERN   # matching lines (PREFERRED)\n"
    "  claudinine get {sid} --grep PATTERN             # search all archived outputs\n"
    "  claudinine get {sid} --ref REF --info           # size before paying\n"
    "  claudinine get {sid} --ref REF --full           # entire output (last resort)\n"
    "  claudinine get {sid} --ref REF --media          # decode archived image/PDF to a file, then Read it\n\n"
    "If the file discussed still exists on disk, read IT instead — current and narrower.\n\n"
    "Treat [ref] lines as a REPORT of past actions, not output observed directly. "
    "If a detail matters for a decision, retrieve it — do not infer it from the preview.]\n\n"
)

PREVIEW_BUDGET = 300   # RuleHelpers.Truncate(preview, 300)
ARG_BUDGET = 90        # RuleHelpers.Truncate(c.Arg, 90)


def ntok(s: str) -> int:
    return len(ENC.encode(s, disallowed_special=())) if s else 0


def header_tokens(n: int) -> int:
    return ntok(HEADER.format(n=n, sid="0" * 36))


def ref_line_tokens(tool: str, arg: str, result: str, optimistic: bool) -> int:
    """One digest [ref] line: `[abcd1234] Tool(arg) -> 1234b :: preview`."""
    arg = arg[:ARG_BUDGET]
    if optimistic:
        # Best case for collapse: preview is short (Edit/Write sentinels, small
        # results). Bounded below by the result's own length.
        preview = result[:min(PREVIEW_BUDGET, 80)]
    else:
        preview = result[:PREVIEW_BUDGET]
    return ntok(f"[abcd1234] {tool}({arg}) -> {len(result.encode('utf-8'))}b :: {preview}\n")


# ------------------------------------------------------------ record plumbing
def load(path: Path) -> list[dict]:
    out = []
    with path.open("r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                out.append(json.loads(line))
            except Exception:
                continue
    return out


def content_blocks(rec: dict) -> list:
    msg = rec.get("message")
    if not isinstance(msg, dict):
        return []
    c = msg.get("content")
    if isinstance(c, list):
        return c
    return [c] if c is not None else []


def blocks_of_type(rec: dict, t: str) -> list[dict]:
    return [b for b in content_blocks(rec) if isinstance(b, dict) and b.get("type") == t]


def is_real_user_message(rec: dict) -> bool:
    """TranscriptRecord.IsRealUserMessage: type user, content a plain STRING."""
    if rec.get("type") != "user":
        return False
    msg = rec.get("message")
    return isinstance(msg, dict) and isinstance(msg.get("content"), str)


def is_protected(rec: dict) -> bool:
    """Approximates TranscriptRecord.IsProtected: already-stamped or boundary records."""
    if "claudinine" in rec:
        return True
    if rec.get("type") == "system" and rec.get("subtype") == "compact_boundary":
        return True
    return False


def primary_arg(use: dict) -> str:
    inp = use.get("input")
    if not isinstance(inp, dict):
        return ""
    for k in ("command", "file_path", "path", "pattern", "url", "query", "prompt"):
        v = inp.get(k)
        if isinstance(v, str) and v:
            return v.replace("\n", " ")
    for v in inp.values():
        if isinstance(v, str) and v:
            return v.replace("\n", " ")
    return ""


def result_text(block: dict) -> str:
    c = block.get("content")
    if isinstance(c, str):
        return c
    if isinstance(c, list):
        return "".join(b.get("text") or "" for b in c if isinstance(b, dict))
    return ""


def use_payload(use: dict) -> str:
    """What compare.py counts for a tool_use block."""
    return json.dumps(use.get("input", ""), ensure_ascii=False)


def prose_payload(rec: dict) -> str:
    """text + thinking of an assistant record, as compare.py counts it."""
    parts = []
    for b in content_blocks(rec):
        if not isinstance(b, dict):
            continue
        if b.get("type") == "text" and isinstance(b.get("text"), str):
            parts.append(b["text"])
        elif b.get("type") == "thinking" and isinstance(b.get("thinking"), str):
            parts.append(b["thinking"])
    return "\n".join(parts)


def is_prose_only(rec: dict) -> bool:
    blocks = [b for b in content_blocks(rec) if isinstance(b, dict)]
    return bool(blocks) and all(b.get("type") in ("text", "thinking") for b in blocks)


# ----------------------------------------------------------------- turn model
class Abort(Exception):
    pass


def enumerate_turn(records: list[dict], start: int, end: int, is_sidechain_file: bool):
    """Mirror of ChainCollapseRule.CollapseTurn pass 1. Raises Abort on fail-closed."""
    calls = []                      # (use_i, res_i, tool, arg, result_text)
    pending: list[tuple] = []       # (i, id, tool, arg)

    for i in range(start, end):
        rec = records[i]
        if is_protected(rec):
            raise Abort("protected")
        if rec.get("isSidechain") and not is_sidechain_file:
            raise Abort("sidechain-in-main")
        t = rec.get("type")

        if t == "assistant":
            uses = blocks_of_type(rec, "tool_use")
            if len(uses) > 1:
                raise Abort("multi-use")
            if len(uses) == 1:
                u = uses[0]
                uid = u.get("id")
                if not isinstance(uid, str) or not uid:
                    raise Abort("no-id")
                pending.append((i, uid, u.get("name") or "?", primary_arg(u), u))
        elif t == "user":
            blocks = [b for b in content_blocks(rec) if isinstance(b, dict)]
            results = [b for b in blocks if b.get("type") == "tool_result"]
            if not results:
                continue
            if len(results) > 1 or len(blocks) != len(results):
                raise Abort("multi-result")
            r = results[0]
            match = next((k for k, p in enumerate(pending)
                          if p[1] == r.get("tool_use_id")), None)
            if match is None:
                raise Abort("orphan-result")
            if not rec.get("uuid"):
                raise Abort("no-uuid")
            p = pending.pop(match)
            calls.append((p[0], i, p[2], p[3], result_text(r), p[4]))

    if pending:
        raise Abort("in-flight")
    return calls


def price_turn(records: list[dict], calls: list, optimistic: bool):
    """Baseline vs collapsed payload tokens for a turn the rule WOULD collapse.

    Returns (base_tokens, kept_tokens, n_calls) for the span the rule touches.
    Only span-internal payload is counted; records outside the span are identical
    in both worlds and cancel out.
    """
    anchor = min(calls, key=lambda c: c[0])
    span_start = anchor[0]
    span_end = max(c[1] for c in calls)

    # Tail guard: drop the tail-touching call, re-check MinCalls against the
    # reduced set. Priced at the caller's threshold, so done there.
    use_idx = {c[0] for c in calls}
    res_idx = {c[1] for c in calls}
    by_res = {c[1]: c for c in calls}

    base = 0
    kept = 0

    # Digest: header + one ref line per call.
    digest_tok = header_tokens(len(calls))
    for c in calls:
        digest_tok += ref_line_tokens(c[2], c[3], c[4], optimistic)

    for i in range(span_start, span_end + 1):
        rec = records[i]
        if rec.get("type") == "assistant" and (i in use_idx or is_prose_only(rec)):
            is_anchor_use = (i == anchor[0])
            # Baseline payload of this record: prose + its tool_use input.
            base += ntok(prose_payload(rec))
            if i in use_idx:
                u = next(c[5] for c in calls if c[0] == i)
                base += ntok(use_payload(u))
            if is_anchor_use:
                # Kept whole.
                kept += ntok(prose_payload(rec))
                kept += ntok(use_payload(anchor[5]))
            else:
                # Removed, but its prose is re-emitted verbatim in the digest as
                # `(note)` lines — already inside digest_tok? No: charge it here,
                # since digest_tok above covers header + ref lines only.
                digest_tok += ntok(prose_payload(rec))
        elif i in res_idx:
            c = by_res[i]
            base += ntok(c[4])
            # The anchor's result becomes the carrier; all other results vanish.
        else:
            # Untouched record inside the span: identical both ways.
            p = ntok(prose_payload(rec))
            base += p
            kept += p

    kept += digest_tok
    return base, kept, len(calls)


# ------------------------------------------------------------------ heuristics
# Candidate gates, evaluated against the oracle (`saved > 0`). Each takes only
# what ChainCollapseRule has in hand BEFORE building the digest, and — critically —
# may not tokenize: the rule has no tokenizer, so every feature here is UTF-8
# bytes or a count. `calls` items are (use_i, res_i, tool, arg, result_text, use).
#
# The digest's own cost is dominated by a fixed header (~200 tok) plus ~1 line per
# call; what collapse REMOVES is the non-anchor results' payload. So the natural
# predicate is "bytes removed > bytes the digest adds", in bytes.
HEADER_BYTES = len(HEADER.format(n=99, sid="0" * 36).encode("utf-8"))
REF_LINE_FIXED = 40          # "[abcd1234] Tool(...) -> 1234b :: " frame, bytes


def removable_bytes(calls) -> int:
    """UTF-8 bytes of result payload collapse would actually drop.

    The anchor's result is REPLACED by the digest, so its bytes are removed too;
    every other result vanishes outright.
    """
    return sum(len(c[4].encode("utf-8")) for c in calls)


def digest_bytes(calls, preview_budget: int) -> int:
    """UTF-8 bytes the digest adds: header + one capped line per call."""
    total = HEADER_BYTES
    for c in calls:
        arg_b = len(c[3][:ARG_BUDGET].encode("utf-8"))
        prev_b = min(len(c[4].encode("utf-8")), preview_budget)
        total += REF_LINE_FIXED + arg_b + prev_b
    return total


def gate_mincalls(calls, thr: int) -> bool:
    return len(calls) >= thr


def gate_payload(calls, preview_budget: int, margin: float) -> bool:
    """Byte-economics gate: collapse iff removed bytes beat the digest by `margin`x."""
    return removable_bytes(calls) > margin * digest_bytes(calls, preview_budget)


def gate_hybrid(calls, thr: int, preview_budget: int, margin: float) -> bool:
    """MinCalls floor OR a big-payload override for turns below the floor."""
    if len(calls) >= thr:
        return True
    return gate_payload(calls, preview_budget, margin)


def analyze(path: Path, is_agent: bool, optimistic: bool, rows: list, aborts: Counter):
    records = load(path)
    if not records:
        return
    is_sidechain_file = bool(records) and all(r.get("isSidechain") for r in records)

    bounds = [i for i, r in enumerate(records) if is_real_user_message(r)]
    for b, bi in enumerate(bounds):
        start = bi + 1
        end = bounds[b + 1] if b + 1 < len(bounds) else len(records)
        if end <= start:
            continue
        try:
            calls = enumerate_turn(records, start, end, is_sidechain_file)
        except Abort as e:
            aborts[str(e)] += 1
            continue
        if not calls:
            continue

        # Tail guard, as the rule applies it.
        span_end = max(c[1] for c in calls)
        tail_dropped = False
        if span_end == len(records) - 1:
            tail = next(c for c in calls if c[1] == span_end)
            calls = [c for c in calls if c is not tail]
            tail_dropped = True
        if not calls:
            continue

        base, kept, n = price_turn(records, calls, optimistic)
        rows.append({
            "file": path.stem, "kind": "agent" if is_agent else "main",
            "n": n, "base": base, "kept": kept, "saved": base - kept,
            # Gate features, in the units the rule can actually see (bytes/counts).
            "rm_bytes": removable_bytes(calls),
            "dg_bytes": digest_bytes(calls, PREVIEW_BUDGET),
            # A 1-call row is genuinely single-call unless the tail guard reduced
            # it. The distinction matters for the MinCalls=1 question: only the
            # untouched singles are turns a threshold of 1 would newly collapse,
            # since the tail-reduced ones are re-checked and bail either way.
            "tail_dropped": tail_dropped,
        })


# -------------------------------------------------------------------- report
def report(rows: list[dict], label: str) -> None:
    print(f"\n{'='*78}\n{label}\n{'='*78}")
    by_n = defaultdict(list)
    for r in rows:
        by_n[r["n"]].append(r)

    print(f"\n{'calls':>6} {'turns':>7} {'base tok':>11} {'saved tok':>11} "
          f"{'save%':>7} {'wins':>7} {'losses':>7} {'median saved':>13}")
    print("-" * 82)
    for n in sorted(by_n):
        g = by_n[n]
        base = sum(r["base"] for r in g)
        saved = sum(r["saved"] for r in g)
        wins = sum(1 for r in g if r["saved"] > 0)
        losses = sum(1 for r in g if r["saved"] <= 0)
        med = statistics.median(r["saved"] for r in g)
        pctv = 100.0 * saved / base if base else 0.0
        flag = "  <-- MinCalls" if n == 2 else ""
        print(f"{n:>6} {len(g):>7} {base:>11,} {saved:>11,} {pctv:>6.1f}% "
              f"{wins:>7} {losses:>7} {med:>13,.0f}{flag}")

    # The MinCalls=1 question specifically: a threshold of 1 would newly collapse
    # the GENUINE single-call turns. The tail-reduced 1-call rows are re-checked
    # against the reduced count and bail at any threshold >= 1... except that at
    # MinCalls=1 they would now pass. Both cohorts are therefore reported.
    ones = [r for r in rows if r["n"] == 1]
    genuine = [r for r in ones if not r["tail_dropped"]]
    reduced = [r for r in ones if r["tail_dropped"]]
    print("\n1-call band, split by origin (what MinCalls=1 would newly collapse)")
    print("-" * 78)
    for label, g in (("genuine single-call turns", genuine),
                     ("tail-guard-reduced (2 -> 1)", reduced)):
        if not g:
            continue
        saved = sum(r["saved"] for r in g)
        wins = sum(1 for r in g if r["saved"] > 0)
        print(f"  {label:<30} n={len(g):<5} saved={saved:>9,}  "
              f"wins={wins:<5} losses={len(g)-wins:<5} "
              f"median={statistics.median(r['saved'] for r in g):>8,.0f}")

    # Cumulative view: what a given MinCalls threshold yields corpus-wide.
    print(f"\n{'MinCalls':>9} {'turns kept':>11} {'saved tok':>12} {'vs base':>9} "
          f"{'net-neg turns':>14} {'tok lost to neg':>16}")
    print("-" * 78)
    total_base = sum(r["base"] for r in rows)
    for thr in range(1, 11):
        kept_rows = [r for r in rows if r["n"] >= thr]
        saved = sum(r["saved"] for r in kept_rows)
        neg = [r for r in kept_rows if r["saved"] <= 0]
        lost = sum(r["saved"] for r in neg)
        print(f"{thr:>9} {len(kept_rows):>11} {saved:>12,} "
              f"{100.0*saved/total_base if total_base else 0:>8.1f}% "
              f"{len(neg):>14} {lost:>16,}")


def gate_report(rows: list[dict], label: str) -> None:
    """Score candidate gates against the oracle.

    The ORACLE collapses iff the turn actually saves tokens — unreachable in
    practice (it needs the post-collapse token count), but it is the ceiling any
    heuristic is measured against. For each gate we report realized saving plus
    the two error classes: turns collapsed that shouldn't be (waste) and turns
    skipped that would have paid (missed).
    """
    print(f"\n{'='*98}\n{label}\n{'='*98}")
    total = sum(r["base"] for r in rows)
    oracle = sum(r["saved"] for r in rows if r["saved"] > 0)
    print(f"oracle ceiling (collapse iff saved>0): {oracle:,} tok  "
          f"({100.0*oracle/total:.2f}% of {total:,})\n")

    print(f"{'gate':<34} {'collapsed':>10} {'saved tok':>12} {'vs oracle':>10} "
          f"{'waste':>8} {'wasted tok':>11} {'missed':>7} {'missed tok':>11}")
    print("-" * 98)

    cands: list[tuple[str, callable]] = []
    for thr in (1, 2, 3, 4):
        cands.append((f"MinCalls>={thr}", lambda r, t=thr: r["n"] >= t))
    for margin in (1.0, 1.25, 1.5, 2.0):
        cands.append((f"payload: rm > {margin}x digest",
                      lambda r, m=margin: r["rm_bytes"] > m * r["dg_bytes"]))
    for thr in (2, 3):
        for margin in (1.0, 1.5):
            cands.append((f"n>={thr} OR rm>{margin}x digest",
                          lambda r, t=thr, m=margin:
                          r["n"] >= t or r["rm_bytes"] > m * r["dg_bytes"]))
    # The shipped rule, plus an AND form: floor AND economics must both hold.
    for margin in (1.0, 1.5):
        cands.append((f"n>=2 AND rm>{margin}x digest",
                      lambda r, m=margin: r["n"] >= 2 and r["rm_bytes"] > m * r["dg_bytes"]))

    for name, pred in cands:
        sel = [r for r in rows if pred(r)]
        saved = sum(r["saved"] for r in sel)
        waste = [r for r in sel if r["saved"] <= 0]
        missed = [r for r in rows if not pred(r) and r["saved"] > 0]
        print(f"{name:<34} {len(sel):>10} {saved:>12,} "
              f"{100.0*saved/oracle if oracle else 0:>9.1f}% "
              f"{len(waste):>8} {sum(r['saved'] for r in waste):>11,} "
              f"{len(missed):>7} {sum(r['saved'] for r in missed):>11,}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--corpus", type=Path, default=DEFAULT_CORPUS)
    ap.add_argument("--only", choices=["main", "agent"], default=None)
    ap.add_argument("--out", type=Path, default=None)
    args = ap.parse_args()

    groups = ["main", "agent"] if args.only is None else [args.only]
    files = []
    for g in groups:
        d = args.corpus / g
        if d.is_dir():
            files.extend((f, g == "agent") for f in sorted(d.glob("*.jsonl")))
    if not files:
        print(f"no corpus under {args.corpus}")
        return 1

    for optimistic in (True, False):
        rows: list[dict] = []
        aborts: Counter = Counter()
        for f, is_agent in files:
            analyze(f, is_agent, optimistic, rows, aborts)
        tag = "OPTIMISTIC previews (short)" if optimistic else "PESSIMISTIC previews (full 300-char budget)"
        report(rows, f"{len(files)} files, {len(rows)} collapsible turns — {tag}")
        gate_report(rows, f"CANDIDATE GATES vs ORACLE — {tag}")
        if optimistic:
            print(f"\nfail-closed aborts: {dict(aborts.most_common())}")
            for kind in ("main", "agent"):
                sub = [r for r in rows if r["kind"] == kind]
                if sub:
                    report(sub, f"{kind} only — {tag}")
        if args.out and not optimistic:
            args.out.write_text(json.dumps(rows, indent=1), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
