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
                // Never re-TRIM our own trim output (multibyte content can trim to
                // just over the byte cap — each pass would then shave a sliver off
                // the previous pass's tail). Stubbing trimmed content when it ages
                // into the old tier is still fine (and much smaller).
                bool alreadyTrimmed = content.Contains("trimmed by claudinine]", StringComparison.Ordinal);
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

                clone ??= (JsonObject)node.DeepClone();
                ((JsonObject)RuleHelpers.ContentBlocks(clone).ElementAt(bi)!)["content"] = newContent;
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
    /// Head/tail trim for content still over the size caps after minification.
    /// The kept budget lands strictly UNDER the caps (marker included) so a second
    /// pass sees an in-budget result and does nothing — trim must be a fixpoint,
    /// not shave a sliver off its own output every pass.
    /// </summary>
    internal static string TrimOversized(string content)
    {
        int bytes = RuleHelpers.Utf8Len(content);
        string[] lines = content.Split('\n');
        if (bytes <= TrimMaxBytes && lines.Length <= TrimMaxLines)
            return content;

        if (lines.Length > TrimMaxLines)
        {
            int keep = TrimMaxLines / 2 - 1; // + 1 marker line = 99 ≤ cap
            return string.Join('\n', lines[..keep])
                + $"\n... [{lines.Length - 2 * keep} lines trimmed by claudinine] ...\n"
                + string.Join('\n', lines[^keep..]);
        }

        // Character-indexed halves like the original (byte counts reported, char
        // slices taken); 100 chars of headroom cover the marker.
        int half = Math.Min(TrimMaxBytes / 2 - 100, content.Length / 2);
        return content[..half]
            + $"\n... [{bytes - TrimMaxBytes} bytes trimmed by claudinine] ...\n"
            + content[^half..];
    }

    /// <summary>
    /// Old tier: "[claudinine: Bash npm test — 220 lines, 14.3KB]". Looks back a few
    /// records for the matching tool_use to name the tool and its target.
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
            for (int p = Math.Max(0, pos - 10); p <= pos && p < records.Count; p++)
            {
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
                            ?? Truncate(StringField(input, "command"), 80)
                            ?? "";
                    }
                }
            }
        }

        int lineCount = content.Length == 0 ? 0 : content.Count(c => c == '\n') + 1;
        string parts = "[claudinine";
        if (toolName.Length > 0) parts += $": {toolName}";
        if (toolPath.Length > 0) parts += $" {toolPath}";
        parts += $" — {lineCount} lines, {content.Length / 1024.0:F1}KB";
        if (persistedPath is not null) parts += $"; full output: {persistedPath}";
        parts += "]";
        return parts;
    }

    private static string? StringField(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out string? s) && s.Length > 0 ? s : null;

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}
