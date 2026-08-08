namespace Claudinine.Transcript;

/// <summary>
/// Shared JSONL file plumbing: the one reader every scan goes through (CR trim,
/// blank-line skip, tolerant parse) and the durable writers. Hand-rolled copies
/// of these loops drifted once already — add capabilities here, not at call sites.
/// </summary>
internal static class Jsonl
{
    /// <summary>
    /// Iterate a JSONL file's lines: CR-trimmed, blank lines skipped, each line
    /// tolerantly parsed (Node is null when the line is not a JSON object).
    /// <paramref name="skipFirst"/> drops the first non-blank line — the header
    /// convention of mirror files. Yielded nodes are fresh parses owned by the
    /// caller (safe to mutate).
    /// </summary>
    public static IEnumerable<(string Line, JsonObject? Node)> ReadRecords(
        string path, bool skipFirst = false)
    {
        bool first = true;
        foreach (string raw in File.ReadLines(path, Encoding.UTF8))
        {
            string line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length == 0)
                continue;
            if (first)
            {
                first = false;
                if (skipFirst)
                    continue;
            }
            JsonObject? node;
            try { node = JsonNode.Parse(line) as JsonObject; }
            catch { node = null; }
            yield return (line, node);
        }
    }

    /// <summary>
    /// Write lines (LF-terminated, UTF-8 no BOM) and flush to DISK before
    /// returning. The flushToDisk is load-bearing everywhere this is used: the
    /// mirror-first invariant is only real once the bytes are.
    /// </summary>
    public static void WriteLinesDurably(string path, FileMode mode, IEnumerable<string> lines)
    {
        using var stream = new FileStream(path, mode, FileAccess.Write,
            mode == FileMode.Append ? FileShare.Read : FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (string line in lines)
            writer.Write(line + "\n");
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Temp suffix for atomic swaps — deliberately NOT ending in .jsonl: session
    /// discovery scans *.jsonl and must never see a half-written file.
    /// </summary>
    public const string TempSuffix = ".claudinine-tmp";

    /// <summary>
    /// Durably write content to a temp sibling, then move it over the target —
    /// the rename can survive a power cut the data didn't, so the flush comes
    /// first. Cleans up the temp and rethrows on failure.
    /// </summary>
    public static void ReplaceAtomically(string path, string content)
    {
        string temp = path + TempSuffix;
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }
}
