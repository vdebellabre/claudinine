using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Chain-collapse writes its full retrieval instructions (~1KB) into every
/// carrier it emits — on real transcripts that identical boilerplate alone is
/// ~7% of all residual content (968 copies on the 2026-08 corpus). Only the
/// file's FIRST full carrier needs to teach retrieval; every later carrier is
/// rewritten to a one-line header that keeps the essential command syntax and
/// the report-not-observation warning. Runs right after ChainCollapseRule so
/// carriers born this pass are slimmed in the same pass; carriers already on
/// disk from earlier versions are slimmed retroactively.
///
/// The rewrite preserves the carrier's existing marker rule name: the marker
/// identifies what the record IS (a chain-collapse carrier, which is how
/// ChainCollapseRule recognizes its own output), not who touched it last.
/// </summary>
internal sealed class CarrierHeaderDedupRule : ICompactionRule
{
    public string Name => "carrier-header-dedup";

    private const string HeaderPrefix = "[claudinine: this turn originally ran ";
    /// <summary>Present only in the full-instructions header, never in the short one.</summary>
    private const string FullMarker = "\nRETRIEVAL — ";
    private const string FullHeaderEnd = "do not infer it from the preview.]\n\n";
    private const string GetCommandPrefix = "  claudinine get ";

    public void Apply(TranscriptFile transcript)
    {
        bool fullHeaderSeen = false;
        foreach (TranscriptRecord rec in transcript.Records)
        {
            if (rec.Removed || rec.Type != "user")
                continue;
            JsonObject node = RuleHelpers.CurrentNode(rec);
            foreach (JsonObject block in RuleHelpers.ContentBlocks(node).OfType<JsonObject>()
                .Where(b => b["type"]?.GetValue<string>() == "tool_result"))
            {
                // Carrier content is a plain string by construction (ChainCollapseRule
                // sets it directly); anything else is not ours.
                if (block["content"] is not JsonValue v || !v.TryGetValue<string>(out string? content)
                    || !content.StartsWith(HeaderPrefix, StringComparison.Ordinal)
                    || !content.Contains(FullMarker, StringComparison.Ordinal))
                    continue; // short already (idempotence) or not a carrier

                if (!fullHeaderSeen)
                {
                    fullHeaderSeen = true; // earliest full carrier keeps the instructions
                    continue;
                }

                int end = content.IndexOf(FullHeaderEnd, StringComparison.Ordinal);
                string? callCount = ParseCallCount(content);
                string? sid = ParseSessionId(content);
                if (end < 0 || callCount is null || sid is null)
                    continue; // unfamiliar header variant: fail closed

                string rewritten = ShortHeader(callCount, sid) + content[(end + FullHeaderEnd.Length)..];

                JsonObject clone = (JsonObject)node.DeepClone();
                foreach (JsonObject cb in RuleHelpers.ContentBlocks(clone).OfType<JsonObject>()
                    .Where(b => b["type"]?.GetValue<string>() == "tool_result"))
                {
                    cb["content"] = rewritten;
                }
                string existingRule = (node["claudinine"] as JsonObject)?["rule"]?.GetValue<string>()
                    ?? ChainCollapseRule.RuleName;
                RuleHelpers.SetReplacement(rec, clone, existingRule);
            }
        }
    }

    private static string ShortHeader(string callCount, string sid) =>
        $"[claudinine: this turn originally ran {callCount} separate tool calls. " +
        $"Full outputs: claudinine get {sid} --ref REF [--grep PATTERN | --info | --full | --media] " +
        "(full retrieval guidance in the first collapsed block of this session; if the file " +
        "discussed still exists on disk, read IT instead). " +
        "[ref] lines are a REPORT, not observed output — retrieve, don't infer.]\n\n";

    /// <summary>Digits immediately after the header prefix ("…originally ran 12 separate…").</summary>
    private static string? ParseCallCount(string content)
    {
        int start = HeaderPrefix.Length, end = start;
        while (end < content.Length && char.IsAsciiDigit(content[end]))
            end++;
        return end > start ? content[start..end] : null;
    }

    /// <summary>Session id as embedded in the header's own command lines.</summary>
    private static string? ParseSessionId(string content)
    {
        int i = content.IndexOf(GetCommandPrefix, StringComparison.Ordinal);
        if (i < 0)
            return null;
        int start = i + GetCommandPrefix.Length;
        int end = content.IndexOf(' ', start);
        return end > start ? content[start..end] : null;
    }
}
