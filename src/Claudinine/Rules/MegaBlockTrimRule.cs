using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Safety net (port of cozempic's mega-block-trim): any single text/string-content
/// block over 32KB gets head/tail-trimmed. Two deviations from the original:
/// age-gated to ≥ mid-age turns (our pass runs right after every turn, and a huge
/// block in the active tail — a big user paste, a fresh dump — may still be
/// load-bearing), and thinking blocks are NOT touched (they are signed; a tampered
/// block risks API rejection when the resumed session replays it).
/// </summary>
internal sealed class MegaBlockTrimRule : ICompactionRule
{
    public string Name => "mega-block-trim";

    internal const int MaxBlockBytes = 32768;

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;
        var age = new AgeIndex(records);

        for (int pos = 0; pos < records.Count; pos++)
        {
            TranscriptRecord rec = records[pos];
            if (rec.IsProtected())
                continue;
            if (rec.Type is "summary" or "queue-operation")
                continue;
            if (!age.IsMidAged(pos))
                continue; // active tail — leave alone (deviation, see class doc)

            JsonObject node = RuleHelpers.CurrentNode(rec);
            JsonObject? clone = null;
            int bi = -1;
            foreach (JsonNode? block in RuleHelpers.ContentBlocks(node))
            {
                bi++;
                if (block is not JsonObject b)
                    continue;
                string? btype = b["type"].GetString();
                string? field = btype switch
                {
                    "text" => "text",
                    "tool_result" when b["content"] is JsonValue cv
                        && cv.TryGetValue<string>(out _) => "content",
                    _ => null,
                };
                if (field is null)
                    continue;
                string text = b[field]!.GetValue<string>();
                if (RuleHelpers.Utf8Len(text) <= MaxBlockBytes)
                    continue;
                // Fixpoint guard: never re-trim our own output (see TrimSentinel).
                if (text.Contains(RuleHelpers.TrimSentinel, StringComparison.Ordinal))
                    continue;

                RuleHelpers.CloneBlockAt(ref clone, node, bi)[field] =
                    RuleHelpers.HeadTailTrimBytes(text, MaxBlockBytes);
            }

            if (clone is not null)
                RuleHelpers.SetReplacement(rec, clone, Name);
        }
    }
}
