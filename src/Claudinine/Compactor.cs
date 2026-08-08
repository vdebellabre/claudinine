namespace Claudinine;

/// <summary>
/// The one idempotent pass every hook runs: load → mirror-append → decide →
/// rewrite (validate + atomic swap). Stateless — the compacted transcript IS the
/// state, the mirror's content is the progress marker. Fail-closed at every step:
/// any anomaly means the transcript is left exactly as found.
/// </summary>
internal static class Compactor
{
    /// <summary>
    /// Mirror without compacting — the pass for sessions frozen by
    /// `restore-compaction-off`: the backup stays fresh (crash protection, and
    /// re-enabling later has everything), the transcript is never touched.
    /// </summary>
    public static void MirrorOnly(string transcriptPath)
    {
        var transcript = TranscriptFile.TryLoad(transcriptPath);
        if (transcript is null)
            return;
        bool mirrored = MirrorFile.TryAppendMissing(transcript);
        Dbg.Log($"compaction skipped (skip marker present); mirrorOk={mirrored}");
    }

    public static void Run(string transcriptPath)
    {
        var transcript = TranscriptFile.TryLoad(transcriptPath);
        if (transcript is null)
            return; // unreadable or unfamiliar shape: do nothing silently

        // Mirror-first invariant: nothing is ever stubbed that isn't already
        // durably mirrored. Append failure → skip compaction entirely.
        if (!MirrorFile.TryAppendMissing(transcript))
            return;

        foreach (var rule in RuleCatalog.All)
        {
            try
            {
                rule.Apply(transcript);
            }
            catch when (!Dbg.Enabled)
            {
                return; // a misbehaving rule poisons the pass, not the file
            }
        }

        bool ok = transcript.TryRewrite();
        if (Dbg.Enabled)
        {
            int replaced = transcript.Records.Count(r => r.Replacement is not null);
            int removed = transcript.Records.Count(r => r.Removed);
            Dbg.Log($"replaced={replaced} removed={removed} rewriteOk={ok}");
        }
    }
}
