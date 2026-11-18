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
/// `restore-compaction-off`. A transcript with no colocated mirror was never
/// compacted here (or not since the 2026-08-15 colocation), so it holds no
/// orphans of ours.
/// </para>
/// <para>
/// DORMANT sessions only: a transcript written to in the last
/// <see cref="QuietFor"/> is skipped. Another session of the project may be
/// streaming a turn in another window, and its PassLock does not stop the CLI
/// from appending: a rewrite racing that append would drop the record — and
/// since this sweep does not mirror first, the dropped uuid would be in neither
/// file, a cut no heal could ever repair. A recently active session is healed
/// by its own passes anyway. (The swap also re-checks the length right before
/// the move; the quiet gate is what keeps the race from being routine.)
/// </para>
/// <para>
/// Each transcript that loaded and healed cleanly (or needed nothing) gets a
/// `&lt;sid&gt;.healed` marker in its claudinine dir and is not swept again —
/// the live window keeps the known shapes from recurring, and the session's own
/// passes heal anything new. Anything inconclusive gets no marker and is
/// retried at a later SessionStart: busy under another hook's PassLock, a
/// transcript that would not load or a mirror that would not read (both can be
/// transient), or a rewrite refused or raced.
/// </para>
/// Real SessionStart only, never a wake on the prompt path, and time-boxed
/// (checked between files): the markers make the sweep resumable, so a large
/// backlog drains across a few session starts.
/// </summary>
internal static class HealSweep
{
    private const string MarkerExtension = ".healed";
    private const string MarkerVersion = "v1";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(10);

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
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < QuietFor)
            return; // possibly live in another window: its own passes heal it
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
            if (transcript is null)
                return; // possibly transient (sharing violation): retry later
            int healed = OrphanHeal.Prepare(transcript, mirrorPath);
            if (healed < 0)
                return; // mirror unreadable: retry later
            if (healed > 0 && !transcript.TryRewrite())
                return; // refused or raced a live append: retry later
        }
        File.WriteAllText(marker, MarkerVersion + "\n", new UTF8Encoding(false));
    }
}
