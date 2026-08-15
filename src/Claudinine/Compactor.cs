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
        if (MirrorLost(transcript))
            return;
        bool mirrored = MirrorFile.TryAppendMissing(transcript);
        Dbg.Log($"compaction skipped (skip marker present); mirrorOk={mirrored}");
    }

    public static void Run(string transcriptPath)
    {
        var transcript = TranscriptFile.TryLoad(transcriptPath);
        if (transcript is null)
            return; // unreadable or unfamiliar shape: do nothing silently

        if (MirrorLost(transcript))
            return;

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

    /// <summary>
    /// Fail-closed tripwire: the transcript carries stubs whose retrieval
    /// commands point at THIS session's mirror, and NO mirror exists anywhere —
    /// it was lost (a snapshot/restore that missed the claudinine dir, a partial
    /// sync, manual deletion). The stubbed originals are unrecoverable; what is
    /// left to protect is the rest of the transcript and the evidence. So the
    /// whole pass stops: compacting would strand more content behind refs that
    /// resolve to nothing, and even the mirror append is skipped — a fresh
    /// mirror would blind this very check on the next pass while the loss
    /// stayed real.
    ///
    /// The own-sid condition is what keeps forks alive: a fork's transcript
    /// carries the PARENT's stubs (their get-commands still name the parent id)
    /// and legitimately has no mirror of its own yet — the pass must run so
    /// ForkHealRule can adopt the parent mirror and retarget those refs. Every
    /// emitter spells `claudinine get &lt;full-id&gt;` inside the record it stamps,
    /// so own-sid text inside a marked record is exactly "this session promised
    /// its own mirror".
    /// </summary>
    private static bool MirrorLost(TranscriptFile transcript)
    {
        string ownPhrase = "claudinine get "
            + Path.GetFileNameWithoutExtension(transcript.Path);
        if (!transcript.Records.Any(r => r.View["claudinine"].Exists
            && r.RawLine.Contains(ownPhrase, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (MirrorLocator.ExistingMirrorsFor(transcript.Path).Count > 0)
            return false;
        Dbg.Log($"mirror lost for stubbed transcript {transcript.Path}; pass disabled");
        return true;
    }
}
