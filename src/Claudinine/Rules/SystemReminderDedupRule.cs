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

        foreach (TranscriptRecord rec in transcript.Records)
        {
            if (rec.IsProtected())
                continue;

            JsonObject node = RuleHelpers.CurrentNode(rec);

            // Reminders are injected into plain-string user prompts too. Write the
            // deduped text back as a STRING — cozempic coerced these to block
            // lists, silently breaking the "real user message has string content"
            // invariant that turn detection relies on.
            if ((node["message"] as JsonObject)?["content"] is JsonValue sv
                && sv.TryGetValue<string>(out string? promptText))
            {
                if (DedupIn(promptText, seen) is string dedupedPrompt)
                {
                    JsonObject stringClone = (JsonObject)node.DeepClone();
                    ((JsonObject)stringClone["message"]!)["content"] = dedupedPrompt;
                    RuleHelpers.SetReplacement(rec, stringClone, Name);
                }
                continue;
            }

            JsonObject? clone = null;
            int blockIndex = -1;
            foreach (JsonNode? block in RuleHelpers.ContentBlocks(node))
            {
                blockIndex++;
                if (block is not JsonObject b)
                    continue;
                string? btype = b["type"]?.GetValue<string>();
                if (btype is not ("text" or "tool_result"))
                    continue;

                // text blocks carry "text"; tool_result only qualifies with string content.
                bool isText = btype == "text";
                string? text = isText
                    ? (b["text"] as JsonValue)?.GetValue<string>()
                    : (b["content"] as JsonValue) is JsonValue cv && cv.TryGetValue<string>(out string? cs) ? cs : null;
                if (string.IsNullOrEmpty(text))
                    continue;

                if (DedupIn(text, seen) is not string newText)
                    continue;

                clone ??= (JsonObject)node.DeepClone();
                JsonObject cloneBlock = (JsonObject)RuleHelpers.ContentBlocks(clone).ElementAt(blockIndex)!;
                cloneBlock[isText ? "text" : "content"] = newText;
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
        string newText = text;
        bool changed = false;
        foreach (Match m in Reminder().Matches(text))
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.Value)));
            if (!seen.Add(hash))
            {
                newText = newText.Replace(m.Value, "");
                changed = true;
            }
        }
        if (!changed)
            return null;
        return ExcessNewlines().Replace(newText, "\n\n").Trim();
    }
}
