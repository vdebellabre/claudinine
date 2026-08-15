namespace Claudinine.Mirror;

/// <summary>
/// `restore-compaction-off` freezes a session via a `&lt;sid&gt;.skip` marker next to
/// its mirror(s). File presence is the whole state: hooks that see it keep
/// mirroring but never compact, so an explicit restore is never silently undone.
/// Markers are written next to every mirror the session has (plus the colocated
/// claudinine dir, the canonical location) because pre-migration sessions may
/// still keep their mirror in a legacy pool while hooks probe all known dirs.
/// </summary>
internal static class SkipMarkers
{
    /// <summary>
    /// True when the transcript's colocated dir or any legacy mirror dir holds a
    /// skip marker for it. Keyed by the transcript's stem, so session and
    /// subagent files each carry their own marker.
    /// </summary>
    public static bool IsCompactionSkipped(string transcriptPath)
    {
        string stem = Path.GetFileNameWithoutExtension(transcriptPath);
        foreach (string dir in MirrorLocator.SearchDirectoriesFor(transcriptPath))
        {
            if (File.Exists(Path.Combine(dir, stem + ".skip")))
                return true;
        }
        return false;
    }

    public static void Write(string sessionId, string transcriptPath)
    {
        string content = MirrorFormat.Line("skipCompactionOf", Path.GetFullPath(transcriptPath));
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string mirror in MirrorLocator.FindSessionMirrors(sessionId))
            dirs.Add(Path.GetDirectoryName(mirror)!);
        string colocated = MirrorLocator.ClaudinineDirFor(transcriptPath);
        Directory.CreateDirectory(colocated);
        dirs.Add(colocated);
        foreach (string dir in dirs)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, sessionId + ".skip"),
                    content + "\n", new UTF8Encoding(false));
            }
            catch
            {
                // one unwritable dir must not block the others
            }
        }
    }

    public static void Remove(string sessionId, string transcriptPath)
    {
        foreach (string dir in MirrorLocator.SearchDirectoriesFor(transcriptPath))
        {
            try { File.Delete(Path.Combine(dir, sessionId + ".skip")); } catch { }
        }
    }

    /// <summary>
    /// The `.skip` half of garbage collection: reap markers whose transcript no
    /// longer exists. Called per-dir by <see cref="MirrorFile.CollectGarbage()"/>.
    /// </summary>
    internal static void CollectGarbage(string dir)
    {
        foreach (string marker in Directory.EnumerateFiles(dir, "*.skip"))
        {
            try
            {
                string? line = File.ReadLines(marker, Encoding.UTF8).FirstOrDefault();
                if (line is null) continue;
                if (JsonNode.Parse(line) is not JsonObject header) continue;
                string? target = header["claudinine"]?["skipCompactionOf"].GetString();
                if (target is not null && !File.Exists(target))
                    File.Delete(marker);
            }
            catch
            {
                // Unreadable marker: leave it for a human.
            }
        }
    }
}
