namespace Claudinine;

/// <summary>
/// Collects orphaned session directories: &lt;uuid&gt;/ sidecar dirs (subagents/,
/// tool-results/, workflows/, loose plan notes) whose sibling &lt;uuid&gt;.jsonl
/// transcript is gone. Claude Code ages transcripts out but never deletes these,
/// and no transcript references another session's sidecars (verified corpus-wide
/// 2026-08-08), so transcript-gone means unreachable. Scoped to the current
/// project directory; runs at SessionStart, off the per-prompt critical path.
/// UI-deleted sessions keep their transcript on disk, so their dirs survive —
/// same accepted caveat as mirror GC.
/// </summary>
internal static class SessionDirGc
{
    /// <summary>
    /// Never delete anything touched recently: dodges races with a concurrently
    /// starting session whose files are not all on disk yet.
    /// </summary>
    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    public static void Run(string transcriptPath, string? currentSessionId)
    {
        try
        {
            string? projectDir = Path.GetDirectoryName(Path.GetFullPath(transcriptPath));
            if (projectDir is null)
                return;

            foreach (string dir in Directory.EnumerateDirectories(projectDir))
            {
                string name = Path.GetFileName(dir);
                if (!IsSessionUuid(name))
                    continue; // memory/, memory.backup-*, anything not session-shaped
                if (string.Equals(name, currentSessionId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(Path.Combine(projectDir, name + ".jsonl")))
                    continue; // transcript alive — not an orphan

                TryDelete(dir, name);
            }
        }
        catch
        {
            // Best-effort housekeeping; never disturb the session it runs in.
        }
    }

    private static void TryDelete(string dir, string name)
    {
        try
        {
            if (DateTime.UtcNow - NewestTimestampUtc(dir) < Grace)
            {
                Dbg.Log($"gc: kept session dir {name} (inside grace window)");
                return;
            }
            Directory.Delete(dir, recursive: true);
            Dbg.Log($"gc: deleted orphan session dir {name}");
        }
        catch
        {
            // Locked or half-gone dir: leave it for a later pass.
        }
    }

    /// <summary>
    /// Newest creation/write timestamp across the dir and its whole tree — the
    /// most conservative "last touched" for the grace check (a session dir's own
    /// mtime does not change when files land in its subdirectories).
    /// </summary>
    private static DateTime NewestTimestampUtc(string dir)
    {
        var newest = Directory.GetCreationTimeUtc(dir);
        var w = Directory.GetLastWriteTimeUtc(dir);
        if (w > newest) newest = w;
        foreach (string entry in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
        {
            // File.Get* also works on directories.
            w = File.GetLastWriteTimeUtc(entry);
            if (w > newest) newest = w;
            w = File.GetCreationTimeUtc(entry);
            if (w > newest) newest = w;
        }
        return newest;
    }

    /// <summary>
    /// Strict session-id shape: lowercase hex 8-4-4-4-12. The project dir also
    /// holds user data (memory/, memory.backup-*) — a deleter must match exactly
    /// what the app names session dirs, nothing looser.
    /// </summary>
    private static bool IsSessionUuid(string name)
    {
        if (name.Length != 36)
            return false;
        for (int i = 0; i < 36; i++)
        {
            char c = name[i];
            if (i is 8 or 13 or 18 or 23)
            {
                if (c != '-')
                    return false;
            }
            else if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }
}
