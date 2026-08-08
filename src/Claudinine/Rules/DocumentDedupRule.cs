using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Deduplicate large identical text blocks (CLAUDE.md re-injection, repeated file
/// attachments): later occurrences become a one-line stub pointing back at the
/// first (faithful port of cozempic's document-dedup, ≥1KB blocks).
/// Known ordering caveat, accepted: chain-collapse runs AFTER this rule and may
/// remove the record holding the "first seen earlier" occurrence in the same
/// pass — the stub then points at content that survives only in the mirror.
/// Rare (needs a ≥1KB duplicate inside a collapsed turn) and non-destructive:
/// the original is always mirrored before either rule touches anything.
/// </summary>
internal sealed class DocumentDedupRule : ICompactionRule
{
    public string Name => "document-dedup";

    private const int MinBlockBytes = 1024;

    public void Apply(TranscriptFile transcript)
    {
        // Pass 1: hash every big-enough block, in file order.
        var occurrences = new Dictionary<string, List<(TranscriptRecord Rec, int BlockIndex)>>();
        foreach (TranscriptRecord rec in transcript.Records)
        {
            if (rec.IsProtected())
                continue;
            int bi = -1;
            foreach (JsonNode? block in RuleHelpers.ContentBlocks(RuleHelpers.CurrentNode(rec)))
            {
                bi++;
                string text = RuleHelpers.TextOf(block);
                if (RuleHelpers.Utf8Len(text) < MinBlockBytes)
                    continue;
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                if (!occurrences.TryGetValue(hash, out var list))
                    occurrences[hash] = list = [];
                list.Add((rec, bi));
            }
        }

        // Pass 2: stub every occurrence after the first.
        foreach (var list in occurrences.Values)
        {
            if (list.Count <= 1)
                continue;
            foreach ((TranscriptRecord rec, int blockIndex) in list.Skip(1))
            {
                JsonObject node = RuleHelpers.CurrentNode(rec);
                if (RuleHelpers.ContentBlocks(node).ElementAtOrDefault(blockIndex) is not JsonObject block)
                    continue;
                // Only text and string tool_results, like the original.
                string? field = block["type"].GetString() switch
                {
                    "text" => "text",
                    "tool_result" when block["content"] is JsonValue cv
                        && cv.TryGetValue<string>(out _) => "content",
                    _ => null,
                };
                if (field is null)
                    continue;

                string preview = RuleHelpers.TextOf(block);
                preview = preview[..Math.Min(80, preview.Length)].Replace('\n', ' ');

                JsonObject? clone = null;
                RuleHelpers.CloneBlockAt(ref clone, node, blockIndex)[field] =
                    $"[claudinine: duplicate content removed — first seen earlier: {preview}...]";
                RuleHelpers.SetReplacement(rec, clone!, Name);
            }
        }
    }
}
