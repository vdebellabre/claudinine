namespace Claudinine.Mirror;

/// <summary>
/// Per-session snapshot of what the app actually LOADED into its context buffer:
/// a `&lt;sid&gt;.load` file next to the mirror holding (uuid, byte-length) for every
/// record the transcript had at load time.
///
/// Written at SessionStart — the app seeds its buffer from the transcript BEFORE
/// hooks run, so the file as the hook first sees it IS the buffer, provided the
/// stamp is taken before our own repair pass mutates anything. The statusline
/// prices a reload against this stamp: a record's reclaim is buffer-size minus
/// file-size, and for records that predate the load the buffer size is the stamp,
/// not the mirror's fat original. Without the watermark the metric reported the
/// standing mirror-vs-transcript gap, which survives a reload — the bar claimed
/// ~48k reclaimable seconds after resuming, when the true figure was zero.
///
/// Format: one header line (same shape as mirror/skip headers, carrying the
/// transcript path for GC), then `uuid&lt;TAB&gt;bytes` per record. Not JSON per line:
/// the stamp is rewritten at every load and read once per assistant message.
/// </summary>
internal static class LoadStamp
{
    /// <summary>
    /// Stamp the transcript as it stands right now. Call before anything mutates
    /// the file. A transcript that does not exist yet (brand-new session) stamps
    /// as empty — "the buffer loaded nothing" — which is exactly what makes a
    /// never-reloaded session price every compaction as reclaimable.
    /// Fail-safe: a stamp we cannot write only costs statusline visibility.
    /// </summary>
    public static void Write(string transcriptPath)
    {
        try
        {
            string dir = MirrorLocator.MirrorsDirectory();
            Directory.CreateDirectory(dir);
            string stem = Path.GetFileNameWithoutExtension(transcriptPath);
            string stampPath = Path.Combine(dir, stem + ".load");

            // Temp + move: the statusline may read mid-write, and a torn stamp
            // (header + partial body) silently misprices the missing records.
            string tmp = stampPath + ".tmp";
            using (var writer = new StreamWriter(tmp, append: false, new UTF8Encoding(false)))
            {
                writer.Write(MirrorFormat.Line("loadStampOf", Path.GetFullPath(transcriptPath)));
                writer.Write('\n');
                if (File.Exists(transcriptPath))
                {
                    foreach ((string uuid, long size) in ScanRecordSizes(transcriptPath))
                    {
                        writer.Write(uuid);
                        writer.Write('\t');
                        writer.Write(size);
                        writer.Write('\n');
                    }
                }
            }
            File.Move(tmp, stampPath, overwrite: true);
        }
        catch
        {
            // No stamp means the statusline stays silent for this session —
            // degraded visibility, never a broken session.
        }
    }

    /// <summary>
    /// The most recent stamp for this transcript, as uuid → bytes-at-load, or
    /// null when no readable stamp exists (session predates the feature, or the
    /// write failed). Probes every known mirror dir for the same reason mirror
    /// reads do — the statusline runs without CLAUDE_PLUGIN_DATA — and when a
    /// cross-context session has several, the newest is the last load.
    /// </summary>
    public static Dictionary<string, long>? Read(string transcriptPath)
    {
        string stem = Path.GetFileNameWithoutExtension(transcriptPath);
        string? newest = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (string dir in MirrorLocator.SearchDirectories())
        {
            var candidate = new FileInfo(Path.Combine(dir, stem + ".load"));
            if (candidate.Exists && candidate.LastWriteTimeUtc > newestTime)
            {
                newest = candidate.FullName;
                newestTime = candidate.LastWriteTimeUtc;
            }
        }
        if (newest is null)
            return null;

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        bool headerSeen = false;
        foreach (string line in File.ReadLines(newest, Encoding.UTF8))
        {
            if (!headerSeen)
            {
                // Format sentinel: a first line that is not our header means the
                // file is not a stamp we understand — say nothing rather than
                // price a reload off garbage.
                if (!line.StartsWith("{\"claudinine\"", StringComparison.Ordinal))
                    return null;
                headerSeen = true;
                continue;
            }
            int tab = line.IndexOf('\t');
            if (tab > 0 && long.TryParse(line.AsSpan(tab + 1), out long size))
                sizes[line[..tab]] = size;
        }
        return headerSeen ? sizes : null;
    }

    /// <summary>
    /// Streams (uuid, byte-length) per uuid-bearing line. Reads line by line
    /// rather than loading the file: transcripts run to megabytes, and the
    /// statusline re-scans once per assistant message inside a 300ms debounce.
    /// </summary>
    internal static IEnumerable<(string Uuid, long Size)> ScanRecordSizes(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
                continue;
            string? uuid = ExtractUuid(line);
            if (uuid is not null)
                yield return (uuid, Encoding.UTF8.GetByteCount(line));
        }
    }

    /// <summary>
    /// The value of the top-level "uuid" field, or null if absent.
    ///
    /// Deliberately a substring scan, not a JSON parse: these records embed whole
    /// tool outputs, and every caller scans full files on a hot path. A false
    /// positive would need the literal `"uuid":"` inside archived content AND a
    /// matching uuid in the other file, which costs an over-count of one record
    /// rather than a wrong verdict.
    /// </summary>
    private static string? ExtractUuid(string line)
    {
        const string Key = "\"uuid\":\"";
        int start = line.IndexOf(Key, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += Key.Length;
        int end = line.IndexOf('"', start);
        return end > start ? line[start..end] : null;
    }

    /// <summary>
    /// The `.load` half of garbage collection: reap stamps whose transcript no
    /// longer exists. Called per-dir by <see cref="MirrorFile.CollectGarbage()"/>.
    /// </summary>
    internal static void CollectGarbage(string dir)
    {
        foreach (string stamp in Directory.EnumerateFiles(dir, "*.load"))
        {
            try
            {
                string? line = File.ReadLines(stamp, Encoding.UTF8).FirstOrDefault();
                if (line is null) continue;
                if (JsonNode.Parse(line) is not JsonObject header) continue;
                string? target = header["claudinine"]?["loadStampOf"].GetString();
                if (target is not null && !File.Exists(target))
                    File.Delete(stamp);
            }
            catch
            {
                // Unreadable stamp: leave it for a human.
            }
        }
    }
}
