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
        // Pass 1a: bucket every big-enough block by TEXT LENGTH, in file order.
        // Equal text implies equal length, so only blocks sharing a length can be
        // duplicates — hashing length-unique blocks (the vast majority) is wasted
        // SHA-256 over megabytes.
        var byLength = new Dictionary<int, List<(TranscriptRecord Rec, int BlockIndex, string Text)>>();
        foreach (var rec in transcript.Records)
        {
            // Tail guard, same invariant as everywhere else: the file's final
            // record is never replaced (TryRewrite would refuse the WHOLE pass).
            // A tail occurrence is always last in file order — never the kept
            // first — so excluding it here only defers its stub to a later pass.
            if (ReferenceEquals(rec, transcript.Records[^1]))
                continue;
            if (rec.IsProtected())
                continue;
            int bi = -1;
            foreach (var block in RuleHelpers.ContentBlocks(rec.CurrentView))
            {
                bi++;
                string text = RuleHelpers.TextOf(block);
                if (RuleHelpers.Utf8Len(text) < MinBlockBytes)
                    continue;
                if (!byLength.TryGetValue(text.Length, out var bucket))
                    byLength[text.Length] = bucket = [];
                bucket.Add((rec, bi, text));
            }
        }

        // Pass 1b: group only within length collisions, keyed by the text itself —
        // the strings are already in memory, so a dictionary probe (allocation-free
        // hash + ordinal compare on collision) beats copying each block to UTF-8
        // bytes for a SHA-256. File order is preserved: buckets keep insertion
        // order, so "first occurrence" stays the earliest.
        var occurrences = new Dictionary<string, List<(TranscriptRecord Rec, int BlockIndex)>>(
            StringComparer.Ordinal);
        foreach (var bucket in byLength.Values)
        {
            if (bucket.Count <= 1)
                continue;
            foreach ((var rec, int bi, string text) in bucket)
            {
                if (!occurrences.TryGetValue(text, out var list))
                    occurrences[text] = list = [];
                list.Add((rec, bi));
            }
        }

        // Pass 2: stub every occurrence after the first.
        foreach (var list in occurrences.Values)
        {
            if (list.Count <= 1)
                continue;
            foreach ((var rec, int blockIndex) in list.Skip(1))
            {
                var block = RuleHelpers.ContentBlocks(rec.CurrentView).ElementAtOrDefault(blockIndex);
                if (!block.IsObject)
                    continue;
                // Only text and string tool_results, like the original.
                string? field = block["type"].AsString() switch
                {
                    "text" => "text",
                    "tool_result" when block["content"].AsStringMemo() is not null => "content",
                    _ => null,
                };
                if (field is null)
                    continue;

                string preview = RuleHelpers.TextOf(block);
                preview = preview[..Math.Min(80, preview.Length)].Replace('\n', ' ');

                JsonObject? clone = null;
                RuleHelpers.CloneBlockAt(ref clone, rec, blockIndex)[field] =
                    $"[claudinine: duplicate content removed — first seen earlier: {preview}...]";
                RuleHelpers.SetReplacement(rec, clone!, Name);
            }
        }
    }
}
