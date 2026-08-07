using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Strip old base64 image blocks — port of cozempic's image-strip. Screenshots in
/// old turns are opaque blobs the model never revisits, and they dominate byte
/// counts when present. Two deviations from the original: (1) recency is the same
/// turn-age gate as the other rules instead of "keep newest 20% by count" — the
/// count-based window is not idempotent under our per-turn reruns (each pass sees
/// fewer images and strips again until one remains), while a record's age only
/// grows; (2) a stripped image block becomes a text stub instead of being deleted,
/// so a content array can never end up empty and block arity is preserved.
/// </summary>
internal sealed class ImageStripRule : ICompactionRule
{
    public string Name => "image-strip";

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
                continue; // recently shared — keep

            JsonObject node = RuleHelpers.CurrentNode(rec);
            JsonObject? clone = null;
            int bi = -1;
            foreach (JsonNode? block in RuleHelpers.ContentBlocks(node))
            {
                bi++;
                if (block is not JsonObject b || b["type"]?.GetValue<string>() != "image")
                    continue;
                clone ??= (JsonObject)node.DeepClone();
                var cloneBlock = (JsonObject)RuleHelpers.ContentBlocks(clone).ElementAt(bi)!;
                cloneBlock.Clear();
                cloneBlock["type"] = "text";
                cloneBlock["text"] = "[claudinine: old screenshot removed — re-request if needed]";
            }

            if (clone is not null)
                RuleHelpers.SetReplacement(rec, clone, Name);
        }
    }
}
