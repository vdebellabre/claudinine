# Source analysis — bugs, oversights, performance

Full-read review of `src/Claudinine` (~40 files), 2026-08-20, on `main` @ 2f2c8ae.
Baseline: build clean (0 warnings, analyzers on), all 369 tests pass.

**Revision 2 (same day):** the original B1 trigger and B2 were challenged by a
second review (Fable) and re-verified here. B1 is rescoped (mechanism real, trigger
much narrower than claimed), B2 is refuted and replaced by a different, genuine gap
(now B3). Both corrections are folded in below; the original claims are kept in
edited form only where they still hold.

Suggested order: B3 (soundness, small fix) → B1 tail guard (cheap, convention
consistency) → B2 pin test → O1/O2 → P1/P2 against the perf bar.

---

## Confirmed findings

### B1. Reminder dedup can touch the tail record — mechanism real, trigger narrow

**Where:** `Rules/SystemReminderDedupRule.cs` (no tail guard) vs the refusal at
`Transcript/TranscriptFile.cs:209` (`tail-touched`).

**Mechanism (confirmed, reproducible).** If the file's tail record carries a
`<system-reminder>` identical to one seen earlier, keep-first dedup sets a
Replacement on the tail → `TryComputeRewrite` refuses `tail-touched` → every
rule's work for the pass is discarded. Reproduced live against the built binary:

```
[claudinine debug] rewrite refused: tail-touched
[claudinine debug] replaced=1 removed=0 rewriteOk=False
```

Move the dup one record earlier and it dedups fine. The amplifier is real too:
`HookRunner.cs:88` touches `PassStamp` even on a refused rewrite (the `.pass`
file appears after the refused run), so the Stop repair pass within 120 s is
throttled away as well.

**Trigger (corrected).** The original claim — "at UserPromptSubmit the new prompt
is the tail record" — is FALSE. The prompt lands AFTER the hook: on a session's
first prompt the transcript does not exist yet; on later prompts the tail at hook
time is the previous turn's last record or a queue-operation dequeue (verified
empirically on CLI 2.1.235 by the second review; the codebase itself knows this —
`QueueHistoryCollapseRule.cs:21` guards exactly a queue-op tail "mid-flight at the
boundary we run on", and HookRunner calls UserPromptSubmit the workhorse for "the
turn that just ended"). The "per-prompt compaction stalls for whole stretches"
scenario therefore cannot happen.

**Real exposure.** A file that ENDS on a reminder-bearing user record — a session
killed mid-turn after the prompt was appended — refuses every repair pass
(SessionStart on resume, SessionEnd, PreCompact) until something is appended after
it. One refused pass per such boundary; compaction catches up once the turn
continues. Narrow, but the refusal is pure waste there.

**Fix (still worth doing, reduced urgency).** Skip `records[^1]` in
`SystemReminderDedupRule.Apply` — matches the four rules that already carry a
tail guard; the duplicate is deduped on the next pass once it is no longer the
tail. `Rules/DocumentDedupRule.cs` has the same latent gap (rare: needs a >=1 KB
duplicate block landing exactly in the tail record); identical one-line fix.

**Test gap:** existing tests always append an `AssistantText` after the
dup-bearing prompt, so the tail case is never exercised.

### B2. `head -n +N` parsing — REFUTED, do not change

The original review claimed `head -n +N` is rejected by GNU/BSD head, making the
parser's coverage claim at `Rules/BashReadParser.cs:138` (`TrimStart('+')`)
unsound. That is wrong: GNU head 8.32 accepts `head -n +5` and prints the FIRST
5 lines (re-verified here via Git Bash: `head (GNU coreutils) 8.32`, `head -n +3`
of a 7-line input prints lines 1–3, exit 0) — exactly what `TrimStart('+')`
assumes. The supersession is sound on GNU head. The original "end-to-end repro"
only proved the rule trusts the parse, which was never in doubt.

Two things survive from this thread:

- **Pin test (do it).** `BashReadParserTests.cs` covers `head -n 50` and `tail`
  refusal but nothing with a leading `+`. Add a case pinning `head -n +5 f` →
  `(f, 1, 5)`, with a comment naming the GNU semantics, so nobody "fixes" this
  again. Open question for the pin, UNVERIFIED here (no macOS host available):
  whether BSD/macOS head accepts `+N` at all. If it rejects it, the command
  errors at runtime on those hosts — which is exactly the failed-superseder case
  B3 below must catch, and one more reason B3 matters.
- **B3**, the genuine gap noticed in passing — see next.

### B3. Supersession ignores `is_error` — a failed later read retires a good earlier one

**Where:** `Rules/ReadSupersessionRule.cs` (shared engine of `BashReadDedupRule`
and `ReadToolDedupRule`).

**Mechanism.** Pass 1 (lines 28–44) collects read targets from `tool_use` blocks
ONLY — the rule never looks at the superseding read's result. A later read whose
`tool_result` carries `is_error: true` still contributes coverage targets in pass
2, so a FAILED read of file F can stub an earlier SUCCESSFUL read of F as
"superseded" although the failed read returned no file content at all. The parser
is fail-closed about the COMMAND text by design; this hole is orthogonal to it —
the command parsed fine, the execution failed.

**Trigger cases.** Any read of a file deleted or permission-lost between the two
reads; a range read that errors at runtime for any other reason (a plausible one:
`head -n +N` on hosts whose head rejects the form — unverified, see the B2 pin
note). All parse as valid pure-read targets, all claim coverage, all retire
earlier good reads. Same harm class the parser's fail-closed stance exists to
prevent: content survives only in the mirror, behind a stub that claims a later
read has it.

**Fix sketch.** Build a `tool_use_id → is_error` map from `tool_result` blocks in
the same scan, and exclude error reads from being SUPERSEDERS in pass 2 (they may
still be stubbed when a valid later read covers them). One change covers both
subclass rules.

---

## Oversights

### O1. `CarrierHeaderDedupRule` segment reset ignores `microcompact_boundary`

**Where:** `Rules/CarrierHeaderDedupRule.cs:82-87` — the segment reset checks
`subtype == "compact_boundary"` only.

Every other boundary treatment in the codebase pairs the two subtypes
(`TranscriptRecord.IsProtected`, `TranscriptFile.MarkPreserved`). If the app
slices context from a microcompact boundary the same way, every short header after
one points at a RETRIEVAL block the model can no longer see — the exact E8 failure
mode this rule exists to prevent (docs/cowork-compatibility.md).

The corpus holds no real microcompact records yet (only quoted source text), so
this is preemptive; the fix is one pattern: `is "compact_boundary" or
"microcompact_boundary"`. Worth canarying what the loader actually does at a
microcompact boundary before or alongside the change.

### O2. Chain-collapse drops `thinking` blocks without a note

**Where:** `Rules/ChainCollapseRule.cs:326` — note lines collect only `text`
blocks; a removed prose-only assistant record's `thinking` blocks vanish from the
digest with no trace. (`IsProseOnly` admits thinking-only records, which are then
removed with zero digest footprint.)

Not loss — the mirror holds them — but the class doc says interleaved prose is
kept VERBATIM and the product stance is "nothing is thrown away". Either carry a
note line for thinking too, or document the deliberate drop (thinking is bulky,
ephemeral, and MegaBlockTrimRule's doc notes thinking blocks are signed — a
reason to not carry them verbatim into a tool result).

---

## Performance

### P1. Statusline re-reads the entire mirror + transcript on every render

**Where:** `StatuslineVerb.cs:170-187` (`Measure`), via `LoadStamp.ScanRecordSizes`.

The statusline is spawned per assistant message (no in-process cache survives
between renders). Each render walks the transcript AND the mirror line by line
with substring uuid extraction. On a session with a 40 MB mirror that is a
multi-MB read per message, on the user-visible status bar path.

The mirror is append-only: a persistent uuid→size sidecar keyed by the mirror's
byte length — the exact `SeenCache` pattern already in the repo — would eliminate
the mirror half of the scan. The transcript half must stay live (the file mutates
in place) but is the smaller, compacted side.

### P2. `ReadToolDedupRule.DefaultReadLimit = 2000` hardcodes the app's default

**Where:** `Rules/ReadToolDedupRule.cs:15`.

If Claude Code ever changes its default read limit, coverage claims silently go
wrong in the over-claim direction (a later read credited with lines it never
returned). The truth is already in the transcript: `toolUseResult.file` carries
`startLine`/`numLines`/`totalLines` — `EditedTextFileSupersessionRule` reads
exactly that shape. Deriving actual coverage from the result record (of the later
read) instead of assuming the default would remove the fragile cross-app coupling.

### P3. Peak memory on huge transcripts

**Where:** `Transcript/TranscriptFile.cs:43-74` (`TryLoad`/`TryParseText`).

Load holds byte[] + decoded string + `Split('\n')` arrays simultaneously (~3× file
size) before parsing begins. Fine at corpus scale (largest transcript: 14.9 MB),
but Cowork cloud sessions can grow far beyond; a streaming line walk would bound
it. Low priority — the rewrite needs all lines in memory anyway, so the win is
only the load-time peak, not the steady-state footprint.

### P4. Minor

- `LocalCowork.RefsDirFor` does its ancestor walk + `Directory.Exists` probes
  4+ times per pass from different classes (Compactor, ChainCollapseRule,
  ImageStripRule, CarrierHeaderDedupRule). Memoizable per process per path.
- `ChainCollapseRule`'s `pending.FindIndex` is O(n²) per turn — negligible at
  realistic batch sizes, noted for completeness.

---

## Checked and found solid (do not re-audit)

- Concurrency: PassLock semantics (Windows sharing violation / Unix flock via
  FileShare.None), lock-file lifecycle and GC, busy-skip economics.
- Mirror-first invariant and the `MirrorLost` tripwire (all three sid phrase
  forms, JSON-escaped launcher fragment included).
- SeenCache fail-closed validation (length-keyed, torn-append rejection).
- Atomic swap + length re-check race handling in `TryRewrite`; rechain and
  reachability-delta validation.
- Uuid-less record identity: corpus confirms no uuid-less record carries a
  `parentUuid`, so the leafUuid-exclusion hash in `MirrorFile.IdentityOf` is
  sufficient (a rechained parentUuid cannot change an h: identity in practice).
- Fork-heal fork-vs-quote validation, mirror adoption, restore/clone/GC paths.
- CRLF preservation, strict-UTF8 + BOM refusal at load.
- Economics gate scoping (replacedBytes vs noteBytes vs headerDedupSaving) and
  its idempotence argument.
- Hook-time transcript shape: at `UserPromptSubmit` the new prompt is NOT yet in
  the file (verified empirically, CLI 2.1.235); the tail is the previous turn's
  last record or a queue-operation dequeue. Do not re-derive the opposite.
- `head -n +N` under GNU coreutils (8.32) prints the FIRST N lines; the parser's
  `TrimStart('+')` models that correctly. Do not "fix" without re-testing.
