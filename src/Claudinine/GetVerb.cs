using System.Text;
using System.Text.Json.Nodes;
using Claudinine.Mirror;
using Claudinine.Rules;

namespace Claudinine;

/// <summary>
/// `claudinine get <session> [--ref R] [--grep P] [--info] [--full]` — retrieval
/// over the session mirror, the command surface that chain-collapse digest headers
/// promise. Targeted forms first: `--ref --grep` prints matching lines of one
/// call's output; `--info` prices a record before paying for it; `--full` is the
/// last resort. Exit 1 with a stderr hint when nothing matches.
/// </summary>
internal static class GetVerb
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "usage: claudinine get <session-id> [--ref REF] [--grep PATTERN] [--info] [--full]");
            return 1;
        }

        string session = args[0];
        string? refPrefix = null, grep = null;
        bool info = false, full = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ref" when i + 1 < args.Length: refPrefix = args[++i]; break;
                case "--grep" when i + 1 < args.Length: grep = args[++i]; break;
                case "--info": info = true; break;
                case "--full": full = true; break;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    return 1;
            }
        }

        string? mirrorPath = FindMirror(session);
        if (mirrorPath is null)
        {
            Console.Error.WriteLine($"no mirror found for session '{session}' under {MirrorFile.MirrorsDirectory()}");
            return 1;
        }

        var matches = new List<(string Uuid, string Tool, string Arg, string Text)>();
        bool first = true;
        foreach (string line in File.ReadLines(mirrorPath, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            if (first) { first = false; continue; } // header
            JsonObject? rec;
            try { rec = JsonNode.Parse(line) as JsonObject; } catch { continue; }
            if (rec?["uuid"]?.GetValue<string>() is not string uuid)
                continue;
            if (refPrefix is not null && !uuid.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (JsonObject b in RuleHelpers.ContentBlocks(rec).OfType<JsonObject>()
                .Where(x => x["type"]?.GetValue<string>() == "tool_result"))
            {
                string text = RuleHelpers.ResultText(b);
                if (text.Length == 0)
                    continue;
                if (refPrefix is null && grep is not null
                    && !text.Contains(grep, StringComparison.OrdinalIgnoreCase))
                    continue;
                matches.Add((uuid, "", "", text));
            }
        }

        if (matches.Count == 0)
        {
            Console.Error.WriteLine(refPrefix is not null
                ? $"no archived output for ref '{refPrefix}'"
                : $"no archived output matches");
            return 1;
        }

        foreach ((string uuid, _, _, string text) in matches)
        {
            string tag = uuid[..8];
            if (info)
            {
                Console.WriteLine($"[{tag}] {RuleHelpers.Utf8Len(text)} bytes, {text.Split('\n').Length} lines (~{text.Length / 4} tokens)");
                continue;
            }
            if (grep is not null)
            {
                foreach (string l in text.Split('\n'))
                {
                    if (l.Contains(grep, StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"[{tag}] {l}");
                }
                continue;
            }
            if (full || refPrefix is not null)
            {
                Console.WriteLine($"=== [{tag}] ===");
                Console.WriteLine(text);
            }
        }
        return 0;
    }

    /// <summary>Match a mirror by session-id prefix (refs use 8-char prefixes; so can sessions).</summary>
    private static string? FindMirror(string session)
    {
        string dir = MirrorFile.MirrorsDirectory();
        if (!Directory.Exists(dir))
            return null;
        string exact = System.IO.Path.Combine(dir, session + ".jsonl");
        if (File.Exists(exact))
            return exact;
        var candidates = Directory.EnumerateFiles(dir, session + "*.jsonl").ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
