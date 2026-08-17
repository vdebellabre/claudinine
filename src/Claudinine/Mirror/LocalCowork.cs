namespace Claudinine.Mirror;

/// <summary>
/// Cowork "On your computer" (local mode) detection and layout. Local mode is
/// the one host where the hook's OS and the session's shell OS differ: hooks
/// and file tools run on the desktop HOST (win32 on Windows) while the only
/// shell is `mcp__workspace__bash` inside a Linux microVM that is usually not
/// even running (docs/cowork-compatibility.md B5–B7). Consequently no shell
/// command in a digest header can be trusted there; retrieval must go through
/// the model's HOST-side file tools (Read/Grep) instead.
///
/// Those tools are gated by a connected-folder allowlist that covers `outputs/`
/// (the agent's cwd) but NOT the app-internal `.claude/projects/` tree where
/// the colocated mirror lives (E6) — so local mode gets a second, readable
/// retrieval surface: plain per-ref files under `outputs/.claudinine/refs/`,
/// written by <see cref="RefsDump"/> from the mirror.
///
/// Detection is structural, from the transcript path alone: the session tree is
/// `…\local_&lt;uuid&gt;\.claude\projects\&lt;slug&gt;\&lt;sid&gt;.jsonl` with `outputs\` a
/// sibling of `.claude\` under the same `local_&lt;uuid&gt;` root. That probe is not
/// an inference about the shell — it is the precondition of the alternate
/// retrieval path itself: no `outputs/` dir, nothing to write into, so the
/// launcher path stays the right fallback. (The doc's B7 discriminator — the
/// shell tool's NAME — identifies the host from inside a transcript; this
/// helper answers the write path's question, "where can I put files the model
/// can read?", which only the layout can answer.)
/// </summary>
internal static class LocalCowork
{
    /// <summary>
    /// The per-ref dump directory for a transcript inside a Cowork local-mode
    /// session tree, or null when the transcript is not in one. Subagent
    /// transcripts (`…/&lt;sid&gt;/subagents/agent-*.jsonl`) resolve to the same
    /// directory as their session — refs are uuid-prefixed and therefore
    /// key-compatible across every mirror of the tree.
    /// </summary>
    public static string? RefsDirFor(string transcriptPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(transcriptPath));
            for (; dir is not null; dir = Path.GetDirectoryName(dir))
            {
                if (!Path.GetFileName(dir).StartsWith("local_", StringComparison.OrdinalIgnoreCase))
                    continue;
                string outputs = Path.Combine(dir, "outputs");
                if (Directory.Exists(outputs) && Directory.Exists(Path.Combine(dir, ".claude")))
                    return Path.Combine(outputs, ".claudinine", "refs");
            }
        }
        catch
        {
            // An unresolvable path is simply not the local layout.
        }
        return null;
    }

    /// <summary>
    /// The form digest headers embed: absolute, forward slashes only — same
    /// contract as <see cref="Launcher.HeaderPathFor"/> (the model's file tools
    /// accept `C:/…` on Windows, and forward slashes survive JSON and Markdown
    /// quoting unescaped).
    /// </summary>
    public static string? HeaderRefsDirFor(string transcriptPath) =>
        RefsDirFor(transcriptPath)?.Replace('\\', '/');
}
