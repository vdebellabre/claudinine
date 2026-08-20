namespace Claudinine.Rules;

/// <summary>
/// Turn-span chain collapse — the headline strategy (POC: ~57% token cut). Inside
/// an aged turn, the first→last tool-call span collapses to its FIRST
/// tool_use/tool_result pair (real ids, no synthetic tool — a tool_result cannot
/// exist without its tool_use); every other call becomes one [ref] preview line
/// merged into that anchor result, interleaved assistant prose is kept VERBATIM as
/// note lines, and the remaining pairs are dropped whole. Full outputs stay in the
/// session mirror, addressed by uuid prefix via `claudinine get`.
///
/// Parallel batches (modern format, v2.1.222+): each tool_use is its OWN
/// assistant record; a batch is a run of consecutive use records followed by its
/// results in COMPLETION order, each result parented to its own use record (the
/// chain forks; one result per batch is a dead-end leaf). Batch calls collapse
/// like sequential ones; pairs are removed atomically (a kept result whose use
/// was removed would dangle its tool_use_id and sourceToolAssistantUUID).
/// Batches may INTERLEAVE (use A, use B, result B, use C, result A…) and still
/// collapse: pairing is by tool_use_id, and the span — first use → last result —
/// is a superset of every pair however the calls overlap. Consequence: pairs are
/// NOT span-local, so nothing downstream may assume two pairs never overlap.
///
/// Fail-closed: any shape this rule does not fully understand (legacy multi-use
/// records, orphan results, protected records — or sidechain records inside a
/// MAIN transcript's span) skips the whole turn. In a subagent file (every record isSidechain: true,
/// see TranscriptFile.IsSidechainFile) the sidechain guard is off: the
/// sidechain IS the conversation there, and these files are exactly the
/// dense-span shape this rule is strongest on. Digest headers address
/// retrieval by the FILE STEM (= mirror key: session id for main files,
/// agent-&lt;id&gt; for subagent files) — a subagent record's sessionId names its
/// PARENT session, whose mirror never holds these records.
/// </summary>
internal sealed class ChainCollapseRule : ICompactionRule
{
    public const string RuleName = "chain-collapse";

    /// <summary>
    /// The literal opening of every carrier this rule emits. Canonical value in
    /// <see cref="Protocol"/> (aliased here so call sites read as "the chain-collapse
    /// prefix"); see Protocol's class doc for who else depends on it.
    /// </summary>
    public const string CarrierPrefix = Protocol.CarrierPrefix;

    public string Name => RuleName;

    /// <summary>
    /// The call-count phrase that follows <see cref="CarrierPrefix"/> in every header,
    /// full or slimmed. Singular matters now that the economics gate admits single-call
    /// turns (a fat Read, a base64 screenshot pays for its own header), so "ran 1
    /// separate tool calls" is reachable text rather than a theoretical case. Shared
    /// with <see cref="CarrierHeaderDedupRule"/> so slimming a singular carrier cannot
    /// silently re-pluralize it; takes the count as a STRING because the dedup rule
    /// recovers it by parsing the header it is rewriting.
    /// </summary>
    internal static string CallCountPhrase(string callCount) =>
        callCount == "1" ? "1 tool call" : $"{callCount} separate tool calls";

    /// <summary>
    /// Structural floor only: one settled pair is the least this rule can act on.
    /// Whether collapsing PAYS is not decided here — see the economics gate at the
    /// end of CollapseTurn, which compares the built digest against the payload it
    /// would replace. A count threshold was the old proxy for that question
    /// (MinCalls was 2); measured over the 174-file corpus that proxy is wrong in both
    /// directions — it collapsed 64 turns at a net loss and skipped 40 single-call
    /// turns that pay handsomely (one large result).
    ///
    /// Measured payoff on the 174-file corpus: 69.1% tokens / 77.5% bytes versus
    /// 68.9% / 77.4% for MinCalls=2. The gate first measured as a NEUTRAL trade;
    /// that was a pricing bug, not a structural limit — removed tool_use records
    /// were credited at their preview argument's length (Call.Arg) instead of the
    /// full serialized input the removal deletes, which refused every edit-heavy
    /// turn (multi-KB old/new strings, one-line sentinel results; see
    /// EditHeavyTurnWithSentinelResultsCollapses). Priced correctly, the corpus
    /// replay reaches 100% of the collapse-iff-it-pays oracle under both preview
    /// bounds, refusing zero profitable turns.
    /// </summary>
    private const int MinCalls = 1;

    /// <summary>
    /// The digest must beat the payload it replaces by this factor to be worth it.
    /// Above 1.0 on purpose: the comparison is in UTF-8 bytes (no tokenizer here),
    /// and bytes-per-token differs slightly between raw tool output and a digest of
    /// it, so a flat margin absorbs the divergence without tuning. Measured over the
    /// corpus, anything in 1.0–2.0 lands within ~0.02 pts, i.e. the exact value is
    /// not load-bearing — do not "optimize" it against a single snapshot.
    /// </summary>
    private const double MinGain = 1.1;

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;

        // The retrieval address the header's command lines embed — launcher path
        // normally, refs-dir path in Cowork local mode (where no shell command
        // can be trusted; see LocalCowork). Both are per-FILE constants, so every
        // turn's verdict stays a pure function of its own content plus file
        // identity (the property idempotence depends on).
        string launcher = Launcher.HeaderPathFor(transcript.Path);
        string? refsDir = LocalCowork.HeaderRefsDirFor(transcript.Path);
        string sid = Path.GetFileNameWithoutExtension(transcript.Path);

        // Bytes the retrieval header shrinks by once CarrierHeaderDedupRule slims
        // a carrier (full instructions → one-line form). The economics gate
        // discounts it from EVERY carrier: all but the segment's first really are
        // slimmed, and making the discount conditional on that would tie a turn's
        // verdict to what an earlier pass wrote, breaking idempotence (see the
        // gate). Derived from the two real header texts rather than hard-coded,
        // so it cannot drift when either is reworded — and computed with the real
        // sid and retrieval address, since the full header embeds both.
        int headerDedupSaving =
            RuleHelpers.Utf8Len(Header(99, sid, launcher, refsDir))
            - RuleHelpers.Utf8Len(CarrierHeaderDedupRule.ShortHeaderFor("99"));

        // Turn boundaries: a REAL user message (plain-string content) is a hard
        // boundary; tool-result carriers use a list. Getting this wrong makes the
        // whole session look like one turn.
        var bounds = new List<int>();
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i].IsRealUserMessage())
                bounds.Add(i);
        }

        // No age gate — deliberately. The app never re-reads the file mid-session
        // (canary-verified), so collapsing the freshest turn affects nothing live;
        // the only consumer is the next load, and the POC's live test showed a
        // fully-collapsed session retrieves rather than guesses. The in-flight
        // guard below (a use with no result) is structural, not temporal.
        for (int b = 0; b < bounds.Count; b++)
        {
            int start = bounds[b] + 1;
            int end = b + 1 < bounds.Count ? bounds[b + 1] : records.Count;
            if (end <= start)
                continue;
            CollapseTurn(transcript, start, end, sid, launcher, refsDir, headerDedupSaving);
        }
    }

    // Struct on purpose: one is created per tool call in the hottest rule of the
    // pipeline (largest allocator per eng/bench/profiling-notes.md — the pass is
    // GC-bound), same pattern as RestoreVerb.Line and BashReadParser.ReadTarget.
    // InputBytes is the FULL serialized input, not Arg's length: Arg is the
    // one-line preview argument (file_path for Edit/Write), while removing the
    // use record deletes the whole input — old_string/new_string/content included.
    // The economics gate must price what is actually removed; pricing it at Arg
    // refused every edit-heavy turn (sentinel results + preview-sized credit).
    private readonly record struct Call(int UseIndex, int ResultIndex, string ToolUseId,
        string Tool, string Arg, string ResultText, bool IsError, string Media, int MediaBytes,
        int InputBytes);

    private void CollapseTurn(TranscriptFile transcript, int start, int end,
        string sid, string launcher, string? refsDir, int headerDedupSaving)
    {
        var records = transcript.Records;

        // Pass 1: enumerate the turn's calls; anything unexpected aborts the turn.
        // A parallel batch shows up as consecutive uses accumulating in `pending`,
        // drained by results in any order. Batches may also INTERLEAVE — a new use
        // issued while an earlier one is still unanswered (real specimen: a slow
        // Bash overlapping later Reads) — which is fine here: `pending` is keyed by
        // tool_use_id, so pairing never depends on adjacency, and Pass 2 classifies
        // by record index rather than by batch. The only temporal requirement is
        // that the turn be SETTLED, enforced after the loop by `pending.Count > 0`.
        var calls = new List<Call>();
        var pending = new List<(int Index, string Id, string Tool, string Arg, int InputBytes)>();

        for (int i = start; i < end; i++)
        {
            var rec = records[i];
            if (rec.IsProtected())
                return;
            if (rec.IsSidechain && !transcript.IsSidechainFile)
                return; // sidechain material spliced into a MAIN transcript's turn
            var node = rec.CurrentView;
            string? type = rec.Type;

            if (type == "assistant")
            {
                var uses = RuleHelpers.BlocksOfType(node, "tool_use").ToList();
                if (uses.Count > 1)
                    return; // legacy multi-use record: not a chain, don't touch
                if (uses.Count == 1)
                {
                    var u = uses[0];
                    if (u["id"].AsString() is not string id || id.Length == 0)
                        return;
                    pending.Add((i, id, u["name"].AsString() ?? "?", RuleHelpers.PrimaryArg(u),
                        u["input"].Exists ? u["input"].SerializedLength() : 0));
                }
            }
            else if (type == "user")
            {
                var blocks = RuleHelpers.ContentBlocks(node).Where(x => x.IsObject).ToList();
                var results = blocks.Where(x => x["type"].AsString() == "tool_result").ToList();
                if (results.Count == 0)
                    continue; // an image share or similar — leave it alone, keep scanning
                if (results.Count > 1 || blocks.Count != results.Count)
                    return; // legacy multi-result or mixed carrier: don't touch
                var r = results[0];
                int match = pending.FindIndex(p => p.Id == r["tool_use_id"].AsString());
                if (match < 0)
                    return; // orphan or duplicate result
                if (rec.Uuid is null)
                    return; // ref addressing needs the uuid
                var p = pending[match];
                pending.RemoveAt(match);
                calls.Add(new Call(p.Index, i, p.Id, p.Tool, p.Arg,
                    RuleHelpers.ResultText(r), r["is_error"].IsTrue, RuleHelpers.MediaKinds(r),
                    RuleHelpers.MediaBytes(r), p.InputBytes));
            }
        }
        if (pending.Count > 0)
            return; // in-flight call with no result: not a settled turn
        if (calls.Count < MinCalls)
            return;

        // Anchor = the pair of the FIRST use in file order. In a batch whose
        // results arrived out of order, the first RESULT's pair would leave the
        // batch's first use outside the span (its result removed, the use kept —
        // API-invalid); the first USE's pair also keeps the survivor chain linear.
        var anchor = calls.MinBy(c => c.UseIndex);
        var anchorResult = records[anchor.ResultIndex];
        // Idempotence, by CONTENT not by stamp. The envelope's `rule` key names
        // whichever rule wrote the record LAST, and later rules legitimately rewrite
        // a carrier (mega-block-trim retrims a huge digest), which overwrites
        // "chain-collapse" and made a stamp test miss. A carrier re-enumerates as a
        // perfectly ordinary 1-call turn, so on the next pass it was collapsed into a
        // digest-OF-a-digest — measured on 00b42a12: two carriers of 79 and 159 [ref]
        // lines each rewritten down to "1 tool call", silently destroying 236 real
        // references. Harmless while MinCalls was 2 (one re-found call fell below the
        // threshold) and load-bearing now that a single call can collapse.
        if (calls.Any(c => RuleHelpers.IsCarrier(c.ResultText)))
            return; // already collapsed (idempotence)

        int spanStart = anchor.UseIndex;
        int spanEnd = calls.Max(c => c.ResultIndex);

        // Tail guard: the file's final record must never be removed or replaced
        // (the app chains its next append off it; TryRewrite would abort the WHOLE
        // rewrite, discarding every rule's work).
        //
        // Rather than skip the whole turn, drop the tail-touching call from the
        // batch and collapse the rest: the last pair stays verbatim, the span ends
        // before it. This is what saves WORKFLOW subagent transcripts, whose single
        // turn runs to EOF and whose final record IS a tool_result — the agent's
        // last act is a tool call whose result returns to the orchestrator, so the
        // turn never closes with an assistant message. Skipping cost 100% of the
        // yield on those files; excluding one call of 5–29 costs 3–20%.
        // (A plain Agent-tool transcript ends on assistant/text and never hits this.)
        //
        // The excluded pair could in principle be STUBBED in place instead — the
        // enforced invariant is only that the tail keeps its uuid, and TryRewrite
        // already rewrites the tail's parentUuid via tailRewritten. It is refused
        // today by `Replacement is not null` in TryRewrite, which is stricter than
        // the invariant needs. Deliberately not pursued: it would also have to stub
        // the pair's tool_use in place (removing it dangles sourceToolAssistantUUID,
        // which is not an ancestry link and cannot be remapped), and the reachability
        // guard can no more validate a replaced tail than it can a fork.
        if (spanEnd == records.Count - 1)
        {
            var tailCall = calls.First(c => c.ResultIndex == spanEnd);
            calls.Remove(tailCall);
            // Re-check against the REDUCED count. Structural only now: dropping the
            // last call can empty the batch. Whether the remainder is worth
            // collapsing is the economics gate's call, not a count's.
            if (calls.Count < MinCalls)
                return;
            // The anchor cannot move: it is the first use in file order and only the
            // LAST call was dropped (a one-call batch already returned above), so
            // spanStart stands — and the idempotence check above, which reads the
            // stub marker off the anchor's result, stays valid for this span.
            spanEnd = calls.Max(c => c.ResultIndex);
        }
        var useIndexes = calls.Select(c => c.UseIndex).ToHashSet();
        var resultIndexes = calls.Select(c => c.ResultIndex).ToHashSet();
        var callByResult = calls.ToDictionary(c => c.ResultIndex);
        var callByUse = calls.ToDictionary(c => c.UseIndex);

        // Pass 2: build the digest in reading order and decide removals. The
        // retrieval id is the FILE STEM (passed in as sid), not the records'
        // sessionId: the two are equal for main transcripts, but a subagent
        // record's sessionId names the PARENT session while its mirror is keyed
        // by the agent file stem.
        var digest = new StringBuilder();
        digest.Append(Header(calls.Count, sid, launcher, refsDir));
        var toRemove = new List<TranscriptRecord>();

        // Two byte counters for the economics gate, and they must be scoped
        // IDENTICALLY or the comparison is meaningless.
        //
        // replacedBytes: payload the collapse actually removes — every result's text
        // (non-anchor ones with their record, the anchor's by being overwritten) plus
        // removed tool_use inputs.
        //
        // noteBytes: the (note) lines the digest re-emits VERBATIM. Interleaved prose
        // survives the collapse unchanged, so it is on BOTH sides of the trade and is
        // not a saving — but it IS inside the built digest. Comparing the whole digest
        // against a prose-free payload therefore charges collapse for bytes it does not
        // add, and refuses prose-heavy turns that pay. Measured cost of getting this
        // wrong: 273 profitable turns refused, 432,914 tokens forgone, corpus saving
        // down 2.3 pts.
        int replacedBytes = 0;
        int noteBytes = 0;

        for (int i = spanStart; i <= spanEnd; i++)
        {
            var rec = records[i];
            var node = rec.CurrentView;

            if (rec.Type == "assistant" && (useIndexes.Contains(i) || IsProseOnly(node)))
            {
                bool isAnchorUse = i == anchor.UseIndex;
                if (!isAnchorUse)
                {
                    toRemove.Add(rec);
                    // A removed use record takes its FULL tool_use input with it.
                    if (useIndexes.Contains(i))
                        replacedBytes += callByUse[i].InputBytes;
                }
                // Interleaved prose, verbatim — it carries the reasoning and
                // self-corrections (~10% of span bytes; collapsing it too would
                // raise savings but destroy the thread). The anchor-use record is
                // kept whole, so its text stays in place, not duplicated here.
                // Only TEXT blocks: thinking blocks are deliberately dropped with
                // no digest trace (mirror keeps them). They are bulky, ephemeral
                // by design, and cryptographically signed — quoting one into a
                // tool result would carry an unverifiable signature payload.
                if (!isAnchorUse)
                {
                    foreach (var tb in RuleHelpers.BlocksOfType(node, "text"))
                    {
                        string t = tb["text"].AsStringMemo()?.Trim() ?? "";
                        if (t.Length > 0)
                        {
                            string indented = t.Replace("\n", "\n    ");
                            digest.Append("    (note) ").Append(indented).Append('\n');
                            // Carried verbatim, so it is not part of the trade — see
                            // noteBytes. Counts the indented form actually appended,
                            // frame included, and reuses the one string already built
                            // (this rule is the pipeline's largest allocator).
                            noteBytes += RuleHelpers.Utf8Len(indented) + "    (note) \n".Length;
                        }
                    }
                }
            }
            else if (resultIndexes.Contains(i))
            {
                var c = callByResult[i];
                string refId = RuleHelpers.RefPrefix(records[c.ResultIndex].Uuid!);
                string preview = PreviewRenderer.RenderPreview(c.Tool, c.Arg, c.ResultText, c.IsError);
                // Media blocks are invisible to text extraction — without this
                // note a screenshot-only result digests as "(no output)".
                string media = c.Media.Length > 0 ? $" [+media {c.Media} — --media decodes it to a file]" : "";
                digest.Append($"[{refId}] {c.Tool}({RuleHelpers.Truncate(c.Arg, 90)}) -> {RuleHelpers.Utf8Len(c.ResultText)}b :: {RuleHelpers.Truncate(preview, 300)}{media}\n");
                // Every result's payload goes away: the non-anchor ones with their
                // record, the anchor's by being overwritten with the digest. Media
                // counts — a base64 screenshot is invisible to ResultText but is the
                // heaviest thing in the span, and pricing it at zero would refuse
                // exactly the turns most worth collapsing.
                replacedBytes += RuleHelpers.Utf8Len(c.ResultText) + c.MediaBytes;
                if (i != anchor.ResultIndex)
                    toRemove.Add(rec);
            }
            // Anything else inside the span (attachments, system records, image
            // shares…) is not part of the chain: it survives in place untouched.
        }

        // Economics gate — the whole decision, on real bytes rather than a proxy.
        // Nothing above this line mutated anything (the digest is a StringBuilder and
        // toRemove is just a list), so bailing here leaves the turn untouched. This
        // placement is load-bearing: it is the only point where BOTH sides of the
        // trade are known exactly, which is what lets the rule skip the count
        // heuristic entirely. Keep every mutation below it.
        //
        // Two corrections turn the raw digest length into what collapse actually costs.
        //
        // Subtract the verbatim (note) lines: they are carried, not added, so charging
        // them to the digest would compare an inflated cost against a prose-free
        // payload and refuse turns that genuinely pay.
        //
        // Also discount the retrieval header down to what it will really cost. This
        // rule writes the full ~1.1KB instructions into every carrier, but
        // CarrierHeaderDedupRule runs immediately after and slims all but the file's
        // first carrier to a one-line header. Charging every turn the full header
        // over-prices each later carrier by ~1KB and refuses many-small-call turns that
        // are in fact profitable — measured: 18 files regressed, 57a4cdbf alone losing
        // 20,737 tokens (-10.9 pts) with 9 of 23 turns wrongly refused.
        //
        // The discount is applied UNCONDITIONALLY, not just to non-first carriers.
        // Pricing it by "is this the file's first carrier?" makes the verdict depend on
        // what a PREVIOUS pass already wrote: a turn refused on pass 1 (full header, no
        // carrier yet) sees the discount on pass 2 (carrier now present) and collapses,
        // so the rewrite is not a fixpoint and the idempotence guard fails. Amortizing
        // the one full header across the file instead keeps every turn's verdict a pure
        // function of its own content — the property idempotence depends on. The
        // residual error is bounded by a single header on the one turn that keeps it.
        string body = digest.ToString().TrimEnd('\n');
        int digestCost = RuleHelpers.Utf8Len(body) - noteBytes - headerDedupSaving;
        if (digestCost * MinGain >= replacedBytes)
        {
            Dbg.Log($"chain-collapse: turn at {spanStart} not worth collapsing " +
                    $"(digest {digestCost}b vs payload {replacedBytes}b, {calls.Count} calls)");
            return;
        }

        // Commit: removals + anchor carrier replacement.
        foreach (var rec in toRemove)
            rec.Removed = true;
        var clone = anchorResult.CloneCurrentNode();
        foreach (var rb in RuleHelpers.ContentBlocks(clone).OfType<JsonObject>()
            .Where(x => x["tool_use_id"].GetString() == anchor.ToolUseId))
        {
            rb["content"] = body;
        }
        RuleHelpers.SetReplacement(anchorResult, clone, Name);
    }

    /// <summary>
    /// Opens the header's command block; everything between this line and
    /// <see cref="CommandBlockEnd"/> is exactly <see cref="CommandLines"/>'
    /// output — the region CarrierHeaderDedupRule's self-heal regenerates when
    /// the launcher path goes stale (the tree moved). Both sentinels appear
    /// verbatim in pre-launcher (0.1.x/0.2.x) headers too, which is what lets
    /// the heal upgrade those in place.
    /// </summary>
    internal const string CommandBlockStart =
        "RETRIEVAL — use the targeted form; printing a whole record costs hundreds-to-thousands of tokens:\n";

    internal const string CommandBlockEnd =
        "If the file discussed still exists on disk, read IT instead — current and narrower.\n\n";

    /// <summary>
    /// The five retrieval commands. Invoked through the per-session launcher
    /// (see Mirror/Launcher.cs) rather than a bare `claudinine`, because a
    /// hosted (claude.ai/Cowork) install has no PATH entry at all. `sh` rather
    /// than direct execution makes a lost exec bit survivable; the quotes keep
    /// paths with spaces working. Every matcher that recognizes our retrieval
    /// commands (Compactor.MirrorLost, ForkHealRule, CloneVerb, header dedup's
    /// sid parse) accepts BOTH this form and the bare pre-launcher form —
    /// transcripts compacted by 0.1.x/0.2.x carry the old phrasing forever.
    /// The REF line binds the placeholder to the bracketed 8-hex id the [ref]
    /// lines actually show; without it nothing in the header says what REF is
    /// (measured failure mode: retrieval never attempted, docs/cowork E8).
    /// </summary>
    internal static string CommandLines(string sid, string launcher) =>
        $"  sh \"{launcher}\" get {sid} --ref REF --grep PATTERN   # matching lines (PREFERRED)\n" +
        $"  sh \"{launcher}\" get {sid} --grep PATTERN             # search all archived outputs\n" +
        $"  sh \"{launcher}\" get {sid} --ref REF --info           # size before paying\n" +
        $"  sh \"{launcher}\" get {sid} --ref REF --full           # entire output (last resort)\n" +
        $"  sh \"{launcher}\" get {sid} --ref REF --media          # decode archived image/PDF to a file, then Read it\n" +
        "  REF = the 8-hex id in [brackets]: [ab12cd34] -> --ref ab12cd34\n\n";

    /// <summary>
    /// The local-mode (Cowork "On your computer") command block: same sentinels,
    /// different verbs. There the only shell is a Linux microVM that is usually
    /// down and could not run the host-path launcher anyway, while the model's
    /// HOST-side Read/Grep tools can reach `outputs/` — so retrieval is plain
    /// file access against the RefsDump tree. The `mirror key:` clause carries
    /// the sid this block would otherwise not name; ForkHealRule and
    /// Compactor.MirrorLost both match it (their third accepted form).
    /// </summary>
    internal static string LocalCommandLines(string sid, string refsDir) =>
        $"  DIR = {refsDir}   (mirror key: {sid})\n" +
        "  REF = the 8-hex id in [brackets]: [ab12cd34] -> ab12cd34\n" +
        "  Grep DIR/REF.txt for a pattern      # matching lines (PREFERRED)\n" +
        "  Grep across DIR                     # search all archived outputs\n" +
        "  Read DIR/REF.txt                    # entire output (offset/limit to page)\n" +
        "  Read DIR/REF-media-N.png|.jpg|.pdf  # archived media, viewable via Read\n" +
        "  Use your Read/Grep FILE TOOLS with the literal DIR path — this session's shell cannot run retrieval commands.\n\n";

    // The header must NEVER say that assistant-authored content is spliced or
    // quoted inside this tool result — in any wording. Fable 5's API-side
    // safeguards read such a sentence as an assistant-impersonation injection
    // and block EVERY resume of the session (bisected empirically 2026-08-15:
    // the removed sentence alone flipped the verdict on an otherwise untouched
    // transcript, and a reworded variant was flagged too; see
    // CarrierHeaderDedupRule.LegacySentence, which heals it out of carriers
    // written by older versions).
    private static string Header(int callCount, string sid, string launcher, string? refsDir)
    {
        return
            CarrierPrefix + $"{CallCountPhrase(callCount.ToString())}. " +
            "Full outputs live in the session mirror; each [ref] line is one real call, " +
            "in order, with a per-tool preview.\n\n" +
            CommandBlockStart +
            (refsDir is null ? CommandLines(sid, launcher) : LocalCommandLines(sid, refsDir)) +
            CommandBlockEnd +
            "Treat [ref] lines as a REPORT of past actions, not output observed directly. " +
            "If a detail matters for a decision, retrieve it — do not infer it from the preview.]\n\n";
    }

    /// <summary>Assistant record whose content is only text and/or thinking (no tool interaction).</summary>
    private static bool IsProseOnly(JsonView node)
    {
        var blocks = RuleHelpers.ContentBlocks(node).Where(b => b.IsObject).ToList();
        return blocks.Count > 0 && blocks.All(b =>
            b["type"].AsString() is "text" or "thinking");
    }
}
