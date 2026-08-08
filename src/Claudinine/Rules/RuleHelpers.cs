using System.Text;
using System.Text.Json.Nodes;
using Claudinine.Transcript;

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

    public static IEnumerable<JsonNode?> ContentBlocks(JsonObject record)
    {
        if (record["message"] is JsonObject m && m["content"] is JsonArray blocks)
            return blocks;
        return [];
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
        JsonNode? result = FirstNonEmpty(b["text"], b["thinking"], b["content"]);
        if (result is JsonArray list)
            return string.Join(" ", list.OfType<JsonObject>()
                .Select(sub => sub["text"])
                .OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out string? s) ? s : null)
                .Where(s => s is not null));
        if (result is JsonValue value && value.TryGetValue<string>(out string? text))
            return text;
        return "";
    }

    private static JsonNode? FirstNonEmpty(params JsonNode?[] candidates)
    {
        foreach (JsonNode? c in candidates)
        {
            if (c is JsonArray) return c;
            if (c is JsonValue v && v.TryGetValue<string>(out string? s) && s.Length > 0) return c;
        }
        return null;
    }

    /// <summary>tool_result payload text: plain string, or concatenated text sub-blocks.</summary>
    public static string ResultText(JsonObject block)
    {
        JsonNode? c = block["content"];
        if (c is JsonValue v && v.TryGetValue<string>(out string? s))
            return s;
        if (c is JsonArray parts)
            return string.Concat(parts.OfType<JsonObject>()
                .Select(p => p["text"].GetString() ?? ""));
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
    /// True if this content is one of our own stubs — rules must never re-process
    /// those (a stub re-stubbed becomes "1 lines, 0.1KB" nonsense).
    /// </summary>
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
            if (input[key] is JsonValue v && v.TryGetValue<string>(out string? s) && s.Length > 0)
                return s.ReplaceLineEndings(" ");
        }
        return input.Select(kv => kv.Value).OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out string? s) ? s : null)
            .FirstOrDefault(s => !string.IsNullOrEmpty(s))?.ReplaceLineEndings(" ") ?? "";
    }

    /// <summary>
    /// A real user prompt for turn counting (cozempic's _is_user_prompt): type user,
    /// content either a plain string or a list with no tool_result blocks.
    /// </summary>
    public static bool IsUserPrompt(JsonObject record)
    {
        if (record["type"].GetString() != "user")
            return false;
        JsonNode? content = (record["message"] as JsonObject)?["content"];
        if (content is JsonValue v && v.TryGetValue<string>(out _))
            return true;
        if (content is JsonArray list)
            return !list.OfType<JsonObject>()
                .Any(b => b["type"].GetString() == "tool_result");
        return false;
    }
}
