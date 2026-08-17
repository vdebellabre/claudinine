namespace Claudinine.Rules;

/// <summary>
/// Stub old base64 media blocks — images pasted into prompts, base64 document
/// blocks (PDFs), and screenshots nested inside a tool_result's content array.
/// Descends from cozempic's image-strip with two long-standing deviations
/// (age-gated instead of keep-newest-by-count for idempotence under per-turn
/// reruns; blocks are stubbed, never deleted, so a content array can't end up
/// empty) plus the mirror retrieval loop: the stub names the exact retrieval —
/// `sh "&lt;run.sh&gt;" get &lt;sid&gt; --ref &lt;uuid&gt; --media` (which decodes the mirrored
/// block to a file the Read tool renders), or in Cowork local mode the
/// RefsDump file the model's own Read tool can open — so the content re-enters
/// context as fresh vision input instead of being lost. Unlike short headers,
/// media stubs can exist with no RETRIEVAL block anywhere in the file (an
/// image-share turn strips without any chain collapse), so they must stay
/// SELF-SUFFICIENT: full command or full path, never a pointer. Legacy
/// dead-end stubs ("re-request if needed") and 0.1.x–0.4.x bare-`claudinine`
/// stubs (a command with no PATH entry on hosted installs) are upgraded in
/// place to the current form.
///
/// The stub sid is the FILE STEM — the mirror key — not the record's sessionId:
/// the two are equal for main transcripts, but a subagent record's sessionId
/// names the PARENT session, whose mirror never holds the record.
/// </summary>
internal sealed class ImageStripRule : ICompactionRule
{
    public string Name => "image-strip";

    /// <summary>The pre-0.1.6 stub text, upgraded retroactively when addressable.</summary>
    private const string LegacyStubPrefix = "[claudinine: old screenshot removed";

    /// <summary>The 0.1.x–0.4.x stub command clause, upgraded retroactively —
    /// the current forms never contain it, which is the idempotence test.</summary>
    private const string OldCommandMarker = " archived — claudinine get ";

    private const string StubPrefix = "[claudinine: ";

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;
        var age = new AgeIndex(records);
        string fileSid = Path.GetFileNameWithoutExtension(transcript.Path);
        string launcher = Launcher.HeaderPathFor(transcript.Path);
        string? refsDir = LocalCowork.HeaderRefsDirFor(transcript.Path);

        for (int pos = 0; pos < records.Count; pos++)
        {
            var rec = records[pos];
            if (rec.IsProtected())
                continue;
            if (!age.IsImageAged(pos))
                continue; // recently shared — keep (image clock, faster than IsMidAged)

            var node = rec.CurrentView;
            string? refPrefix = rec.Uuid is string u ? RuleHelpers.RefPrefix(u) : null;
            JsonObject? clone = null;
            int bi = -1;
            foreach (var b in RuleHelpers.ContentBlocks(node))
            {
                bi++;
                if (!b.IsObject)
                    continue;
                switch (b["type"].AsString())
                {
                    case "image":
                    case "document" when SourceType(b) == "base64":
                        Stub(ref clone, rec, bi, b, fileSid, launcher, refsDir, refPrefix);
                        break;

                    case "text" when refPrefix is not null
                        && b["text"].AsString() is string t:
                        if (t.StartsWith(LegacyStubPrefix, StringComparison.Ordinal))
                        {
                            // The original media info is gone from a legacy stub;
                            // "image" is all it ever replaced.
                            WriteStub(RuleHelpers.CloneBlockAt(ref clone, rec, bi),
                                "image", fileSid, launcher, refsDir, refPrefix);
                        }
                        else if (t.StartsWith(StubPrefix, StringComparison.Ordinal)
                            && t.Contains(OldCommandMarker, StringComparison.Ordinal))
                        {
                            // 0.1.x–0.4.x form: keep its media label AND its sid,
                            // respell only the retrieval. The sid is preserved on
                            // purpose — a fork-parent sid in an old stub is
                            // ForkHealRule's to retarget after validation, never
                            // this rule's to overwrite. Label and sid sit at
                            // fixed positions by construction; fail closed on
                            // anything that doesn't parse.
                            int labelEnd = t.IndexOf(OldCommandMarker, StringComparison.Ordinal);
                            int sidStart = labelEnd + OldCommandMarker.Length;
                            int sidEnd = t.IndexOf(' ', sidStart);
                            if (sidEnd > sidStart)
                            {
                                WriteStub(RuleHelpers.CloneBlockAt(ref clone, rec, bi),
                                    t[StubPrefix.Length..labelEnd], t[sidStart..sidEnd],
                                    launcher, refsDir, refPrefix);
                            }
                        }
                        break;

                    case "tool_result" when b["content"].IsArray:
                        var inner = b["content"];
                        for (int ti = 0; ti < inner.Count; ti++)
                        {
                            var ib = inner[ti];
                            if (!ib.IsObject || ib["type"].AsString() != "image")
                                continue;

                            var cloneResult = RuleHelpers.CloneBlockAt(ref clone, rec, bi);
                            var cloneInner = (JsonObject)((JsonArray)cloneResult["content"]!)[ti]!;
                            WriteStub(cloneInner, Describe(ib), fileSid, launcher, refsDir, refPrefix);
                        }
                        break;
                }
            }

            if (clone is not null)
                RuleHelpers.SetReplacement(rec, clone, Name);
        }
    }

    private static void Stub(
        ref JsonObject? clone, TranscriptRecord rec, int blockIndex, JsonView original,
        string sid, string launcher, string? refsDir, string? refPrefix) =>
        WriteStub(RuleHelpers.CloneBlockAt(ref clone, rec, blockIndex), Describe(original),
            sid, launcher, refsDir, refPrefix);

    private static void WriteStub(JsonObject cloneBlock, string label,
        string sid, string launcher, string? refsDir, string? refPrefix)
    {
        // No ordinal in the local form on purpose: a record can hold several
        // media blocks and older passes may have stubbed some already, so the
        // block's ordinal among the ORIGINAL record's media (which is what
        // RefsDump names files by) is not recoverable from the current record.
        // The glob names them all; the common case is exactly one file.
        string text = refPrefix is null
            // No retrieval address (uuid-less record): the mirror can't serve
            // it, so keep the honest dead-end wording.
            ? $"[claudinine: old {label} removed — re-request if needed]"
            : refsDir is null
                ? $"[claudinine: {label} archived — sh \"{launcher}\" get {sid} --ref {refPrefix} --media " +
                  "decodes it to a file; Read that file to view it]"
                : $"[claudinine: {label} archived — Glob {refsDir}/{refPrefix}-media-*.* " +
                  "and Read the file to view it]";
        cloneBlock.Clear();
        cloneBlock["type"] = "text";
        cloneBlock["text"] = text;
    }

    private static string? SourceType(JsonView block) =>
        block["source"]["type"].AsString();

    /// <summary>"image/png, 498KB" — enough to decide whether retrieval is worth it.</summary>
    private static string Describe(JsonView block)
    {
        var source = block["source"];
        string label = source["media_type"].AsString()
            ?? block["type"].AsString() ?? "image";
        if (source["data"].AsString() is string data && data.Length > 0)
            label += $", {Math.Max(1, data.Length * 3L / 4 / 1024)}KB";
        else if (SourceType(block) == "url")
            label += ", url source";
        return label;
    }
}
