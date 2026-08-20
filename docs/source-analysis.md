# Source analysis — bugs, oversights, performance

Full-read review of `src/Claudinine` (~40 files), 2026-08-20, on `main` @ 2f2c8ae.
Baseline: build clean (0 warnings, analyzers on), all 369 tests pass. Findings below
were verified against the built binary (live repros) and the corpus where noted —
each is reproducible from this document alone.

Overall: an unusually disciplined codebase (fail-closed everywhere, mirror-first
invariant, content-based idempotence, contract-tested string protocol). The findings
are holes in that discipline, not an absence of it. Suggested order: fix B1 and B2
(both small, both confirmed), then O1/O2 as a follow-up, then weigh P1/P2 against
the perf bar.

---

## Confirmed bugs

### B1. Duplicate `<system-reminder>` in the just-submitted prompt aborts the whole pass

**Where:** `Rules/SystemReminderDedupRule.cs` (no tail guard) vs the refusal at
`Transcript/TranscriptFile.cs:209` (`tail-touched`).

**Mechanism.** The app appends the user prompt to the transcript BEFORE firing
`UserPromptSubmit`, so the new prompt is the file's tail record. If that prompt
carries a reminder identical to one seen earlier, `SystemReminderDedupRule`
(keep-first dedup) sets a Replacement on the tail record. `TryComputeRewrite`
then refuses `tail-touched` and discards EVERY rule's work for the pass.

**Repro** (run against the built binary; any build works):

```powershell
# transcript: user prompt with reminder, assistant text, user prompt (tail) with same reminder
# then:
'{"hook_event_name":"UserPromptSubmit","transcript_path":"...","session_id":"..."}' | claudinine hook
# with CLAUDININE_DEBUG=1:
#   rewrite refused: tail-touched
#   replaced=1 removed=0 rewriteOk=False
```

**Amplifier.** `HookRunner.cs:88` touches `PassStamp` unconditionally — even when
the rewrite was refused. The Stop repair pass within 120 s is therefore throttled
away too. In a session where every prompt repeats a static reminder (todo nudges,
plan-mode, empty-list nudges), per-prompt compaction can stall for whole stretches.
No data loss (mirroring runs before rules), but the core benefit quietly stops.

**Prevalence.** 10 of 97 corpus main transcripts carry an identical >=40-char
reminder block repeated across records; the tail-landing case is a subset of that,
and the corpus predates heavy todo-reminder usage.

**Fix.** Skip `records[^1]` in `SystemReminderDedupRule.Apply` — the same tail
guard CarrierHeaderDedupRule, ForkHealRule and ChainCollapseRule already have.
The duplicate is deduped on the next pass once it is no longer the tail.

**Same latent gap:** `Rules/DocumentDedupRule.cs` has no tail guard either —
rare (needs a >=1 KB duplicate block landing exactly in the tail record) but the
identical one-line fix applies. Note the existing tests never hit B1 because they
always append an `AssistantText` after the dup-bearing prompt; a regression test
needs the dup in the actual tail record.

### B2. `BashReadParser` mis-parses `head -n +N` — unsound supersession

**Where:** `Rules/BashReadParser.cs:138` — `int.TryParse(args[i + 1].TrimStart('+'), ...)`.

**Mechanism.** `head -n +5 f` parses as "covers lines 1–5". But `head -n +N` is
rejected by GNU/BSD head (the output is an error message), and where the form is
meaningful (tail semantics) it means "from line N to EOF" — never "first N lines".
The coverage claim is false in every interpretation.

**Repro (confirmed end-to-end):** a turn running `sed -n '1,5p' /tmp/f.txt`
followed, several reads later (outside the RecencyKeep window of 6), by
`head -n +5 /tmp/f.txt` — the sed result is stubbed
`[claudinine: file read superseded by a later read of /tmp/f.txt:1-5]` although
the "superseding" command's output contains none of those lines. This is exactly
the false-positive class the parser's fail-closed design exists to prevent; the
content survives only in the mirror, behind a stub that claims it is redundant.

**Fix.** Refuse `+N` outright (return null for the segment — the leading `+` is
the tell). Consider also refusing negative counts (`head -n -5`, GNU "all but the
last N"): currently harmless (a negative End never covers anything) but
semantically wrong and one condition away from mattering.

**Test gap:** `BashReadParserTests.cs` covers `head -n 50` and `tail` refusal,
nothing with a leading `+`.

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
