namespace Claudinine.Rules;

/// <summary>
/// Removes stop_hook_summary system records that carry no signal — no output, no
/// errors, no additional context, no prevented continuation, no stop reason. One
/// lands after nearly every turn (census 2026-08-07: 1.0% of bytes). They sit ON
/// the uuid chain (the next user prompt parents them), so removal leans on the
/// rewrite layer's rechaining; anything non-empty is kept verbatim.
/// </summary>
internal sealed class StopHookSummaryStripRule : ICompactionRule
{
    public string Name => "stop-hook-summary-strip";

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;
        for (int i = 0; i < records.Count - 1; i++) // tail excluded by loop bound
        {
            var rec = records[i];
            if (rec.Type != "system" || rec.IsProtected())
                continue;
            var node = rec.Node;
            if (node["subtype"].GetString() != "stop_hook_summary")
                continue;
            if (IsTruthy(node["hasOutput"]) || IsTruthy(node["preventedContinuation"]))
                continue;
            if (NonEmpty(node["hookErrors"]) || NonEmpty(node["hookAdditionalContext"]))
                continue;
            if (node["stopReason"] is JsonValue sr && sr.TryGetValue(out string? reason)
                && reason.Length > 0)
            {
                continue;
            }

            rec.Removed = true;
        }
    }

    private static bool IsTruthy(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue(out bool b) && b;

    private static bool NonEmpty(JsonNode? n) => n is JsonArray a && a.Count > 0;
}
