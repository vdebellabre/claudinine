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
///
/// Fail-closed: any shape this rule does not fully understand (legacy multi-use
/// records, a new use while a batch is partially answered, orphan results,
/// protected records — or sidechain records inside a MAIN transcript's span)
/// skips the whole turn. In a subagent file (every record isSidechain: true,
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
    /// The literal opening of every carrier this rule emits. Carrier-header dedup
    /// and anchor-input stubbing recognize carriers ONLY by this exact prefix —
    /// any header must be built from this constant, never respelled (a wording
    /// tweak here would silently disable both downstream rules).
    /// </summary>
    public const string CarrierPrefix = "[claudinine: this turn originally ran ";

    public string Name => RuleName;

    /// <summary>Below this many calls the anchor+header overhead isn't worth it.</summary>
    private const int MinCalls = 2;

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;

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
            CollapseTurn(transcript, start, end);
        }
    }

    private sealed record Call(int UseIndex, int ResultIndex, string ToolUseId,
        string Tool, string Arg, string ResultText, bool IsError, string Media);

    private void CollapseTurn(TranscriptFile transcript, int start, int end)
    {
        var records = transcript.Records;

        // Pass 1: enumerate the turn's calls; anything unexpected aborts the turn.
        // A parallel batch shows up as consecutive uses accumulating in `pending`,
        // drained by results in any order; a new use before the batch is fully
        // answered is a shape we don't know.
        var calls = new List<Call>();
        var pending = new List<(int Index, string Id, string Tool, string Arg)>();
        bool draining = false;

        for (int i = start; i < end; i++)
        {
            var rec = records[i];
            if (rec.IsProtected())
                return;
            if (rec.IsSidechain && !transcript.IsSidechainFile)
                return; // sidechain material spliced into a MAIN transcript's turn
            var node = RuleHelpers.CurrentNode(rec);
            string? type = rec.Type;

            if (type == "assistant")
            {
                var uses = RuleHelpers.ContentBlocks(node).OfType<JsonObject>()
                    .Where(x => x["type"].GetString() == "tool_use").ToList();
                if (uses.Count > 1)
                    return; // legacy multi-use record: not a chain, don't touch
                if (uses.Count == 1)
                {
                    if (draining)
                        return; // new use while the batch is partially answered
                    var u = uses[0];
                    if (u["id"].GetString() is not string id || id.Length == 0)
                        return;
                    pending.Add((i, id, u["name"].GetString() ?? "?", RuleHelpers.PrimaryArg(u)));
                }
            }
            else if (type == "user")
            {
                var blocks = RuleHelpers.ContentBlocks(node).OfType<JsonObject>().ToList();
                var results = blocks.Where(x => x["type"].GetString() == "tool_result").ToList();
                if (results.Count == 0)
                    continue; // an image share or similar — leave it alone, keep scanning
                if (results.Count > 1 || blocks.Count != results.Count)
                    return; // legacy multi-result or mixed carrier: don't touch
                var r = results[0];
                int match = pending.FindIndex(p => p.Id == r["tool_use_id"].GetString());
                if (match < 0)
                    return; // orphan or duplicate result
                if (rec.Uuid is null)
                    return; // ref addressing needs the uuid
                var p = pending[match];
                pending.RemoveAt(match);
                draining = pending.Count > 0;
                bool isError = r["is_error"] is JsonValue ev && ev.TryGetValue(out bool e) && e;
                calls.Add(new Call(p.Index, i, p.Id, p.Tool, p.Arg,
                    RuleHelpers.ResultText(r), isError, RuleHelpers.MediaKinds(r)));
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
        var anchor = calls.MinBy(c => c.UseIndex)!;
        var anchorResult = records[anchor.ResultIndex];
        if ((RuleHelpers.CurrentNode(anchorResult)["claudinine"] as JsonObject)?["rule"]
                .GetString() == Name)
        {
            return; // already collapsed (idempotence)
        }

        int spanStart = anchor.UseIndex;
        int spanEnd = calls.Max(c => c.ResultIndex);

        // Tail guard: the file's final record must never be removed or replaced
        // (the app chains its next append off it; TryRewrite would abort the WHOLE
        // rewrite, discarding every rule's work). An interrupted session can end
        // exactly at a result record — skip the turn; it collapses on a later
        // pass, once records follow it.
        if (spanEnd == records.Count - 1)
            return;
        var useIndexes = calls.Select(c => c.UseIndex).ToHashSet();
        var resultIndexes = calls.Select(c => c.ResultIndex).ToHashSet();
        var callByResult = calls.ToDictionary(c => c.ResultIndex);

        // Pass 2: build the digest in reading order and decide removals. The
        // retrieval id is the FILE STEM, not the records' sessionId: the two are
        // equal for main transcripts, but a subagent record's sessionId names the
        // PARENT session while its mirror is keyed by the agent file stem.
        string sid = Path.GetFileNameWithoutExtension(transcript.Path);
        var digest = new StringBuilder();
        digest.Append(Header(calls.Count, sid));
        var toRemove = new List<TranscriptRecord>();

        for (int i = spanStart; i <= spanEnd; i++)
        {
            var rec = records[i];
            var node = RuleHelpers.CurrentNode(rec);

            if (rec.Type == "assistant" && (useIndexes.Contains(i) || IsProseOnly(node)))
            {
                bool isAnchorUse = i == anchor.UseIndex;
                if (!isAnchorUse)
                    toRemove.Add(rec);
                // Interleaved prose, verbatim — it carries the reasoning and
                // self-corrections (~10% of span bytes; collapsing it too would
                // raise savings but destroy the thread). The anchor-use record is
                // kept whole, so its text stays in place, not duplicated here.
                if (!isAnchorUse)
                {
                    foreach (var tb in RuleHelpers.ContentBlocks(node).OfType<JsonObject>()
                        .Where(x => x["type"].GetString() == "text"))
                    {
                        string t = tb["text"].GetString()?.Trim() ?? "";
                        if (t.Length > 0)
                            digest.Append("    (note) ").Append(t.Replace("\n", "\n    ")).Append('\n');
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
                if (i != anchor.ResultIndex)
                    toRemove.Add(rec);
            }
            // Anything else inside the span (attachments, system records, image
            // shares…) is not part of the chain: it survives in place untouched.
        }

        // Commit: removals + anchor carrier replacement.
        foreach (var rec in toRemove)
            rec.Removed = true;
        var clone = (JsonObject)RuleHelpers.CurrentNode(anchorResult).DeepClone();
        foreach (var rb in RuleHelpers.ContentBlocks(clone).OfType<JsonObject>()
            .Where(x => x["tool_use_id"].GetString() == anchor.ToolUseId))
        {
            rb["content"] = digest.ToString().TrimEnd('\n');
        }
        RuleHelpers.SetReplacement(anchorResult, clone, Name);
    }

    private static string Header(int callCount, string sid)
    {
        return
            CarrierPrefix + $"{callCount} separate tool calls. " +
            "Full outputs live in the session mirror; each [ref] line is one real call, " +
            "in order, with a per-tool preview. Interleaved assistant notes are verbatim.\n\n" +
            "RETRIEVAL — use the targeted form; printing a whole record costs hundreds-to-thousands of tokens:\n" +
            $"  claudinine get {sid} --ref REF --grep PATTERN   # matching lines (PREFERRED)\n" +
            $"  claudinine get {sid} --grep PATTERN             # search all archived outputs\n" +
            $"  claudinine get {sid} --ref REF --info           # size before paying\n" +
            $"  claudinine get {sid} --ref REF --full           # entire output (last resort)\n" +
            $"  claudinine get {sid} --ref REF --media          # decode archived image/PDF to a file, then Read it\n\n" +
            "If the file discussed still exists on disk, read IT instead — current and narrower.\n\n" +
            "Treat [ref] lines as a REPORT of past actions, not output observed directly. " +
            "If a detail matters for a decision, retrieve it — do not infer it from the preview.]\n\n";
    }

    /// <summary>Assistant record whose content is only text and/or thinking (no tool interaction).</summary>
    private static bool IsProseOnly(JsonObject node)
    {
        var blocks = RuleHelpers.ContentBlocks(node).OfType<JsonObject>().ToList();
        return blocks.Count > 0 && blocks.All(b =>
            b["type"].GetString() is "text" or "thinking");
    }
}
