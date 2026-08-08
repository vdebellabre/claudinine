namespace Claudinine.Mirror;

/// <summary>
/// Where mirrors live and how a session's are found. Writes always target
/// <see cref="MirrorsDirectory"/>; reads probe <see cref="SearchDirectories()"/> —
/// see that method for why the two differ.
/// </summary>
internal static class MirrorLocator
{
    public static string MirrorsDirectory()
    {
        string root = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA")
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claudinine");
        return System.IO.Path.Combine(root, "mirrors");
    }

    public static string PathFor(string transcriptPath) =>
        System.IO.Path.Combine(
            MirrorsDirectory(),
            System.IO.Path.GetFileNameWithoutExtension(transcriptPath) + ".jsonl");

    /// <summary>
    /// Directories to probe when resolving a mirror for READING. Writes always use
    /// MirrorsDirectory() — hooks have CLAUDE_PLUGIN_DATA set. But `get` typically
    /// runs from the session's Bash tool, which does NOT inherit that variable
    /// (verified live 2026-08-08), and the app hands a different data dir to each
    /// install context (claudinine-inline for desktop, claudinine-&lt;marketplace&gt;
    /// for CLI), so a read must look everywhere mirrors are known to land.
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
            if (Directory.Exists(dir) && seen.Add(System.IO.Path.GetFullPath(dir)))
                dirs.Add(dir);
        }

        if (!string.IsNullOrEmpty(pluginData))
            Add(System.IO.Path.Combine(pluginData, "mirrors"));
        string dataRoot = System.IO.Path.Combine(home, ".claude", "plugins", "data");
        if (Directory.Exists(dataRoot))
        {
            foreach (string plugin in Directory.EnumerateDirectories(dataRoot, "claudinine-*"))
                Add(System.IO.Path.Combine(plugin, "mirrors"));
        }
        Add(System.IO.Path.Combine(home, ".claudinine", "mirrors"));
        return dirs;
    }

    /// <summary>
    /// Match a session's mirror files by id prefix across every known mirror
    /// directory. The same session may have a mirror in several dirs
    /// (cross-context resume) — all of them are returned; a prefix that resolves
    /// to more than one distinct session id matches nothing.
    /// </summary>
    public static List<string> FindSessionMirrors(string session) =>
        SessionResolver.ResolveByIdOrUniquePrefix(SearchDirectories(), session);

    /// <summary>A session's mirror files across all known dirs, never our own.</summary>
    internal static List<string> ParentMirrorFiles(string sessionId, string ownMirrorPath)
    {
        var sources = new List<string>();
        foreach (string dir in SearchDirectories())
        {
            string candidate = System.IO.Path.Combine(dir, sessionId + ".jsonl");
            if (File.Exists(candidate)
                && !string.Equals(System.IO.Path.GetFullPath(candidate),
                    System.IO.Path.GetFullPath(ownMirrorPath), StringComparison.OrdinalIgnoreCase))
            {
                sources.Add(candidate);
            }
        }
        return sources;
    }
}
