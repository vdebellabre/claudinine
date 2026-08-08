using System.Text;
using System.Text.Json.Nodes;
using Claudinine.Mirror;
using Claudinine.Rules;
using Claudinine.Transcript;

namespace Claudinine;

/// <summary>
/// `claudinine get <session> [--ref R] [--grep P] [--info] [--full] [--media]` —
/// retrieval over the session mirror, the command surface that chain-collapse
/// digest headers promise. Targeted forms first: `--ref --grep` prints matching
/// lines of one call's output; `--info` prices a record before paying for it;
/// `--full` is the last resort. `--media` (requires `--ref`) decodes the record's
/// base64 media blocks — pasted images, PDF documents, screenshots nested in
/// tool_results — to files under the temp dir and prints their paths: base64 on
/// stdout is useless to a model, but a decoded file re-enters context as fresh
/// vision input via the Read tool. Exit 1 with a stderr hint when nothing matches.
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
                "usage: claudinine get <session-id> [--ref REF] [--grep PATTERN] [--info] [--full] [--media]");
            return 1;
        }

        string session = args[0];
        string? refPrefix = null, grep = null;
        bool info = false, full = false, media = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ref" when i + 1 < args.Length: refPrefix = args[++i]; break;
                case "--grep" when i + 1 < args.Length: grep = args[++i]; break;
                case "--info": info = true; break;
                case "--full": full = true; break;
                case "--media": media = true; break;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    return 1;
            }
        }
        if (media && refPrefix is null)
        {
            Console.Error.WriteLine("--media requires --ref (decoding every media block of a session is never what you want)");
            return 1;
        }

        List<string> mirrorPaths = MirrorFile.FindSessionMirrors(session);
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
                if (rec?["uuid"].GetString() is not string uuid)
                    continue;
                if (!seenRecords.Add(uuid))
                    continue;
                if (refPrefix is not null && !uuid.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (media)
                {
                    DecodeMediaBlocks(rec, uuid, matches);
                    continue;
                }
                foreach (JsonObject b in RuleHelpers.ContentBlocks(rec).OfType<JsonObject>()
                    .Where(x => x["type"].GetString() == "tool_result"))
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
                    .Where(x => x["type"].GetString() == "tool_use"))
                {
                    if (b["input"] is not JsonObject input)
                        continue;
                    string name = b["name"].GetString() ?? "?";
                    matches.Add((uuid, "", "", $"{name} input: {input.ToJsonString(RelaxedJson)}"));
                }
            }
        }

        if (matches.Count == 0)
        {
            Console.Error.WriteLine(
                media ? $"no archived media for ref '{refPrefix}'"
                : refPrefix is not null ? $"no archived output for ref '{refPrefix}'"
                : $"no archived output matches");
            return 1;
        }

        foreach ((string uuid, _, _, string text) in matches)
        {
            string tag = RuleHelpers.RefPrefix(uuid);
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
    /// Decode every base64 media block of one mirrored record (top-level image and
    /// document blocks, plus images nested in tool_result content) to a file and
    /// report the paths as one match. Deterministic names (uuid prefix + block
    /// ordinal) make repeated retrievals overwrite rather than accumulate.
    /// </summary>
    private static void DecodeMediaBlocks(JsonObject rec, string uuid,
        List<(string Uuid, string Tool, string Arg, string Text)> matches)
    {
        var lines = new List<string>();
        int n = 0;
        foreach (JsonObject b in RuleHelpers.ContentBlocks(rec).OfType<JsonObject>())
        {
            string? btype = b["type"].GetString();
            if (btype is "image" or "document")
            {
                DecodeOne(b, uuid, n++, lines);
            }
            else if (btype == "tool_result" && b["content"] is JsonArray inner)
            {
                foreach (JsonObject ib in inner.OfType<JsonObject>()
                    .Where(x => x["type"].GetString() is "image" or "document"))
                {
                    DecodeOne(ib, uuid, n++, lines);
                }
            }
        }
        if (lines.Count > 0)
            matches.Add((uuid, "", "", string.Join("\n", lines)));
    }

    private static void DecodeOne(JsonObject block, string uuid, int index, List<string> lines)
    {
        if (block["source"] is not JsonObject source)
            return;
        string? sourceType = source["type"].GetString();
        if (sourceType == "url")
        {
            lines.Add($"media {index} is a URL source: {source["url"].GetString() ?? "?"}");
            return;
        }
        if (sourceType != "base64" || source["data"].GetString() is not string data)
            return;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            lines.Add($"media {index}: base64 decode failed");
            return;
        }
        string mediaType = source["media_type"].GetString() ?? "application/octet-stream";
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claudinine", "media");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{RuleHelpers.RefPrefix(uuid)}-{index}{Extension(mediaType)}");
        File.WriteAllBytes(path, bytes);
        lines.Add($"wrote {path} ({mediaType}, {Math.Max(1, bytes.Length / 1024)}KB) — use the Read tool on this file to view it");
    }

    private static string Extension(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "application/pdf" => ".pdf",
        _ => ".bin",
    };

}
