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
/// vision input via the Read tool. Bare `get &lt;session&gt;` defaults to the `--info`
/// listing. Exit 1 with a stderr hint when nothing matches — including a
/// `--ref --grep` whose record exists but holds no matching line.
/// </summary>
internal static class GetVerb
{
    public static int Run(string[] args) =>
        Run(args,
            Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Home seam for tests, same reason as CloneVerb: SpecialFolder
    /// ignores env overrides, and sid resolution now globs home's projects.</summary>
    internal static int Run(string[] args, string? pluginData, string home)
    {
        // Same top-level guard as clone: a mirror going away mid-read (GC race,
        // network share) should report and exit 1, not dump a stack trace.
        try
        {
            return RunCore(args, pluginData, home);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"get failed: {e.Message}");
            return 1;
        }
    }

    private static int RunCore(string[] args, string? pluginData, string home)
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

        var mirrorPaths = MirrorLocator.FindSessionMirrors(session, pluginData, home);
        if (mirrorPaths.Count == 0)
        {
            var searched = MirrorLocator.SearchDirectories(pluginData, home);
            Console.Error.WriteLine(
                $"no mirror found for session '{session}' (searched claudinine/ session dirs" +
                " under ~/.claude/projects" +
                (searched.Count == 0 ? "" : " and: " + string.Join("; ", searched)) + ")");
            return 1;
        }

        // Bare `get <session>`: the --info listing is the only output that makes
        // sense for the whole mirror (previously this printed nothing, exit 0 —
        // indistinguishable from "everything was empty").
        if (!info && !full && !media && refPrefix is null && grep is null)
            info = true;

        var matches = new List<(string Uuid, string Text)>();
        // A cross-context resume can mirror the same record into two dirs — the
        // copies are identical originals, so the first file to carry a uuid wins.
        var seenRecords = new HashSet<string>();
        foreach (string mirrorPath in mirrorPaths)
        {
            foreach ((string _, var rec) in Jsonl.ReadRecords(mirrorPath, skipFirst: true))
            {
                if (rec?["uuid"].GetString() is not string uuid)
                    continue;
                if (!seenRecords.Add(uuid))
                    continue;
                if (refPrefix is not null && !uuid.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var view = new JsonView(rec);
                if (media)
                {
                    DecodeMediaBlocks(view, uuid, matches);
                    continue;
                }
                foreach (var b in RuleHelpers.BlocksOfType(view, "tool_result"))
                {
                    string text = RuleHelpers.ResultText(b);
                    if (text.Length == 0)
                        continue;
                    if (refPrefix is null && grep is not null
                        && !text.Contains(grep, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    matches.Add((uuid, text));
                }
                // A --ref can also address a tool_use record: anchor-input stubs
                // point here for the original input. Only when explicitly addressed
                // — plain --grep stays an output search, not an input search.
                if (refPrefix is null)
                    continue;
                foreach (var b in RuleHelpers.BlocksOfType(view, "tool_use"))
                {
                    var input = b["input"];
                    if (!input.IsObject)
                        continue;
                    string name = b["name"].AsString() ?? "?";
                    matches.Add((uuid, $"{name} input: {input.ToCompactJson()}"));
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

        bool printed = false;
        foreach ((string uuid, string text) in matches)
        {
            string tag = RuleHelpers.RefPrefix(uuid);
            if (info)
            {
                Console.WriteLine($"[{tag}] {RuleHelpers.Utf8Len(text)} bytes, {text.AsSpan().Count('\n') + 1} lines (~{text.Length / 4} tokens)");
                printed = true;
                continue;
            }
            if (grep is not null)
            {
                foreach (string l in text.Split('\n'))
                {
                    if (l.Contains(grep, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[{tag}] {l}");
                        printed = true;
                    }
                }
                continue;
            }
            Console.WriteLine($"=== [{tag}] ===");
            Console.WriteLine(text);
            printed = true;
        }
        if (!printed)
        {
            // Only reachable via --ref --grep: the record exists (matches is
            // non-empty) but no line matched. Silence here reads as "the output
            // was empty" — a different claim than "nothing matched".
            Console.Error.WriteLine($"no line of ref '{refPrefix}' matches '{grep}'");
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// Decode every base64 media block of one mirrored record (top-level image and
    /// document blocks, plus images nested in tool_result content) to a file and
    /// report the paths as one match. Deterministic names (uuid prefix + block
    /// ordinal) make repeated retrievals overwrite rather than accumulate.
    /// </summary>
    private static void DecodeMediaBlocks(JsonView rec, string uuid,
        List<(string Uuid, string Text)> matches)
    {
        var lines = new List<string>();
        int n = 0;
        foreach (var b in RuleHelpers.ContentBlocks(rec).Where(x => x.IsObject))
        {
            string? btype = b["type"].AsString();
            if (btype is "image" or "document")
            {
                DecodeOne(b, uuid, n++, lines);
            }
            else if (btype == "tool_result" && b["content"].IsArray)
            {
                foreach (var ib in b["content"].Items.Where(x =>
                    x.IsObject && x["type"].AsString() is "image" or "document"))
                {
                    DecodeOne(ib, uuid, n++, lines);
                }
            }
        }
        if (lines.Count > 0)
            matches.Add((uuid, string.Join("\n", lines)));
    }

    private static void DecodeOne(JsonView block, string uuid, int index, List<string> lines)
    {
        var source = block["source"];
        if (!source.IsObject)
            return;
        string? sourceType = source["type"].AsString();
        if (sourceType == "url")
        {
            lines.Add($"media {index} is a URL source: {source["url"].AsString() ?? "?"}");
            return;
        }
        if (sourceType != "base64" || source["data"].AsString() is not string data)
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
        string mediaType = source["media_type"].AsString() ?? "application/octet-stream";
        string dir = Path.Combine(Path.GetTempPath(), "claudinine", "media");
        string path = Path.Combine(dir, $"{RuleHelpers.RefPrefix(uuid)}-{index}{Extension(mediaType)}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception e)
        {
            // One undecodable/unwritable block must not kill the other blocks'
            // decode — report it as a line instead, same shape as the successes.
            lines.Add($"media {index}: could not write {path}: {e.Message}");
            return;
        }
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
