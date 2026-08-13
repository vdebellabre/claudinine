namespace Claudinine.Transcript;

/// <summary>
/// A loaded transcript. Load is the format sentinel: any line that is not a JSON
/// object aborts the whole pass (unfamiliar shape → do nothing silently).
/// </summary>
internal sealed class TranscriptFile
{
    public required string Path { get; init; }
    public required List<TranscriptRecord> Records { get; init; }
    public required bool EndsWithNewline { get; init; }

    /// <summary>On-disk byte length at load time — the swap re-checks it (see TryRewrite).</summary>
    public required long LoadedLength { get; init; }

    /// <summary>
    /// True when EVERY record carries isSidechain: true — the file is a subagent
    /// transcript (agent-*.jsonl under the session dir's subagents/), where the
    /// sidechain IS the conversation, not foreign matter. Corpus check 2026-08-09:
    /// all 57 on-disk subagent files are 100% sidechain-flagged, while a main
    /// transcript always contains unflagged records — one unflagged record is
    /// enough to classify as main, so guards written for main files stay armed.
    /// </summary>
    public required bool IsSidechainFile { get; init; }

    public static TranscriptFile? TryLoad(string path)
    {
        // Strict decode: the default UTF8 decoder silently swaps invalid bytes for
        // U+FFFD, which the rewrite would then write back — the one way this class
        // could corrupt instead of abort. Refuse a BOM the same way: the app never
        // writes one, and ReadAllText would strip it invisibly.
        string text;
        long loadedLength;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return null;
            loadedLength = bytes.Length;
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch
        {
            return null;
        }
        return TryParseText(text, path, loadedLength);
    }

    /// <summary>
    /// The parse half of <see cref="TryLoad"/>, split out so it can be driven
    /// from an in-memory string. Extracted for the benchmark harness, which must
    /// re-parse the same text once per iteration without paying (or measuring)
    /// disk reads — and must exercise the REAL parse, not a copy of it that
    /// could drift out of agreement with production.
    /// </summary>
    internal static TranscriptFile? TryParseText(string text, string path, long loadedLength)
    {
        if (text.Length == 0)
            return null;

        bool endsWithNewline = text.EndsWith('\n');
        string[] lines = text.Split('\n');
        int count = endsWithNewline ? lines.Length - 1 : lines.Length;

        var records = new List<TranscriptRecord>(count);
        for (int i = 0; i < count; i++)
        {
            if (lines[i].Length == 0)
                return null; // blank interior line: not a shape we know
            var rec = TranscriptRecord.TryParse(lines[i]);
            if (rec is null)
                return null; // format sentinel
            records.Add(rec);
        }
        if (records.Count == 0)
            return null;

        MarkPreserved(records);

        return new TranscriptFile
        {
            Path = path,
            Records = records,
            EndsWithNewline = endsWithNewline,
            LoadedLength = loadedLength,
            IsSidechainFile = records.All(r => r.IsSidechain),
        };
    }

    /// <summary>
    /// Flag every record named by a compact_boundary's
    /// compactMetadata.preservedMessages.allUuids so IsProtected() covers them.
    /// After a boundary the app loads the summary PLUS these records; they are
    /// referenced by uuid only, never by the parent chain, so removing one is
    /// invisible to dangling-parent validation. Missing entries are tolerated —
    /// the app itself names uuids that were never written (2 of 8 in d8aa7b17).
    /// </summary>
    private static void MarkPreserved(List<TranscriptRecord> records)
    {
        HashSet<string>? preserved = null;
        foreach (var rec in records)
        {
            if (rec.Type != "system" ||
                rec.Node["subtype"].GetString() is not ("compact_boundary" or "microcompact_boundary"))
            {
                continue;
            }
            if (rec.Node["compactMetadata"] is not JsonObject meta ||
                meta["preservedMessages"] is not JsonObject pm ||
                pm["allUuids"] is not JsonArray all)
            {
                continue;
            }
            foreach (var entry in all)
            {
                if (entry?.GetValue<string>() is string uuid && uuid.Length > 0)
                    (preserved ??= new HashSet<string>(StringComparer.Ordinal)).Add(uuid);
            }
        }
        if (preserved is null)
            return;

        foreach (var rec in records)
        {
            if (rec.Uuid is string u && preserved.Contains(u))
                rec.IsBoundaryPreserved = true;
        }
    }

    public bool HasChanges => Records.Any(r => r.Replacement is not null || r.Removed);

    /// <summary>
    /// Validate the pending rewrite (replacements and removals), then atomically
    /// swap it in. Fail-closed: any validation miss leaves the original file
    /// untouched and reports false. Removals rechain surviving children's
    /// parentUuid — and any leafUuid resume anchors — to the nearest surviving
    /// ancestor (dangling leafUuid was a shipped cozempic-POC bug).
    /// </summary>
    public bool TryRewrite()
    {
        if (!HasChanges)
            return true;

        var lines = TryComputeRewrite();
        if (lines is null)
            return false;

        // The app appends live during a turn; a record landing between load and
        // swap would be silently discarded by the rename — and mirror-first means
        // a lost record was never mirrored either. Appends only ever grow the
        // file, so a length re-check right before the swap shrinks the race
        // window to ~zero without locking. Hooks fire at quiet points; a length
        // change here means this pass raced something — leave the file alone.
        try
        {
            if (new FileInfo(Path).Length != LoadedLength)
                return Refuse("file-changed");
        }
        catch
        {
            return Refuse("file-changed");
        }

        try
        {
            Jsonl.ReplaceAtomically(Path, lines, EndsWithNewline);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The compute half of <see cref="TryRewrite"/>: serialize the pending
    /// rewrite and validate it, no filesystem involved. Returns the output lines,
    /// or null when a check refused. Split out so the benchmark harness exercises
    /// the REAL serialize+validate path instead of a copy that could drift.
    /// Callers must have checked <see cref="HasChanges"/> first.
    /// </summary>
    internal List<string>? TryComputeRewrite()
    {
        // Tail-uuid invariant: the app chains the next append off the in-memory
        // tail uuid — the final record must survive with its uuid. Rules may not
        // remove or replace it; the rewrite layer itself may still rechain its
        // parentUuid when the records just before it were removed.
        if (Records[^1].Removed || Records[^1].Replacement is not null)
            return RefuseCompute("tail-touched");

        var byUuid = new Dictionary<string, TranscriptRecord>();
        foreach (var r in Records)
        {
            if (r.Uuid is not null)
                byUuid.TryAdd(r.Uuid, r);
        }
        var removedUuids = Records.Where(r => r.Removed && r.Uuid is not null)
            .Select(r => r.Uuid!).ToHashSet();

        // Walk up through removed records to the nearest kept ancestor. A uuid we
        // don't know is left as-is (original files legally contain references we
        // cannot resolve — grafts, crash leftovers; we only fix what WE break).
        string? SurvivingAncestor(string? uuid)
        {
            var visited = new HashSet<string>();
            while (uuid is not null && removedUuids.Contains(uuid))
            {
                if (!visited.Add(uuid))
                    return null; // cycle: fail safe to a root
                uuid = byUuid[uuid].ParentUuid;
            }
            return uuid;
        }

        // Build the output, computing the expected chain as we go. Untouched
        // records contribute their RawLine verbatim (the same string instance the
        // load split produced); only serialized lines carry round-trip risk.
        var kept = new List<(TranscriptRecord Rec, string Line, bool Serialized)>();
        var expected = new List<(string? Uuid, string? Parent)>();
        bool tailRewritten = false;
        foreach (var rec in Records)
        {
            if (rec.Removed)
                continue;

            var node = rec.Replacement;

            string? newParent = rec.ParentUuid;
            if (newParent is not null && removedUuids.Contains(newParent))
                newParent = SurvivingAncestor(newParent);

            string? origLeaf = (node ?? rec.Node)["leafUuid"] is JsonValue lv
                && lv.TryGetValue(out string? l) ? l : null;
            string? newLeaf = origLeaf is not null && removedUuids.Contains(origLeaf)
                ? SurvivingAncestor(origLeaf)
                : origLeaf;

            if (newParent != rec.ParentUuid || newLeaf != origLeaf)
            {
                if (ReferenceEquals(rec, Records[^1]))
                    tailRewritten = true;
                node ??= (JsonObject)rec.Node.DeepClone();
                if (newParent != rec.ParentUuid)
                    node["parentUuid"] = newParent is null ? null : JsonValue.Create(newParent);
                if (newLeaf != origLeaf)
                    node["leafUuid"] = newLeaf is null ? null : JsonValue.Create(newLeaf);
            }

            string line;
            if (node is not null)
            {
                line = node.ToJsonString(Json.Compact);
                if (rec.HadCarriageReturn) line += "\r";
            }
            else
            {
                line = rec.RawLine;
            }
            kept.Add((rec, line, node is not null));
            expected.Add((rec.Uuid, newParent));
        }

        if (kept.Count == 0)
            return RefuseCompute("empty"); // never empty a transcript

        // Re-validation. Serialized lines are independently re-parsed — that is
        // the round-trip proof for everything a rule or the rechain touched. An
        // untouched line IS the load-time bytes, whose parse already exists in
        // rec.Node; re-parsing it proved nothing and doubled the pass's parse
        // cost on large files (eng/bench/profiling-notes.md), so those are
        // checked through the parse we have. The old join-then-split count check
        // survives as the embedded-newline guard: a raw '\n' inside a serialized
        // line is the only way the on-disk line count could diverge from
        // kept.Count (untouched lines came FROM a '\n' split and cannot carry one).
        for (int i = 0; i < kept.Count; i++)
        {
            var (rec, line, serialized) = kept[i];
            JsonObject nodeToCheck;
            string? parentToCheck;
            if (serialized)
            {
                if (line.Contains('\n'))
                    return RefuseCompute("embedded-newline");
                var reparsed = TranscriptRecord.TryParse(line);
                if (reparsed is null)
                    return RefuseCompute("reparse");
                if (reparsed.Uuid != expected[i].Uuid || reparsed.ParentUuid != expected[i].Parent)
                    return RefuseCompute("chain-mismatch");
                nodeToCheck = reparsed.Node;
                parentToCheck = reparsed.ParentUuid;
            }
            else
            {
                nodeToCheck = rec.Node;
                parentToCheck = rec.ParentUuid;
            }

            // Nothing may still point at a removed record.
            if (parentToCheck is not null && removedUuids.Contains(parentToCheck))
                return RefuseCompute("dangling-parent");
            if (nodeToCheck["leafUuid"] is JsonValue rlv
                && rlv.TryGetValue(out string? rleaf) && removedUuids.Contains(rleaf))
            {
                return RefuseCompute("dangling-leaf");
            }
            // A result carrier pointing at a removed tool_use record means a rule
            // broke pair atomicity. Unlike parentUuid/leafUuid this is not an
            // ancestry link — remapping has no meaning, so fail the rewrite.
            if (nodeToCheck["sourceToolAssistantUUID"] is JsonValue rsv
                && rsv.TryGetValue(out string? rsrc) && removedUuids.Contains(rsrc))
            {
                return RefuseCompute("dangling-source");
            }
        }
        // The tail keeps its identity (uuid checked above via expected[^1]); it is
        // byte-identical unless the rewrite layer itself had to rechain its
        // parentUuid or remap its leafUuid (demanding byte-identity there silently
        // aborted every pass on files ending in a leafUuid-bearing metadata record
        // whose anchor was removed — same over-strictness bug as the old
        // second-to-last-record collapse abort).
        if (expected[^1].Uuid != Records[^1].Uuid)
            return RefuseCompute("tail-uuid");
        if (!tailRewritten && !ReferenceEquals(kept[^1].Line, Records[^1].RawLine))
            return RefuseCompute("tail-bytes");

        // Reachability, the one global invariant the per-record checks above
        // cannot express. The app reconstructs a conversation by walking parentUuid
        // BACKWARDS from the last record; anything the walk never reaches is
        // dropped at load, silently and with exit code 0. Verified empirically on
        // 2.1.227 (throwaway 6-record fixtures): all links null → 1 of 6 records
        // survived and the app logged warnIfTranscriptUnchained; ONE broken link
        // mid-file → 3 of 6 survived and NOTHING was logged. The app's own warning
        // returns on the first record carrying a parentUuid, so it only ever fires
        // for total chain loss — the partial break, which is the realistic rewrite
        // bug, gets no signal at all. The damage also scales with position: a break
        // at record k costs every record before k, so a late break is worse.
        //
        // Compared as a DELTA, not an absolute: a compact_boundary is written with
        // parentUuid null on purpose (physical chain severed, ancestry carried in
        // logicalParentUuid), and original files legally contain unresolvable refs
        // — grafts, crash leftovers, fork copies. An absolute "all records reach
        // the tail" assertion would refuse every compacted transcript. Losing
        // ground is the failure; inheriting it is not ours to fix (same policy as
        // SurvivingAncestor).
        // Compared as SETS of uuids, not hop counts: a pass legitimately shortens
        // the chain by removing records, which lowers the count without stranding
        // anything. What must never happen is a record that WAS reachable, still
        // survives, and is no longer reachable.
        var reachableBefore = ReachableUuids(Records.Select(r => (r.Uuid, r.ParentUuid)));
        var reachableAfter = ReachableUuids(expected);
        foreach (var (uuid, _) in expected)
        {
            if (uuid is not null && reachableBefore.Contains(uuid) && !reachableAfter.Contains(uuid))
                return RefuseCompute($"unreachable:{uuid}");
        }

        return kept.Select(k => k.Line).ToList();
    }

    /// <summary>
    /// The uuids the app's loader would actually reach: start at the last record
    /// and walk parentUuid upwards. Mirrors the loader's walk rather than testing
    /// graph connectivity in general — a record whose parent link is valid can
    /// still be unreachable, and that is precisely the case that costs context at
    /// load time.
    ///
    /// A null parentUuid ends the walk normally (chain root, or a boundary's
    /// deliberately severed link). An unresolvable parentUuid also ends it: that
    /// is the break itself, and the records above it are what gets lost.
    /// </summary>
    private static HashSet<string> ReachableUuids(IEnumerable<(string? Uuid, string? Parent)> chain)
    {
        var list = chain.ToList();
        var reached = new HashSet<string>(StringComparer.Ordinal);
        if (list.Count == 0)
            return reached;

        var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (uuid, parent) in list)
        {
            if (uuid is not null)
                parentOf.TryAdd(uuid, parent);
        }

        string? cursor = list[^1].Uuid;
        while (cursor is not null && reached.Add(cursor))
        {
            if (!parentOf.TryGetValue(cursor, out string? parent))
                break; // dangling: the walk stops here, everything above is lost
            cursor = parent;
        }
        return reached;
    }

    /// <summary>Fail-closed exit, naming the refused check when CLAUDININE_DEBUG is set.</summary>
    private static bool Refuse(string reason)
    {
        Dbg.Log($"rewrite refused: {reason}");
        return false;
    }

    /// <summary><see cref="Refuse"/> for the compute half's null-on-refusal shape.</summary>
    private static List<string>? RefuseCompute(string reason)
    {
        Refuse(reason);
        return null;
    }
}
