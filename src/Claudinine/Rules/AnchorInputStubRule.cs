namespace Claudinine.Rules;

/// <summary>
/// Chain-collapse must keep one tool_use per collapsed turn (a tool_result
/// cannot exist without its use), and that anchor's full input rides along as
/// dead weight — Write payloads of tens of KB included. On the 2026-08 corpus
/// anchors carry 1.06M chars, 81% of all residual tool_use input. The digest's
/// first [ref] line already previews the call, and the original record lives in
/// the mirror, so a large anchor input is replaced by a two-field stub: a
/// claudinine pointer (which also gives idempotence-by-inspection) and the
/// primary-arg preview. The pointer names the use record's ref and defers to
/// the carrier's RETRIEVAL block for the how — see the comment at the write
/// site for why it is deliberately not a command. Runs after ChainCollapseRule;
/// works identically for carriers born this pass and carriers already on disk
/// from earlier versions.
///
/// Safety: replacement only (never removal — no rechaining involved), assistant
/// prose on the anchor record is untouched, and the use record is never the
/// file's final record (its result follows it). Rewriting a tool_use input is
/// canary-verified: the app replays it on resume without complaint and the API
/// accepts it (input objects are not schema-validated on replay).
/// </summary>
internal sealed class AnchorInputStubRule : ICompactionRule
{
    public string Name => "anchor-input-stub";

    /// <summary>Below this input size the stub saves nothing worth a rewrite.</summary>
    private const int MinInputChars = 300;

    private const string CarrierPrefix = ChainCollapseRule.CarrierPrefix;

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;

        // tool_use id -> index of the assistant record carrying it.
        var useIndexById = new Dictionary<string, int>();
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i].Removed || records[i].Type != "assistant")
                continue;
            foreach (var b in RuleHelpers.BlocksOfType(records[i].CurrentView, "tool_use"))
            {
                if (b["id"].AsString() is string id && id.Length > 0)
                    useIndexById[id] = i;
            }
        }

        foreach (var rec in records)
        {
            if (rec.Removed || rec.Type != "user")
                continue;
            foreach (var block in RuleHelpers.BlocksOfType(rec.CurrentView, "tool_result"))
            {
                if (block["content"].AsStringMemo() is not string content
                    || !content.StartsWith(CarrierPrefix, StringComparison.Ordinal))
                {
                    continue; // not a collapse carrier
                }

                if (block["tool_use_id"].AsString() is not string useId
                    || !useIndexById.TryGetValue(useId, out int useIdx))
                {
                    continue;
                }

                StubAnchorInput(records[useIdx], useId);
            }
        }
    }

    private void StubAnchorInput(TranscriptRecord useRec, string useId)
    {
        var use = RuleHelpers.BlocksOfType(useRec.CurrentView, "tool_use")
            .FirstOrDefault(b => b["id"].AsString() == useId);
        var input = use["input"];
        if (!input.IsObject)
            return;
        if (input.HasProperty("claudinine"))
            return; // already stubbed (idempotence)
        if (input.SerializedLength() < MinInputChars)
            return;
        if (useRec.Uuid is null)
            return; // retrieval addressing needs the uuid

        string preview = RuleHelpers.PrimaryArg(use);
        var clone = useRec.CloneCurrentNode();
        foreach (var cb in RuleHelpers.BlocksOfType(clone, "tool_use")
            .Where(b => b["id"].GetString() == useId))
        {
            // A POINTER, not a command — mode-free and path-free on purpose. An
            // anchor stub only ever exists on a collapse anchor, whose carrier
            // guarantees a full RETRIEVAL block in the same boundary segment
            // (CarrierHeaderDedupRule keeps one per segment), so unlike media
            // stubs it never needs to be self-sufficient. The old spelling was a
            // bare `claudinine get` command — dead on hosted installs (docs/
            // cowork E8) — and the launcher-form replacement measurably cost
            // ~0.5 pt of corpus tokens (one path per stub, paths tokenize badly).
            cb["input"] = new JsonObject
            {
                ["claudinine"] = "input archived at collapse; original: " +
                    $"ref {RuleHelpers.RefPrefix(useRec.Uuid)} — retrieve via the " +
                    "RETRIEVAL block in the nearest collapsed turn",
                ["preview"] = preview.Length <= 90 ? preview : preview[..90],
            };
        }
        RuleHelpers.SetReplacement(useRec, clone, Name);
    }
}
