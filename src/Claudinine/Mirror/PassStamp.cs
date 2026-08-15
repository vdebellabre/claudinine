namespace Claudinine.Mirror;

/// <summary>
/// `&lt;stem&gt;.pass` in the colocated claudinine dir: its mtime is "when did a
/// compaction pass last complete over this transcript". Touched by every
/// completed pass, read only by the Stop trigger's min-interval guard — Stop
/// fires at every turn end, and autonomous stretches (scheduled tasks, /loop,
/// Workflow runs) can chain dozens of turns with no UserPromptSubmit between
/// them, so Stop is what keeps those compacting at all. In an interactive
/// session UserPromptSubmit already compacts every turn, and the stamp it
/// leaves is what stops Stop from doubling that work.
///
/// Pure mtime signal: the content (a standard format header) is never parsed.
/// Colocated only, and it dies with the session dir under SessionDirGc.
/// Fail-safe both ways: an unwritable stamp only makes Stop passes more
/// frequent, an unreadable one costs a single redundant pass.
/// </summary>
internal static class PassStamp
{
    private static string PathFor(string transcriptPath) =>
        Path.Combine(
            MirrorLocator.ClaudinineDirFor(transcriptPath),
            Path.GetFileNameWithoutExtension(transcriptPath) + ".pass");

    public static void Touch(string transcriptPath)
    {
        try
        {
            string path = PathFor(transcriptPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                MirrorFormat.Line("passStampOf", Path.GetFullPath(transcriptPath)) + "\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // No stamp means Stop treats every turn as due — more passes, all
            // idempotent, never a broken session.
        }
    }

    /// <summary>
    /// True when a pass completed less than <paramref name="interval"/> ago.
    /// A missing stamp reads as never-passed, so the guard opens.
    /// </summary>
    public static bool IsFresh(string transcriptPath, TimeSpan interval)
    {
        try
        {
            // GetLastWriteTimeUtc returns 1601-01-01 for a missing file — never
            // fresh, which is exactly the wanted default.
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(PathFor(transcriptPath)) < interval;
        }
        catch
        {
            return false;
        }
    }
}
