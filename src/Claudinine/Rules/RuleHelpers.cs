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
                .Select(p => p["text"]?.GetValue<string>() ?? ""));
        return "";
    }

    public static int Utf8Len(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>
    /// True if this content is one of our own stubs — rules must never re-process
    /// those (a stub re-stubbed becomes "1 lines, 0.1KB" nonsense).
    /// </summary>
    public static bool IsClaudinineStub(string content) =>
        content.StartsWith("[claudinine", StringComparison.Ordinal);

    /// <summary>
    /// A real user prompt for turn counting (cozempic's _is_user_prompt): type user,
    /// content either a plain string or a list with no tool_result blocks.
    /// </summary>
    public static bool IsUserPrompt(JsonObject record)
    {
        if (record["type"]?.GetValue<string>() != "user")
            return false;
        JsonNode? content = (record["message"] as JsonObject)?["content"];
        if (content is JsonValue v && v.TryGetValue<string>(out _))
            return true;
        if (content is JsonArray list)
            return !list.OfType<JsonObject>()
                .Any(b => b["type"]?.GetValue<string>() == "tool_result");
        return false;
    }
}
