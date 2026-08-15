namespace Claudinine.Mirror;

/// <summary>
/// `&lt;stem&gt;.end` marker in the colocated claudinine dir: written at SessionEnd,
/// consumed at the next start boundary. Exists because some hosts (Cowork
/// cloud, measured 2026-08-15) tear a session down on idle — SessionEnd fires —
/// and later re-hydrate it from the transcript into a NEW process without ever
/// firing SessionStart. Without the marker, the once-per-session work (load
/// stamp, subagent sweep, housekeeping) runs exactly once in a session's whole
/// life while re-hydrations happen repeatedly. A UserPromptSubmit that finds
/// the marker is the first prompt after a teardown, so the hook treats it as
/// the session-start boundary; a real SessionStart consumes the marker too, so
/// a CLI resume never replays the start work on its first prompt.
///
/// Colocated only, never in the legacy pools: the marker is ephemeral host
/// state, not user intent, and it dies with the session dir under SessionDirGc.
/// Fail-safe on both sides: an unwritten or unconsumable marker only costs the
/// wake detection, never the session. A crash teardown that skips SessionEnd
/// stays uncovered — same as today, where SessionStart is the repair path.
/// </summary>
internal static class EndMarker
{
    private static string PathFor(string transcriptPath) =>
        Path.Combine(
            MirrorLocator.ClaudinineDirFor(transcriptPath),
            Path.GetFileNameWithoutExtension(transcriptPath) + ".end");

    public static void Write(string transcriptPath)
    {
        try
        {
            string path = PathFor(transcriptPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                MirrorFormat.Line("endMarkerOf", Path.GetFullPath(transcriptPath)) + "\n",
                new UTF8Encoding(false));
        }
        catch
        {
            // No marker means the next silent re-hydration goes undetected;
            // the wake work is repair, never correctness.
        }
    }

    /// <summary>
    /// True when a marker existed and was deleted — this event is the first
    /// boundary since a teardown. False when there is nothing to consume, or
    /// the delete failed: a marker we cannot remove must not turn every
    /// subsequent prompt into a start boundary.
    /// </summary>
    public static bool Consume(string transcriptPath)
    {
        try
        {
            string path = PathFor(transcriptPath);
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
