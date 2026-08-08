using System.Text.Json;
using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Three-tier age-based tool-result compaction (port of cozempic's headline
/// tool-result-age, 10–40% measured): tool results decay in value exponentially,
/// and observation masking matches LLM summarization quality at zero compute cost
/// (JetBrains, SWE-bench). Age runs on the dual clock in <see cref="AgeIndex"/>.
///
///   Recent:   untouched — the model may still be working from it.
///   Mid-age:  minify (JSON re-serialization, unified-diff context collapse) then
///             head/tail-trim if still oversized (this tier absorbs cozempic's
///             separate tool-output-trim, which had no age gate — a deliberate
///             deviation: our pass runs right after every turn, not post-session).
///   Old:      replace with a one-line stub naming the tool, its target and the
///             original size.
///
/// Originals are always in the mirror first; stubs carry origUuid for retrieval.
/// </summary>
internal sealed class ToolResultAgeRule : ICompactionRule
{
    public string Name => "tool-result-age";

    private const int MinContentChars = 100;
    internal const int TrimMaxBytes = 8192;
    internal const int TrimMaxLines = 100;

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;
        var age = new AgeIndex(records);

        for (int pos = 0; pos < records.Count; pos++)
        {
            TranscriptRecord rec = records[pos];
            if (rec.IsProtected())
                continue;

            if (!age.IsMidAged(pos))
                continue; // recent — keep verbatim

            JsonObject node = RuleHelpers.CurrentNode(rec);
            JsonObject? clone = null;
            int bi = -1;
            foreach (JsonNode? block in RuleHelpers.ContentBlocks(node))
            {
                bi++;
                if (block is not JsonObject b || b["type"].GetString() != "tool_result")
                    continue;
                if (b["content"] is not JsonValue cv || !cv.TryGetValue<string>(out string? content))
                    continue;
                if (content.Length < MinContentChars || RuleHelpers.IsClaudinineStub(content))
                    continue;
                // Never re-TRIM our own trim output (see TrimSentinel). Stubbing
                // trimmed content when it ages into the old tier is still fine
                // (and much smaller).
                bool alreadyTrimmed = content.Contains(RuleHelpers.TrimSentinel, StringComparison.Ordinal);
                if (!age.IsOld(pos) && alreadyTrimmed)
                    continue;

                // Persisted-output stubs point at a sidecar file that nothing ever
                // collects; the path must survive any rewrite (see PersistedOutputPath).
                // Stubbing carries it explicitly; trimming can't guarantee the path
                // line lands inside a kept half, so leave those blocks alone.
                string? persisted = RuleHelpers.PersistedOutputPath(content);
                if (persisted is not null && !age.IsOld(pos))
                    continue;

                string newContent = age.IsOld(pos)
                    ? BuildStub(b, content, records, pos, persisted)
                    : TrimOversized(Minify(content));
                if (newContent == content || newContent.Length >= content.Length)
                    continue;

                RuleHelpers.CloneBlockAt(ref clone, node, bi)["content"] = newContent;
            }

            if (clone is not null)
                RuleHelpers.SetReplacement(rec, clone, Name);
        }
    }

    /// <summary>Mid-age: JSON minification (only if it meaningfully shrinks), then diff collapse.</summary>
    internal static string Minify(string content)
    {
        try
        {
            var node = JsonNode.Parse(content);
            if (node is not null)
            {
                string minified = node.ToJsonString(Json.Compact);
                if (minified.Length < content.Length * 0.85)
                    return minified;
            }
        }
        catch (JsonException)
        {
            // not JSON — fall through
        }

        if (!content.Contains('\0') && DiffCollapse.LooksLikeUnifiedDiff(content))
        {
            string collapsed = DiffCollapse.CollapseContext(content);
            if (collapsed != content)
                return collapsed;
        }

        return content;
    }

    /// <summary>
    /// Head/tail trim for content still over the size caps after minification:
    /// line-capped here, byte-capped via the shared fixpoint-safe helper.
    /// </summary>
    internal static string TrimOversized(string content)
    {
        string[] lines = content.Split('\n');
        if (lines.Length > TrimMaxLines)
        {
            int keep = TrimMaxLines / 2 - 1; // + 1 marker line = 99 ≤ cap
            return string.Join('\n', lines[..keep])
                + $"\n... [{lines.Length - 2 * keep} lines trimmed by claudinine] ...\n"
                + string.Join('\n', lines[^keep..]);
        }
        return RuleHelpers.HeadTailTrimBytes(content, TrimMaxBytes);
    }

    /// <summary>
    /// Old tier: "[claudinine: Bash npm test — 220 lines, 14.3KB]". Scans back to
    /// the turn boundary for the matching tool_use to name the tool and its target.
    /// When the result was a persisted-output stub, the sidecar path is appended so
    /// the full output stays reachable ("full output: C:\...\tool-results\x.txt").
    /// </summary>
    private static string BuildStub(JsonObject block, string content,
        List<TranscriptRecord> records, int pos, string? persistedPath = null)
    {
        string? toolUseId = block["tool_use_id"].GetString();
        string toolName = "", toolPath = "";
        if (toolUseId is not null)
        {
            // The matching use always precedes its result within the same turn,
            // but in the parallel-batch format (each use its own record, results
            // in completion order) it can sit arbitrarily many records back — a
            // fixed window produced anonymous stubs on large batches, so scan to
            // the turn boundary instead.
            // Reads .Node, not CurrentNode, on purpose: anchor-input-stub runs
            // earlier in the catalog and may have replaced the input with its
            // pointer stub — the ORIGINAL input is what names this stub usefully.
            for (int p = pos - 1; p >= 0 && toolName.Length == 0; p--)
            {
                if (records[p].IsRealUserMessage())
                    break; // turn boundary: the use cannot be earlier
                foreach (JsonNode? n in RuleHelpers.ContentBlocks(records[p].Node))
                {
                    if (n is not JsonObject u
                        || u["type"].GetString() != "tool_use"
                        || u["id"].GetString() != toolUseId)
                        continue;
                    toolName = u["name"].GetString() ?? "";
                    if (u["input"] is JsonObject input)
                    {
                        toolPath = StringField(input, "file_path")
                            ?? StringField(input, "path")
                            ?? StringField(input, "pattern")
                            ?? RuleHelpers.Truncate(StringField(input, "command"), 80)
                            ?? "";
                    }
                    break;
                }
            }
        }

        int lineCount = content.Length == 0 ? 0 : content.Count(c => c == '\n') + 1;
        string parts = "[claudinine";
        if (toolName.Length > 0) parts += $": {toolName}";
        if (toolPath.Length > 0) parts += $" {toolPath}";
        parts += $" — {lineCount} lines, {RuleHelpers.Utf8Len(content) / 1024.0:F1}KB";
        if (persistedPath is not null) parts += $"; full output: {persistedPath}";
        parts += "]";
        return parts;
    }

    /// <summary>Like GetString, but empty reads as absent so ?? chains fall through.</summary>
    private static string? StringField(JsonObject obj, string key) =>
        obj[key].GetString() is { Length: > 0 } s ? s : null;
}
