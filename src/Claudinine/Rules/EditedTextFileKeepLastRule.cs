using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Keep-last supersession for edited_text_file attachments. Each one carries the
/// ENTIRE current file content (not a diff), the loader replays every copy into
/// resumed context, and N out-of-band modifications leave N full copies (census
/// 2026-08-07: 1.8MB across the corpus, avg 5.1KB/record — the fattest per-record
/// type). A superseded snippet is a stale full copy competing with current truth;
/// canary-verified that its entire usable value is the latest content, so per
/// filename only the LAST record survives. Invariant: keep the last, never an
/// earlier one — a surviving snippet is presented as current content, so keeping
/// a stale one would be worse than the status quo. Records sit ON the uuid chain,
/// so removal leans on the rewrite layer's rechaining; the last occurrence per
/// file is always kept, so a tail attachment is safe by construction.
/// </summary>
internal sealed class EditedTextFileKeepLastRule : ICompactionRule
{
    public string Name => "edited-text-file-keep-last";

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;
        var lastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < records.Count; i++)
        {
            if (Filename(records[i]) is string file)
                lastIndex[file] = i;
        }
        for (int i = 0; i < records.Count; i++)
        {
            TranscriptRecord rec = records[i];
            if (Filename(rec) is string file && i != lastIndex[file] && !rec.IsProtected())
                rec.Removed = true;
        }
    }

    /// <summary>The attachment's filename key, or null if this is not an edited_text_file.</summary>
    private static string? Filename(TranscriptRecord rec)
    {
        if (rec.Type != "attachment" || rec.Node["attachment"] is not JsonObject att)
            return null;
        if (att["type"] is not JsonValue tv || !tv.TryGetValue<string>(out string? type)
            || type != "edited_text_file")
            return null;
        return att["filename"] is JsonValue fv && fv.TryGetValue<string>(out string? file)
            && file.Length > 0 ? file : null;
    }
}
