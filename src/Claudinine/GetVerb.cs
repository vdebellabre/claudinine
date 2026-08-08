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
    /// <summary>Human/model-readable JSON: no "-style escaping of quotes and symbols.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions RelaxedJson =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

        List<string> mirrorPaths = FindMirrors(session);
        if (mirrorPaths.Count == 0)
        {
            IReadOnlyList<string> searched = MirrorFile.SearchDirectories();
            Console.Error.WriteLine(
                $"no mirror found for session '{session}' (searched: " +
                (searched.Count == 0 ? "no mirror directory exists" : string.Join("; ", searched)) + ")");
            return 1;
        }

        var matches = new List<(string Uuid, string Tool, string Arg, string Text)>();
        // A cross-context resume can mirror the same record into two dirs — the
        // copies are identical originals, so the first file to carry a uuid wins.
        var seenRecords = new HashSet<string>();
        foreach (string mirrorPath in mirrorPaths)
        {
            bool first = true;
            foreach (string line in File.ReadLines(mirrorPath, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                if (first) { first = false; continue; } // header
                JsonObject? rec;
                try { rec = JsonNode.Parse(line) as JsonObject; } catch { continue; }
                if (rec?["uuid"]?.GetValue<string>() is not string uuid)
                    continue;
                if (!seenRecords.Add(uuid))
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
                // A --ref can also address a tool_use record: anchor-input stubs
                // point here for the original input. Only when explicitly addressed
                // — plain --grep stays an output search, not an input search.
                if (refPrefix is null)
                    continue;
                foreach (JsonObject b in RuleHelpers.ContentBlocks(rec).OfType<JsonObject>()
                    .Where(x => x["type"]?.GetValue<string>() == "tool_use"))
                {
                    if (b["input"] is not JsonObject input)
                        continue;
                    string name = b["name"]?.GetValue<string>() ?? "?";
                    matches.Add((uuid, "", "", $"{name} input: {input.ToJsonString(RelaxedJson)}"));
                }
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

    /// <summary>
    /// Match mirrors by session-id prefix (refs use 8-char prefixes; so can
    /// sessions) across every known mirror directory. The same session may have a
    /// mirror in several dirs (cross-context resume) — all of them are returned; a
    /// prefix that resolves to more than one distinct session id matches nothing.
    /// </summary>
    private static List<string> FindMirrors(string session)
    {
        var exact = new List<string>();
        var byPrefix = new List<string>();
        foreach (string dir in MirrorFile.SearchDirectories())
        {
            string candidate = System.IO.Path.Combine(dir, session + ".jsonl");
            if (File.Exists(candidate))
                exact.Add(candidate);
            else
                byPrefix.AddRange(Directory.EnumerateFiles(dir, session + "*.jsonl"));
        }
        if (exact.Count > 0)
            return exact;
        int distinct = byPrefix
            .Select(p => System.IO.Path.GetFileNameWithoutExtension(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return distinct == 1 ? byPrefix : [];
    }
}
