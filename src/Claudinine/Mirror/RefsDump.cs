namespace Claudinine.Mirror;

/// <summary>
/// The shell-free retrieval surface for Cowork local mode: every mirrored record
/// is dumped as plain files under `outputs/.claudinine/refs/` — `&lt;ref&gt;.txt` for
/// text (tool_result output, or the serialized tool_use input anchor stubs point
/// at) and `&lt;ref&gt;-media-&lt;n&gt;&lt;ext&gt;` for base64 media — where `&lt;ref&gt;` is the same
/// 8-hex uuid prefix the digest `[ref]` lines carry. The model's Read/Grep tools
/// can reach `outputs/` (it is the agent's cwd) even though the colocated mirror
/// under `.claude/projects/` is behind the connected-folder allowlist and the
/// only shell is a Linux microVM that is usually down (docs/cowork-compatibility.md
/// E6/B6). Both trees live under the same `local_&lt;uuid&gt;` root, so the dump
/// shares the mirror's lifetime by placement, the same durability argument as
/// colocation itself.
///
/// Incremental: a per-stem stamp file records the mirror length last dumped; an
/// unchanged mirror skips the whole read (same length-keyed, fail-closed-to-full-
/// work pattern as SeenCache). A stale stamp re-derives every file from the
/// mirror — deterministic, because the mirror is append-only — which also makes
/// a deleted-refs-dir self-heal: the next pass regenerates everything.
///
/// Two records CAN share an 8-hex prefix (astronomically rare on real GUID
/// uuids, guaranteed on synthetic fixtures); like GetVerb's prefix matching,
/// which returns every match, the dump AGGREGATES: their texts share one
/// `&lt;ref&gt;.txt` in mirror order and their media continue one ordinal sequence.
/// </summary>
internal static class RefsDump
{
    /// <summary>
    /// Bring the refs dump up to date with the transcript's mirror. Returns
    /// false only when the dump could not be written at all — in local mode the
    /// headers PROMISE these files, so the caller fails closed like mirror-first
    /// (no dump, no compaction). A transcript with no mirror yet is a clean true.
    /// </summary>
    public static bool TryEnsureCurrent(string transcriptPath, string refsDir)
    {
        try
        {
            string mirrorPath = MirrorLocator.PathFor(transcriptPath);
            if (!File.Exists(mirrorPath))
                return true; // nothing mirrored yet: nothing to promise
            long mirrorLength = new FileInfo(mirrorPath).Length;
            string stem = Path.GetFileNameWithoutExtension(mirrorPath);
            string stamp = Path.Combine(refsDir, "." + stem + ".dumped");
            if (ReadStamp(stamp) == mirrorLength)
                return true;

            Directory.CreateDirectory(refsDir);
            var texts = new Dictionary<string, List<string>>();
            var mediaOrdinals = new Dictionary<string, int>();
            foreach ((string _, var node) in Jsonl.ReadRecords(mirrorPath, skipFirst: true))
            {
                if (node?["uuid"].GetString() is not string uuid)
                    continue;
                DumpRecord(new JsonView(node), RuleHelpers.RefPrefix(uuid), refsDir,
                    texts, mediaOrdinals);
            }
            foreach ((string refId, var parts) in texts)
                WriteIfChanged(Path.Combine(refsDir, refId + ".txt"), string.Join("\n", parts));

            // Stamp AFTER the files are on disk; a crash mid-dump re-runs the
            // (idempotent) dump on the next pass.
            File.WriteAllText(stamp, mirrorLength.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long ReadStamp(string stamp)
    {
        try
        {
            if (File.Exists(stamp) && long.TryParse(File.ReadAllText(stamp), out long length))
                return length;
        }
        catch
        {
            // Unreadable stamp == stale stamp: fall through to the full dump.
        }
        return -1;
    }

    private static void DumpRecord(JsonView rec, string refId, string refsDir,
        Dictionary<string, List<string>> texts, Dictionary<string, int> mediaOrdinals)
    {
        // Text: the same content `claudinine get --ref` serves — tool_result
        // output first, tool_use inputs after (a record is one or the other,
        // but the enumeration mirrors GetVerb's so the surfaces never diverge).
        foreach (var b in RuleHelpers.BlocksOfType(rec, "tool_result"))
        {
            string text = RuleHelpers.ResultText(b);
            if (text.Length > 0)
                AddText(texts, refId, text);
        }
        foreach (var b in RuleHelpers.BlocksOfType(rec, "tool_use"))
        {
            var input = b["input"];
            if (input.IsObject)
                AddText(texts, refId, $"{b["name"].AsString() ?? "?"} input: {input.ToCompactJson()}");
        }

        // Media: deterministic names, ordinals counted over the SAME enumeration
        // GetVerb.DecodeMediaBlocks uses (top-level image/document blocks, plus
        // images nested in a tool_result's content array, in record order) so
        // the `<ref>-media-*` glob the stubs promise always covers them.
        foreach (var b in RuleHelpers.ContentBlocks(rec).Where(x => x.IsObject))
        {
            string? btype = b["type"].AsString();
            if (btype is "image" or "document")
            {
                DumpMedia(b, refsDir, refId, NextOrdinal(mediaOrdinals, refId));
            }
            else if (btype == "tool_result" && b["content"].IsArray)
            {
                foreach (var ib in b["content"].Items.Where(x =>
                    x.IsObject && x["type"].AsString() is "image" or "document"))
                {
                    DumpMedia(ib, refsDir, refId, NextOrdinal(mediaOrdinals, refId));
                }
            }
        }
    }

    private static void AddText(Dictionary<string, List<string>> texts, string refId, string part)
    {
        if (!texts.TryGetValue(refId, out var parts))
            texts[refId] = parts = [];
        parts.Add(part);
    }

    private static int NextOrdinal(Dictionary<string, int> ordinals, string refId)
    {
        int n = ordinals.GetValueOrDefault(refId);
        ordinals[refId] = n + 1;
        return n;
    }

    private static void DumpMedia(JsonView block, string refsDir, string refId, int index)
    {
        var source = block["source"];
        if (!source.IsObject || source["type"].AsString() != "base64"
            || source["data"].AsString() is not string data)
        {
            return; // url or unknown source: nothing to materialize
        }
        string ext = RuleHelpers.MediaFileExtension(source["media_type"].AsString());
        string path = Path.Combine(refsDir, $"{refId}-media-{index}{ext}");
        if (File.Exists(path))
            return;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            return; // undecodable block: skip it, keep dumping the rest
        }
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private static void WriteIfChanged(string path, string content)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            return; // byte-identical: keep the mtime, same discipline as Launcher
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
