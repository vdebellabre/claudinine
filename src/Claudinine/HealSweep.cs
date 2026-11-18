using System.Diagnostics;

namespace Claudinine;

/// <summary>
/// One-off OrphanHeal over the OTHER sessions of the current project, run at
/// SessionStart. A session's own passes heal it, but only once it is used again
/// — and a resume loads the transcript BEFORE any hook runs, so that first
/// resume would still see the cut chain (2.1.327 shows "earlier messages can't
/// be loaded"; earlier CLIs drop them silently). The sweep repairs dormant
/// sessions ahead of their next resume.
/// <para>
/// Heal only: no mirror append, no rules, and never a session frozen by
/// `restore-compaction-off`. A transcript with no colocated
/// mirror was never compacted here (or not since the 2026-08-15 colocation),
/// so it holds no orphans of ours. Each healed or clean transcript gets a
/// `&lt;sid&gt;.healed` marker in its claudinine dir and is never swept again;
/// new orphans can no longer form behind the live window, and any that do are
/// healed by that session's own passes. A transcript busy under another hook's
/// PassLock, or one that grew mid-heal (a live session), gets no marker and is
/// retried at the next SessionStart.
/// </para>
/// Time-boxed, since SessionStart has a hook timeout and a project can hold
/// hundreds of sessions: the markers make the sweep resumable, so a large
/// backlog drains across a few session starts.
/// </summary>
internal static class HealSweep
{
    private const string MarkerExtension = ".healed";
    private const string MarkerVersion = "v1";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public static void Run(string transcriptPath) => Run(transcriptPath, Budget);

    internal static void Run(string transcriptPath, TimeSpan budget)
    {
        try
        {
            string full = Path.GetFullPath(transcriptPath);
            string projectDir = Path.GetDirectoryName(full)!;
            string ownStem = Path.GetFileNameWithoutExtension(full);
            var clock = Stopwatch.StartNew();
            foreach (string file in Directory.EnumerateFiles(projectDir, "*.jsonl"))
            {
                if (clock.Elapsed > budget)
                    break;
                if (string.Equals(Path.GetFileNameWithoutExtension(file), ownStem,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue; // healed by this session's own passes
                }
                try
                {
                    SweepOne(file);
                }
                catch (Exception e)
                {
                    Dbg.Log($"heal sweep failed on {file}: {e.Message}");
                    // next transcript
                }
            }
        }
        catch (Exception e)
        {
            Dbg.Log($"heal sweep failed: {e.Message}");
        }
    }

    internal static string MarkerPathFor(string transcriptPath) =>
        Path.Combine(
            MirrorLocator.ClaudinineDirFor(transcriptPath),
            Path.GetFileNameWithoutExtension(transcriptPath) + MarkerExtension);

    private static void SweepOne(string file)
    {
        string marker = MarkerPathFor(file);
        if (File.Exists(marker))
            return;
        string mirrorPath = MirrorLocator.PathFor(file);
        if (!File.Exists(mirrorPath))
            return;
        // Frozen by restore-compaction-off: the file is never rewritten, same
        // as its own passes (Compactor.MirrorOnly). The restore rebuilt it from
        // the mirror, so it carries no orphans of ours anyway.
        if (SkipMarkers.IsCompactionSkipped(file))
            return;

        using (var held = PassLock.TryAcquire(file))
        {
            if (held is null)
                return;
            var transcript = TranscriptFile.TryLoad(file);
            // Unloadable: our passes refuse it too, so nothing of ours to heal.
            if (transcript is not null
                && OrphanHeal.Prepare(transcript, mirrorPath) > 0
                && !transcript.TryRewrite())
            {
                return; // refused or raced a live append: retry next SessionStart
            }
        }
        File.WriteAllText(marker, MarkerVersion + "\n", new UTF8Encoding(false));
    }
}
