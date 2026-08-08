using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// Remove duplicate &lt;system-reminder&gt; payloads, keeping only the first
/// occurrence (faithful port of cozempic's system-reminder-dedup). The harness
/// re-injects identical reminder text many times per session; the model only
/// needs one copy in context.
/// </summary>
internal sealed partial class SystemReminderDedupRule : ICompactionRule
{
    public string Name => "system-reminder-dedup";

    [GeneratedRegex("<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex Reminder();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessNewlines();

    public void Apply(TranscriptFile transcript)
    {
        var seen = new HashSet<string>();

        foreach (var rec in transcript.Records)
        {
            if (rec.IsProtected())
                continue;

            var node = RuleHelpers.CurrentNode(rec);

            // Reminders are injected into plain-string user prompts too. Write the
            // deduped text back as a STRING — cozempic coerced these to block
            // lists, silently breaking the "real user message has string content"
            // invariant that turn detection relies on.
            if ((node["message"] as JsonObject)?["content"] is JsonValue sv
                && sv.TryGetValue(out string? promptText))
            {
                if (DedupIn(promptText, seen) is string dedupedPrompt)
                {
                    var stringClone = (JsonObject)node.DeepClone();
                    ((JsonObject)stringClone["message"]!)["content"] = dedupedPrompt;
                    RuleHelpers.SetReplacement(rec, stringClone, Name);
                }
                continue;
            }

            JsonObject? clone = null;
            int blockIndex = -1;
            foreach (var block in RuleHelpers.ContentBlocks(node))
            {
                blockIndex++;
                if (block is not JsonObject b)
                    continue;
                string? btype = b["type"].GetString();
                if (btype is not ("text" or "tool_result"))
                    continue;

                // text blocks carry "text"; tool_result only qualifies with string content.
                bool isText = btype == "text";
                string? text = isText
                    ? (b["text"] as JsonValue).GetString()
                    : b["content"] as JsonValue is JsonValue cv && cv.TryGetValue(out string? cs) ? cs : null;
                if (string.IsNullOrEmpty(text))
                    continue;

                if (DedupIn(text, seen) is not string newText)
                    continue;

                RuleHelpers.CloneBlockAt(ref clone, node, blockIndex)[isText ? "text" : "content"] = newText;
            }

            if (clone is not null)
                RuleHelpers.SetReplacement(rec, clone, Name);
        }
    }

    /// <summary>
    /// Registers first occurrences in <paramref name="seen"/>, removes repeats.
    /// Returns the cleaned text, or null when nothing changed.
    /// </summary>
    private static string? DedupIn(string text, HashSet<string> seen)
    {
        // Remove by position, never by value: Replace(value, "") would also erase
        // the first occurrence when the SAME reminder repeats within this text,
        // leaving it surviving nowhere in context.
        List<(int Index, int Length)>? repeats = null;
        foreach (Match m in Reminder().Matches(text))
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.Value)));
            if (!seen.Add(hash))
                (repeats ??= []).Add((m.Index, m.Length));
        }
        if (repeats is null)
            return null;
        var sb = new StringBuilder(text);
        for (int i = repeats.Count - 1; i >= 0; i--)
            sb.Remove(repeats[i].Index, repeats[i].Length);
        return ExcessNewlines().Replace(sb.ToString(), "\n\n").Trim();
    }
}
