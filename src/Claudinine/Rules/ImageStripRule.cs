using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Stub old base64 media blocks — images pasted into prompts, base64 document
/// blocks (PDFs), and screenshots nested inside a tool_result's content array.
/// Descends from cozempic's image-strip with two long-standing deviations
/// (age-gated instead of keep-newest-by-count for idempotence under per-turn
/// reruns; blocks are stubbed, never deleted, so a content array can't end up
/// empty) plus the mirror retrieval loop: the stub names the exact command —
/// `claudinine get &lt;sid&gt; --ref &lt;uuid&gt; --media` — which decodes the mirrored
/// block to a file the Read tool renders, so the content re-enters context as
/// fresh vision input instead of being lost. Legacy dead-end stubs ("re-request
/// if needed") are upgraded in place to the retrieval form.
/// </summary>
internal sealed class ImageStripRule : ICompactionRule
{
    public string Name => "image-strip";

    /// <summary>The pre-0.1.6 stub text, upgraded retroactively when addressable.</summary>
    private const string LegacyStubPrefix = "[claudinine: old screenshot removed";

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;
        var age = new AgeIndex(records);

        for (int pos = 0; pos < records.Count; pos++)
        {
            var rec = records[pos];
            if (rec.IsProtected())
                continue;
            if (!age.IsMidAged(pos))
                continue; // recently shared — keep

            var node = RuleHelpers.CurrentNode(rec);
            string? sid = node["sessionId"].GetString();
            string? refPrefix = rec.Uuid is string u ? RuleHelpers.RefPrefix(u) : null;
            JsonObject? clone = null;
            int bi = -1;
            foreach (var block in RuleHelpers.ContentBlocks(node))
            {
                bi++;
                if (block is not JsonObject b)
                    continue;
                switch (b["type"].GetString())
                {
                    case "image":
                    case "document" when SourceType(b) == "base64":
                        Stub(ref clone, node, bi, b, sid, refPrefix);
                        break;

                    case "text" when sid is not null && refPrefix is not null
                        && b["text"].GetString() is string t
                        && t.StartsWith(LegacyStubPrefix, StringComparison.Ordinal):
                        // The original media info is gone from a legacy stub;
                        // "image" is all it ever replaced.
                        WriteStub(RuleHelpers.CloneBlockAt(ref clone, node, bi),
                            "image", sid, refPrefix);
                        break;

                    case "tool_result" when b["content"] is JsonArray inner:
                        for (int ti = 0; ti < inner.Count; ti++)
                        {
                            if (inner[ti] is not JsonObject ib
                                || ib["type"].GetString() != "image")
                            {
                                continue;
                            }

                            var cloneResult = RuleHelpers.CloneBlockAt(ref clone, node, bi);
                            var cloneInner = (JsonObject)((JsonArray)cloneResult["content"]!)[ti]!;
                            WriteStub(cloneInner, Describe(ib), sid, refPrefix);
                        }
                        break;
                }
            }

            if (clone is not null)
                RuleHelpers.SetReplacement(rec, clone, Name);
        }
    }

    private static void Stub(
        ref JsonObject? clone, JsonObject node, int blockIndex,
        JsonObject original, string? sid, string? refPrefix) =>
        WriteStub(RuleHelpers.CloneBlockAt(ref clone, node, blockIndex), Describe(original), sid, refPrefix);

    private static void WriteStub(JsonObject cloneBlock, string label, string? sid, string? refPrefix)
    {
        string text = sid is not null && refPrefix is not null
            ? $"[claudinine: {label} archived — claudinine get {sid} --ref {refPrefix} --media " +
              "decodes it to a file; Read that file to view it]"
            // No retrieval address (uuid-less or sessionId-less record): the
            // mirror can't serve it, so keep the honest dead-end wording.
            : $"[claudinine: old {label} removed — re-request if needed]";
        cloneBlock.Clear();
        cloneBlock["type"] = "text";
        cloneBlock["text"] = text;
    }

    private static string? SourceType(JsonObject block) =>
        (block["source"] as JsonObject)?["type"].GetString();

    /// <summary>"image/png, 498KB" — enough to decide whether retrieval is worth it.</summary>
    private static string Describe(JsonObject block)
    {
        var source = block["source"] as JsonObject;
        string label = source?["media_type"].GetString()
            ?? block["type"].GetString() ?? "image";
        if (source?["data"].GetString() is string data && data.Length > 0)
            label += $", {Math.Max(1, data.Length * 3L / 4 / 1024)}KB";
        else if (SourceType(block) == "url")
            label += ", url source";
        return label;
    }
}
