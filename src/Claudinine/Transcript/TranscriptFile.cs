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

        // Tail-uuid invariant: the app chains the next append off the in-memory
        // tail uuid — the final record must survive with its uuid. Rules may not
        // remove or replace it; the rewrite layer itself may still rechain its
        // parentUuid when the records just before it were removed.
        if (Records[^1].Removed || Records[^1].Replacement is not null)
            return Refuse("tail-touched");

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

        // Build the output, computing the expected chain as we go.
        var outLines = new List<string>();
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
            outLines.Add(line);
            expected.Add((rec.Uuid, newParent));
        }

        if (outLines.Count == 0)
            return Refuse("empty"); // never empty a transcript

        var sb = new StringBuilder();
        for (int i = 0; i < outLines.Count; i++)
        {
            sb.Append(outLines[i]);
            if (i < outLines.Count - 1 || EndsWithNewline) sb.Append('\n');
        }
        string rewritten = sb.ToString();

        // Independent re-validation of the full result.
        string[] lines = rewritten.Split('\n');
        int count = EndsWithNewline ? lines.Length - 1 : lines.Length;
        if (count != expected.Count)
            return Refuse("count-mismatch");
        for (int i = 0; i < count; i++)
        {
            var reparsed = TranscriptRecord.TryParse(lines[i]);
            if (reparsed is null)
                return Refuse("reparse");
            if (reparsed.Uuid != expected[i].Uuid || reparsed.ParentUuid != expected[i].Parent)
                return Refuse("chain-mismatch");
            // Nothing may still point at a removed record.
            if (reparsed.ParentUuid is not null && removedUuids.Contains(reparsed.ParentUuid))
                return Refuse("dangling-parent");
            if (reparsed.Node["leafUuid"] is JsonValue rlv
                && rlv.TryGetValue(out string? rleaf) && removedUuids.Contains(rleaf))
            {
                return Refuse("dangling-leaf");
            }
            // A result carrier pointing at a removed tool_use record means a rule
            // broke pair atomicity. Unlike parentUuid/leafUuid this is not an
            // ancestry link — remapping has no meaning, so fail the rewrite.
            if (reparsed.Node["sourceToolAssistantUUID"] is JsonValue rsv
                && rsv.TryGetValue(out string? rsrc) && removedUuids.Contains(rsrc))
            {
                return Refuse("dangling-source");
            }
        }
        // The tail keeps its identity (uuid checked above via expected[^1]); it is
        // byte-identical unless the rewrite layer itself had to rechain its
        // parentUuid or remap its leafUuid (demanding byte-identity there silently
        // aborted every pass on files ending in a leafUuid-bearing metadata record
        // whose anchor was removed — same over-strictness bug as the old
        // second-to-last-record collapse abort).
        if (expected[^1].Uuid != Records[^1].Uuid)
            return Refuse("tail-uuid");
        if (!tailRewritten && lines[count - 1] != Records[^1].RawLine)
            return Refuse("tail-bytes");

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
            Jsonl.ReplaceAtomically(Path, rewritten);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Fail-closed exit, naming the refused check when CLAUDININE_DEBUG is set.</summary>
    private static bool Refuse(string reason)
    {
        Dbg.Log($"rewrite refused: {reason}");
        return false;
    }
}
