using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Shared engine for "a later read of the same range makes the earlier result
/// redundant" rules. Subclasses only say which tool_use blocks are reads and what
/// file ranges they provably return. The engine finds superseded reads (every
/// target covered by some later read), keeps a recency window untouched, and stubs
/// the matching tool_result payloads — original always mirrored first by the pipeline.
/// </summary>
internal abstract class ReadSupersessionRule : ICompactionRule
{
    public abstract string Name { get; }

    /// <summary>Tool name(s) whose tool_use blocks this rule inspects.</summary>
    protected internal abstract bool IsReadTool(string toolName);

    /// <summary>Ranges this call provably returns; empty = not a pure read, don't touch.</summary>
    protected internal abstract List<ReadTarget> ExtractTargets(JsonObject toolUseBlock);

    /// <summary>Results smaller than this aren't worth a stub (the stub itself costs bytes).</summary>
    private const int MinResultChars = 400;

    /// <summary>Never touch this many of the session's most recent reads — the tail is live context.</summary>
    private const int RecencyKeep = 6;

    public void Apply(TranscriptFile transcript)
    {
        // Pass 1: every eligible read, in file order.
        var reads = new List<(string ToolUseId, List<ReadTarget> Targets)>();
        foreach (TranscriptRecord rec in transcript.Records)
        {
            if (rec.IsProtected())
                continue;
            foreach (JsonNode? block in ContentBlocks(rec.Node))
            {
                if (block is not JsonObject b)
                    continue;
                if (b["type"]?.GetValue<string>() != "tool_use")
                    continue;
                if (b["name"]?.GetValue<string>() is not string name || !IsReadTool(name))
                    continue;
                var targets = ExtractTargets(b);
                if (targets.Count == 0)
                    continue;
                if (b["id"]?.GetValue<string>() is string toolUseId && toolUseId.Length > 0)
                    reads.Add((toolUseId, targets));
            }
        }
        if (reads.Count < 2)
            return;

        // Pass 2: a read is superseded when some LATER read covers every target.
        // Never the most recent read of a range, never the recency window.
        var superseded = new Dictionary<string, List<ReadTarget>>();
        int cutoff = reads.Count - RecencyKeep;
        for (int i = 0; i < reads.Count && i < cutoff; i++)
        {
            (string toolUseId, List<ReadTarget> targets) = reads[i];
            bool allCovered = targets.All(t =>
                reads.Skip(i + 1).Any(later => later.Targets.Any(lt => lt.Covers(t))));
            if (allCovered)
                superseded[toolUseId] = targets;
        }
        if (superseded.Count == 0)
            return;

        // Pass 3: stub the matching tool_result payloads.
        foreach (TranscriptRecord rec in transcript.Records)
        {
            if (rec.IsProtected() || rec.Node["claudinine"] is not null)
                continue;

            JsonObject? clone = null;
            foreach (JsonNode? block in ContentBlocks(rec.Replacement ?? rec.Node))
            {
                if (block is not JsonObject b)
                    continue;
                if (b["type"]?.GetValue<string>() != "tool_result")
                    continue;
                if (b["tool_use_id"]?.GetValue<string>() is not string toolUseId
                    || !superseded.TryGetValue(toolUseId, out List<ReadTarget>? targets))
                    continue;
                if (ResultText(b).Length < MinResultChars)
                    continue;

                // First hit on this record: clone it, then mutate the clone's
                // corresponding block (never the original parse).
                if (clone is null)
                {
                    clone = (JsonObject)(rec.Replacement ?? rec.Node).DeepClone();
                    clone["claudinine"] = new JsonObject
                    {
                        ["v"] = 1,
                        ["rule"] = Name,
                        ["origUuid"] = rec.Uuid,
                    };
                }
                foreach (JsonNode? cb in ContentBlocks(clone))
                {
                    if (cb is JsonObject cbo && cbo["tool_use_id"]?.GetValue<string>() == toolUseId)
                    {
                        string desc = string.Join(", ", targets);
                        cbo["content"] = $"[claudinine: file read superseded by a later read of {desc}]";
                    }
                }
            }
            if (clone is not null)
                rec.Replacement = clone;
        }
    }

    private static IEnumerable<JsonNode?> ContentBlocks(JsonObject record)
    {
        if (record["message"] is JsonObject m && m["content"] is JsonArray blocks)
            return blocks;
        return [];
    }

    private static string ResultText(JsonObject block)
    {
        JsonNode? c = block["content"];
        if (c is JsonValue v && v.TryGetValue<string>(out string? s))
            return s;
        if (c is JsonArray parts)
            return string.Concat(parts.OfType<JsonObject>()
                .Select(p => p["text"]?.GetValue<string>() ?? ""));
        return "";
    }
}
