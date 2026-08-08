namespace Claudinine.Rules;

/// <summary>
/// Supersession for edited_text_file attachments. Each one carries the ENTIRE
/// current file content (not a diff), the loader replays every copy into resumed
/// context, and N out-of-band modifications leave N full copies (census
/// 2026-08-07: 1.8MB across the corpus, avg 5.1KB/record — the fattest per-record
/// type). A notice is removable when a LATER record gives the model a full view
/// of the same file, because file order is time order — the notice is then a
/// strictly staler copy presented as current truth. Full-view events:
///   - a later edited_text_file notice for the same filename (keep-last),
///   - a successful full Read (toolUseResult.file with startLine == 1 and
///     numLines == totalLines — offset/limit reads and errors never qualify),
///   - a successful Write (the model authored the entire content; the result's
///     create/update object proves it landed).
/// Edit results never supersede: a patch builds ON the notice's snapshot instead
/// of replacing it. Bash reads are deliberately excluded: shell path styles make
/// the filename match unprovable. It does not matter if a later rule stubs the
/// superseding record — a stub is an honest "retrieve to see", while a stale
/// snapshot is misinformation. Records sit ON the uuid chain, so removal leans on
/// the rewrite layer's rechaining; a notice with nothing after it (including the
/// tail) is never superseded, so the tail is safe by construction.
/// </summary>
internal sealed class EditedTextFileSupersessionRule : ICompactionRule
{
    public string Name => "edited-text-file-supersession";

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;

        // Full-view scan: latest index per filename at which the model gained a
        // complete view of the file. Tool uses always precede their results in
        // file order, so one forward pass can resolve result carriers by id.
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastFullView = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            if (NoticeFilename(rec) is string noticed)
            {
                lastFullView[noticed] = i;
                continue;
            }
            CollectToolUses(rec, toolNames);
            if (FullViewFilename(rec, toolNames) is string viewed)
                lastFullView[viewed] = i;
        }

        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            if (NoticeFilename(rec) is string file && lastFullView[file] > i && !rec.IsProtected())
                rec.Removed = true;
        }
    }

    /// <summary>The attachment's filename key, or null if this is not an edited_text_file.</summary>
    private static string? NoticeFilename(TranscriptRecord rec)
    {
        if (rec.Type != "attachment" || rec.Node["attachment"] is not JsonObject att)
            return null;
        if (att["type"].GetString() != "edited_text_file")
            return null;
        return att["filename"].GetString() is { Length: > 0 } file ? file : null;
    }

    private static void CollectToolUses(TranscriptRecord rec, Dictionary<string, string> toolNames)
    {
        if (rec.Type != "assistant" || rec.Node["message"]?["content"] is not JsonArray content)
            return;
        foreach (var block in content)
        {
            if (block is JsonObject b && b["type"].GetString() == "tool_use"
                && b["id"].GetString() is string id && b["name"].GetString() is string name)
            {
                toolNames[id] = name;
            }
        }
    }

    /// <summary>
    /// The filename this record proves a full view of, or null. Both predicates
    /// require the harness-written toolUseResult object AND the matching tool
    /// name — errors serialize toolUseResult as a string, so they never qualify.
    /// </summary>
    private static string? FullViewFilename(TranscriptRecord rec, Dictionary<string, string> toolNames)
    {
        if (rec.Type != "user" || rec.Node["toolUseResult"] is not JsonObject result)
            return null;
        string? toolName = ResultToolName(rec, toolNames);

        if (toolName == "Read"
            && result["type"].GetString() == "text"
            && result["file"] is JsonObject file
            && GetInt(file["startLine"]) == 1
            && GetInt(file["numLines"]) is int numLines
            && GetInt(file["totalLines"]) is int totalLines && numLines == totalLines)
        {
            return file["filePath"].GetString() is { Length: > 0 } read ? read : null;
        }

        if (toolName == "Write" && result["type"].GetString() is "create" or "update")
            return result["filePath"].GetString() is { Length: > 0 } written ? written : null;

        return null;
    }

    private static string? ResultToolName(TranscriptRecord rec, Dictionary<string, string> toolNames)
    {
        if (rec.Node["message"]?["content"] is not JsonArray content)
            return null;
        foreach (var block in content)
        {
            if (block is JsonObject b && b["type"].GetString() == "tool_result"
                && b["tool_use_id"].GetString() is string id)
            {
                return toolNames.TryGetValue(id, out string? name) ? name : null;
            }
        }
        return null;
    }

    private static int? GetInt(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue(out int i) ? i : null;
}
