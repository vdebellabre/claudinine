namespace Claudinine.Mirror;

/// <summary>
/// Where mirrors live and how a session's are found. The canonical location is
/// COLOCATED with the transcript, inside the session's sidecar directory:
/// `&lt;project&gt;/&lt;sid&gt;/claudinine/&lt;stem&gt;.jsonl` — the same directory that holds
/// `subagents/`, `tool-results/` and `workflows/`. Anything that snapshots,
/// syncs, backs up or deletes the session necessarily carries the mirror with
/// it, which is the whole durability story: mirror lifetime == transcript
/// lifetime, by placement rather than by machinery.
///
/// The flat pools of 0.1.x (`$CLAUDE_PLUGIN_DATA/mirrors`,
/// `~/.claude/plugins/data/claudinine-*/mirrors`, `~/.claudinine/mirrors`) are
/// LEGACY: still probed by every read, never written to. Steady-state passes
/// migrate a legacy mirror to the colocated path the first time they touch its
/// session (see <see cref="MirrorFile.TryAppendMissing"/>).
/// </summary>
internal static class MirrorLocator
{
    /// <summary>
    /// The colocated claudinine directory for a transcript. A session transcript
    /// `&lt;project&gt;/&lt;sid&gt;.jsonl` maps into its own sidecar dir
    /// (`&lt;project&gt;/&lt;sid&gt;/claudinine`); a subagent transcript
    /// `&lt;project&gt;/&lt;sid&gt;/subagents/agent-*.jsonl` maps into its SESSION's dir,
    /// so one directory carries the session mirror, every agent mirror and all
    /// their sidecars.
    /// </summary>
    public static string ClaudinineDirFor(string transcriptPath)
    {
        string full = Path.GetFullPath(transcriptPath);
        string dir = Path.GetDirectoryName(full)!;
        if (string.Equals(Path.GetFileName(dir), "subagents", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Path.GetDirectoryName(dir)!, "claudinine");
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(full), "claudinine");
    }

    /// <summary>The canonical (colocated) mirror path — the only path writes target.</summary>
    public static string PathFor(string transcriptPath) =>
        Path.Combine(
            ClaudinineDirFor(transcriptPath),
            Path.GetFileNameWithoutExtension(transcriptPath) + ".jsonl");

    /// <summary>
    /// The project directory a transcript's sessions live in — the dir holding
    /// `&lt;sid&gt;.jsonl` files, whether the transcript is a session or one of its
    /// subagent files. Fork parents are sibling sessions in this directory.
    /// </summary>
    internal static string ProjectDirFor(string transcriptPath)
    {
        string full = Path.GetFullPath(transcriptPath);
        string dir = Path.GetDirectoryName(full)!;
        if (string.Equals(Path.GetFileName(dir), "subagents", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(Path.GetDirectoryName(dir)!)!;
        return dir;
    }

    /// <summary>
    /// Legacy flat-pool directories, probed on READS only. `get` typically runs
    /// from the session's Bash tool, which does NOT inherit CLAUDE_PLUGIN_DATA
    /// (verified live 2026-08-08), and the app hands a different data dir to each
    /// install context (claudinine-inline for desktop, claudinine-&lt;marketplace&gt;
    /// for CLI), so a read must look everywhere pre-colocation mirrors landed.
    /// </summary>
    public static IReadOnlyList<string> SearchDirectories() =>
        SearchDirectories(
            Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static IReadOnlyList<string> SearchDirectories(string? pluginData, string home)
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string dir)
        {
            if (Directory.Exists(dir) && seen.Add(Path.GetFullPath(dir)))
                dirs.Add(dir);
        }

        if (!string.IsNullOrEmpty(pluginData))
            Add(Path.Combine(pluginData, "mirrors"));
        string dataRoot = Path.Combine(home, ".claude", "plugins", "data");
        if (Directory.Exists(dataRoot))
        {
            foreach (string plugin in Directory.EnumerateDirectories(dataRoot, "claudinine-*"))
                Add(Path.Combine(plugin, "mirrors"));
        }
        Add(Path.Combine(home, ".claudinine", "mirrors"));
        return dirs;
    }

    /// <summary>
    /// Directories to probe when reading state for a KNOWN transcript: the
    /// colocated dir first (canonical), then the legacy pools.
    /// </summary>
    public static IReadOnlyList<string> SearchDirectoriesFor(string transcriptPath)
    {
        var dirs = new List<string> { ClaudinineDirFor(transcriptPath) };
        dirs.AddRange(SearchDirectories());
        return dirs;
    }

    /// <summary>
    /// Every mirror file that exists for this transcript — colocated first, then
    /// legacy stem matches. Empty means "no mirror anywhere", the condition the
    /// compaction tripwire fails closed on.
    /// </summary>
    public static List<string> ExistingMirrorsFor(string transcriptPath)
    {
        var found = new List<string>();
        string stem = Path.GetFileNameWithoutExtension(transcriptPath);
        foreach (string dir in SearchDirectoriesFor(transcriptPath))
        {
            string candidate = Path.Combine(dir, stem + ".jsonl");
            if (File.Exists(candidate))
                found.Add(candidate);
        }
        return found;
    }

    /// <summary>
    /// Colocated claudinine dirs across every project under this home — the
    /// verb-time complement to the legacy pools for sid-only resolution.
    /// Enumeration-heavy (every session dir of every project), so this is for
    /// user commands, never for the hook hot path.
    /// </summary>
    internal static List<string> ColocatedDirectories(string home)
    {
        var dirs = new List<string>();
        try
        {
            string projects = Path.Combine(home, ".claude", "projects");
            if (!Directory.Exists(projects))
                return dirs;
            foreach (string slug in Directory.EnumerateDirectories(projects))
            {
                foreach (string sessionDir in Directory.EnumerateDirectories(slug))
                {
                    string candidate = Path.Combine(sessionDir, "claudinine");
                    if (Directory.Exists(candidate))
                        dirs.Add(candidate);
                }
            }
        }
        catch
        {
            // A denied project dir must not kill sid resolution for the rest.
        }
        return dirs;
    }

    /// <summary>
    /// Match a session's mirror files by id prefix across every known location —
    /// colocated dirs of all projects plus the legacy pools. The same session may
    /// still have a mirror in several places (pre-migration legacy copies) — all
    /// of them are returned; a prefix that resolves to more than one distinct
    /// session id matches nothing.
    /// </summary>
    public static List<string> FindSessionMirrors(string session) =>
        FindSessionMirrors(
            session,
            Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static List<string> FindSessionMirrors(string session, string? pluginData, string home)
    {
        var dirs = new List<string>();
        dirs.AddRange(ColocatedDirectories(home));
        dirs.AddRange(SearchDirectories(pluginData, home));
        return SessionResolver.ResolveByIdOrUniquePrefix(dirs, session);
    }

    /// <summary>
    /// A parent session's mirror files, never our own. The parent is a sibling
    /// session in the same project dir (forks never cross projects), so its
    /// colocated mirror is at a deterministic path; legacy pools are probed for
    /// pre-migration parents.
    /// </summary>
    internal static List<string> ParentMirrorFiles(
        string sessionId, string ownMirrorPath, string transcriptPath)
    {
        var sources = new List<string>();
        var candidates = new List<string>
        {
            Path.Combine(ProjectDirFor(transcriptPath), sessionId, "claudinine", sessionId + ".jsonl"),
        };
        foreach (string dir in SearchDirectories())
            candidates.Add(Path.Combine(dir, sessionId + ".jsonl"));
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)
                && !string.Equals(Path.GetFullPath(candidate),
                    Path.GetFullPath(ownMirrorPath), StringComparison.OrdinalIgnoreCase))
            {
                sources.Add(candidate);
            }
        }
        return sources;
    }
}
