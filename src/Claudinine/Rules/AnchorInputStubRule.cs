using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Chain-collapse must keep one tool_use per collapsed turn (a tool_result
/// cannot exist without its use), and that anchor's full input rides along as
/// dead weight — Write payloads of tens of KB included. On the 2026-08 corpus
/// anchors carry 1.06M chars, 81% of all residual tool_use input. The digest's
/// first [ref] line already previews the call, and the original record lives in
/// the mirror, so a large anchor input is replaced by a two-field stub: a
/// claudinine pointer (which also gives idempotence-by-inspection) and the
/// primary-arg preview. `claudinine get <sid> --ref <use-uuid> --full` returns
/// the original input. Runs after ChainCollapseRule; works identically for
/// carriers born this pass and carriers already on disk from earlier versions.
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
            foreach (JsonObject b in RuleHelpers.ContentBlocks(RuleHelpers.CurrentNode(records[i]))
                .OfType<JsonObject>()
                .Where(x => x["type"].GetString() == "tool_use"))
            {
                if (b["id"].GetString() is string id && id.Length > 0)
                    useIndexById[id] = i;
            }
        }

        foreach (TranscriptRecord rec in records)
        {
            if (rec.Removed || rec.Type != "user")
                continue;
            foreach (JsonObject block in RuleHelpers.ContentBlocks(RuleHelpers.CurrentNode(rec))
                .OfType<JsonObject>()
                .Where(b => b["type"].GetString() == "tool_result"))
            {
                if (block["content"] is not JsonValue v || !v.TryGetValue<string>(out string? content)
                    || !content.StartsWith(CarrierPrefix, StringComparison.Ordinal))
                    continue; // not a collapse carrier
                if (block["tool_use_id"].GetString() is not string useId
                    || !useIndexById.TryGetValue(useId, out int useIdx))
                    continue;
                StubAnchorInput(records[useIdx], useId);
            }
        }
    }

    private void StubAnchorInput(TranscriptRecord useRec, string useId)
    {
        JsonObject node = RuleHelpers.CurrentNode(useRec);
        JsonObject? use = RuleHelpers.ContentBlocks(node).OfType<JsonObject>()
            .FirstOrDefault(b => b["type"].GetString() == "tool_use"
                && b["id"].GetString() == useId);
        if (use?["input"] is not JsonObject input)
            return;
        if (input.ContainsKey("claudinine"))
            return; // already stubbed (idempotence)
        if (input.ToJsonString().Length < MinInputChars)
            return;
        if (useRec.Uuid is null)
            return; // retrieval addressing needs the uuid
        string? sid = node["sessionId"].GetString();
        if (sid is null)
            return;

        string preview = RuleHelpers.PrimaryArg(use);
        JsonObject clone = (JsonObject)node.DeepClone();
        foreach (JsonObject cb in RuleHelpers.ContentBlocks(clone).OfType<JsonObject>()
            .Where(b => b["type"].GetString() == "tool_use"
                && b["id"].GetString() == useId))
        {
            cb["input"] = new JsonObject
            {
                ["claudinine"] = "input archived at collapse; original: " +
                    $"claudinine get {sid} --ref {RuleHelpers.RefPrefix(useRec.Uuid)} --full",
                ["preview"] = preview.Length <= 90 ? preview : preview[..90],
            };
        }
        RuleHelpers.SetReplacement(useRec, clone, Name);
    }
}
