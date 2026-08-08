using System.Text.Json.Nodes;

namespace Claudinine.Transcript;

/// <summary>
/// One line of a transcript. The raw text is authoritative: untouched records are
/// written back byte-for-byte; only records a rule replaces get re-serialized.
/// </summary>
internal sealed class TranscriptRecord
{
    public required string RawLine { get; init; }

    /// <summary>Parsed view of RawLine. Never mutated — replacements go to <see cref="Replacement"/>.</summary>
    public required JsonObject Node { get; init; }

    public string? Uuid { get; init; }
    public string? ParentUuid { get; init; }
    public string? Type { get; init; }

    /// <summary>Set by a rule to replace this record on rewrite. Must preserve uuid/parentUuid.</summary>
    public JsonObject? Replacement { get; set; }

    /// <summary>
    /// Set by a rule to drop this record on rewrite. The rewrite layer rechains
    /// surviving children (and leafUuid anchors) to the nearest surviving ancestor.
    /// </summary>
    public bool Removed { get; set; }

    /// <summary>True if the original line ended with a CR (CRLF file) to preserve on rewrite.</summary>
    public required bool HadCarriageReturn { get; init; }

    public static TranscriptRecord? TryParse(string line)
    {
        bool hadCr = line.EndsWith('\r');
        string json = hadCr ? line[..^1] : line;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
                return null;
            // Identity fields stay strict: a wrong-typed uuid/parentUuid/type is an
            // unfamiliar shape, and the throw lands in this catch → format sentinel.
            return new TranscriptRecord
            {
                RawLine = line,
                Node = obj,
                Uuid = obj["uuid"]?.GetValue<string>(),
                ParentUuid = obj["parentUuid"]?.GetValue<string>(),
                Type = obj["type"]?.GetValue<string>(),
                HadCarriageReturn = hadCr,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True if this record must never be removed or structurally modified.
    /// Ported from cozempic's is_protected, minus its in-memory tag keys.
    /// </summary>
    public bool IsProtected()
    {
        if (Type is "content-replacement" or "marble-origami-commit"
            or "marble-origami-snapshot" or "worktree-state" or "task-summary")
            return true;
        if (Type == "user" && IsTruthy(Node["isCompactSummary"]))
            return true;
        if (Type == "system" &&
            Node["subtype"].GetString() is "compact_boundary" or "microcompact_boundary")
            return true;
        if (IsTruthy(Node["isVisibleInTranscriptOnly"]))
            return true;
        return false;
    }

    /// <summary>A real user turn: message.content is a plain string (tool-result carriers use a list).</summary>
    public bool IsRealUserMessage() =>
        Type == "user"
        && !IsTruthy(Node["isCompactSummary"])
        && Node["message"] is JsonObject m
        && m["content"] is JsonValue v
        && v.TryGetValue<string>(out _);

    private static bool IsTruthy(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<bool>(out bool b) && b;
}
