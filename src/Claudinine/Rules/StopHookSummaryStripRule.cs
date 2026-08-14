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
            var node = rec.View;
            if (node["subtype"].AsString() != "stop_hook_summary")
                continue;
            if (node["hasOutput"].IsTrue || node["preventedContinuation"].IsTrue)
                continue;
            if (NonEmpty(node["hookErrors"]) || NonEmpty(node["hookAdditionalContext"]))
                continue;
            if (node["stopReason"].AsString() is { Length: > 0 })
                continue;

            rec.Removed = true;
        }
    }

    private static bool NonEmpty(JsonView n) => n.IsArray && n.Count > 0;
}
