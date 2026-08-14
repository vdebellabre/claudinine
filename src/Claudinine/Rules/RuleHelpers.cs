namespace Claudinine.Rules;

/// <summary>
/// Shared plumbing for compaction rules. Rules always read through
/// <see cref="CurrentNode"/> (so they see earlier rules' edits) and write through
/// <see cref="SetReplacement"/> (which maintains the claudinine marker that gives
/// idempotence-by-inspection and mirror retrieval addressing).
/// </summary>
internal static class RuleHelpers
{
    /// <summary>The record as the pass currently sees it: pending replacement, else original.</summary>
    public static JsonObject CurrentNode(TranscriptRecord rec) => rec.Replacement ?? rec.Node;

    /// <summary>Register a rule's rewrite of a record, stamping/refreshing the marker.</summary>
    public static void SetReplacement(TranscriptRecord rec, JsonObject clone, string ruleName)
    {
        clone["claudinine"] = new JsonObject
        {
            ["v"] = 1,
            ["rule"] = ruleName,
            ["origUuid"] = rec.Uuid,
        };
        rec.Replacement = clone;
    }

    public static IEnumerable<JsonNode?> ContentBlocks(JsonObject record) =>
        record["message"] is JsonObject m && m["content"] is JsonArray blocks
        ? blocks
        : [];

    /// <summary>The record's content blocks of one type — the filter almost every rule opens with.</summary>
    public static IEnumerable<JsonObject> BlocksOfType(JsonObject record, string type) =>
        ContentBlocks(record).OfType<JsonObject>()
            .Where(b => b["type"].GetString() == type);

    /// <summary>
    /// Keep-last supersession: remove every unprotected match before the final
    /// one. The final occurrence always survives, so the file's tail record is
    /// safe by construction — the invariant both keep-last rules lean on.
    /// </summary>
    public static void RemoveAllButLast(
        List<TranscriptRecord> records, Func<TranscriptRecord, bool> matches)
    {
        int last = -1;
        for (int i = 0; i < records.Count; i++)
        {
            if (matches(records[i]))
                last = i;
        }
        for (int i = 0; i < last; i++)
        {
            if (matches(records[i]) && !records[i].IsProtected())
                records[i].Removed = true;
        }
    }

    /// <summary>
    /// Visit every string leaf of a JSON tree; a non-null return replaces the
    /// leaf in place. One traversal shared by fork-heal's collect/retarget and
    /// clone's retrieval-command rewrite — three hand-rolled copies of this walk
    /// had already drifted apart in shape. Recursion depth is bounded by the
    /// parser (JsonNode.Parse caps document depth at 64), so no explicit guard.
    /// </summary>
    public static void VisitStrings(JsonNode? node, Func<string, string?> transform)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(kv => kv.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue(out string? text))
                    {
                        if (transform(text) is string replaced)
                            obj[key] = replaced;
                    }
                    else
                    {
                        VisitStrings(obj[key], transform);
                    }
                }
                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue(out string? text))
                    {
                        if (transform(text) is string replaced)
                            array[i] = replaced;
                    }
                    else
                    {
                        VisitStrings(array[i], transform);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// The write half of the read-CurrentNode / mutate-clone-only convention:
    /// lazily deep-clone the record's node, then hand back the clone's content
    /// block at <paramref name="blockIndex"/> for mutation. The original parse is
    /// never touched (see <see cref="TranscriptRecord.Node"/>).
    /// </summary>
    public static JsonObject CloneBlockAt(ref JsonObject? clone, JsonObject node, int blockIndex)
    {
        clone ??= (JsonObject)node.DeepClone();
        return (JsonObject)ContentBlocks(clone).ElementAt(blockIndex)!;
    }

    /// <summary>
    /// Text content of a block, any type (cozempic's text_of): text, else thinking,
    /// else content; list content joins sub-block texts with a space. Never throws
    /// on untrusted shapes.
    /// </summary>
    public static string TextOf(JsonNode? block)
    {
        if (block is not JsonObject b)
            return "";
        var result = FirstNonEmpty(b["text"], b["thinking"], b["content"]);
        if (result is JsonArray list)
        {
            return string.Join(" ", list.OfType<JsonObject>()
                .Select(sub => sub["text"].GetStringMemo())
                .Where(s => s is not null));
        }

        return result.GetStringMemo() ?? "";
    }

    private static JsonNode? FirstNonEmpty(params JsonNode?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (c is JsonArray) return c;
            if (c.GetStringMemo() is { Length: > 0 }) return c;
        }
        return null;
    }

    /// <summary>tool_result payload text: plain string, or concatenated text sub-blocks.</summary>
    public static string ResultText(JsonObject block)
    {
        var c = block["content"];
        if (c.GetStringMemo() is string s)
            return s;
        if (c is JsonArray parts)
        {
            return string.Concat(parts.OfType<JsonObject>()
                .Select(p => p["text"].GetStringMemo() ?? ""));
        }

        return "";
    }

    public static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>
    /// The uuid prefix retrieval refs are addressed by. Guarded: real uuids are
    /// GUIDs, but an alien transcript's short uuid must not throw (a throw kills
    /// the whole pass); `get` matches refs by StartsWith, so a short ref still resolves.
    /// </summary>
    public static string RefPrefix(string uuid) => uuid.Length >= 8 ? uuid[..8] : uuid;

    /// <summary>
    /// Fixpoint sentinel present in every head/tail-trim marker: content carrying
    /// it is our own trim output and must never be re-trimmed (multibyte content
    /// can trim to just over a byte cap — each pass would then shave a sliver off
    /// the previous pass's tail).
    /// </summary>
    public const string TrimSentinel = "trimmed by claudinine]";

    /// <summary>
    /// Byte-capped head/tail trim shared by the mid-age tier and mega-block-trim.
    /// The kept budget lands strictly UNDER the cap (marker included, 100 chars of
    /// headroom) so a second pass sees an in-budget result and does nothing —
    /// trim must be a fixpoint. Character-indexed halves, byte counts reported.
    /// </summary>
    public static string HeadTailTrimBytes(string text, int maxBytes)
    {
        int bytes = Utf8Len(text);
        if (bytes <= maxBytes)
            return text;
        int half = Math.Min(maxBytes / 2 - 100, text.Length / 2);
        return text[..half]
            + $"\n... [{bytes - maxBytes} bytes trimmed by claudinine] ...\n"
            + text[^half..];
    }

    public static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];

    /// <summary>
    /// Claude Code overflows large tool output to a sidecar under the session
    /// directory and leaves a "&lt;persisted-output&gt;" stub carrying the absolute
    /// path plus a preview. That path is the ONLY pointer to the file (nothing
    /// garbage-collects it), so any rule that rewrites such a block must carry the
    /// path through — dropping it strands the sidecar on disk forever.
    /// Returns the path, or null when the content is not a persisted-output stub.
    /// </summary>
    public static string? PersistedOutputPath(string content)
    {
        if (!content.Contains("<persisted-output>", StringComparison.Ordinal))
            return null;
        const string marker = "Full output saved to: ";
        int i = content.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            return null;
        int start = i + marker.Length;
        int end = content.IndexOf('\n', start);
        if (end < 0) end = content.Length;
        string path = content[start..end].Trim();
        return path.Length > 0 ? path : null;
    }

    /// <summary>
    /// True if this content is one of our own stubs — rules must never re-process
    /// those (a stub re-stubbed becomes "1 lines, 0.1KB" nonsense).
    /// </summary>
    public static bool IsClaudinineStub(string content) =>
        content.StartsWith("[claudinine", StringComparison.Ordinal);

    /// <summary>
    /// Media types of the non-text blocks (base64 images, documents) inside a
    /// tool_result's content array, e.g. "image/png" or "image/png x2". Text
    /// extraction is blind to these blocks, so without this summary a screenshot
    /// result digests as "(no output)" and its existence is silently lost.
    /// </summary>
    public static string MediaKinds(JsonObject toolResultBlock)
    {
        if (toolResultBlock["content"] is not JsonArray parts)
            return "";
        var kinds = parts.OfType<JsonObject>()
            .Where(p => p["type"].GetString() is "image" or "document")
            .Select(p => (p["source"] as JsonObject)?["media_type"].GetString() ?? "media")
            .ToList();
        return string.Join("+", kinds.GroupBy(k => k)
            .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
    }

    /// <summary>
    /// The human-meaningful argument of a tool_use input, for one-line previews:
    /// first non-empty well-known key, else the first non-empty string value.
    /// </summary>
    public static string PrimaryArg(JsonObject toolUse)
    {
        if (toolUse["input"] is not JsonObject input)
            return "";
        foreach (string key in (string[])["command", "file_path", "path", "pattern", "url", "query", "prompt"])
        {
            if (input[key].GetString() is { Length: > 0 } s)
                return s.ReplaceLineEndings(" ");
        }
        return input.Select(kv => kv.Value.GetString())
            .FirstOrDefault(s => !string.IsNullOrEmpty(s))?.ReplaceLineEndings(" ") ?? "";
    }

    /// <summary>
    /// A real user prompt for turn counting (cozempic's _is_user_prompt): type user,
    /// content either a plain string or a list with no tool_result blocks.
    /// DELIBERATELY broader than <see cref="TranscriptRecord.IsRealUserMessage"/>
    /// (chain-collapse's turn boundary, string content only): an image-share user
    /// message advances the age clock but is not a collapse boundary.
    /// </summary>
    public static bool IsUserPrompt(JsonObject record)
    {
        if (record["type"].GetString() != "user")
            return false;
        // Kind check, not TryGetValue<string>: that would decode the entire
        // prompt just to test its type.
        var content = (record["message"] as JsonObject)?["content"];
        if (content is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            return true;
        if (content is JsonArray list)
        {
            return !list.OfType<JsonObject>()
                .Any(b => b["type"].GetString() == "tool_result");
        }

        return false;
    }
}
