using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Removes hook_success attachments for hook events PROVEN inert on resume.
/// Canary (2026-08-07, session 6faceebf, v2.1.222): SessionStart hook_success
/// records ARE replayed verbatim into resumed context (all of them, rendered
/// from the content field) — never touch those; Stop and PostToolUse records
/// planted early/mid/late were all invisible to the resumed model — pure disk
/// history, 81% of the type's 2.1MB. Removal is an allowlist: any event not
/// proven inert (SessionStart, PreToolUse — extinct in 2.1.222 sessions and
/// never canaried — or anything new) is kept. Records sit ON the uuid chain, so
/// removal leans on the rewrite layer's rechaining; the loop bound excludes the
/// tail record.
/// </summary>
internal sealed class HookSuccessStripRule : ICompactionRule
{
    public string Name => "hook-success-strip";

    public void Apply(TranscriptFile transcript)
    {
        var records = transcript.Records;
        for (int i = 0; i < records.Count - 1; i++) // tail excluded by loop bound
        {
            var rec = records[i];
            if (rec.Type != "attachment" || rec.IsProtected())
                continue;
            if (rec.Node["isSidechain"] is JsonValue sc && sc.TryGetValue(out bool b) && b)
                continue;
            if (rec.Node["attachment"] is not JsonObject att)
                continue;
            if (att["type"].GetString() != "hook_success")
                continue;
            if (att["hookEvent"].GetString() is "Stop" or "PostToolUse")
                rec.Removed = true;
        }
    }
}
