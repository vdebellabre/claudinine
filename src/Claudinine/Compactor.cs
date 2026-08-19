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
        // A frozen session still needs retrieval to work (its digests predate
        // the freeze), so the launcher — and in local mode the refs dump — stays
        // fresh here too.
        Launcher.EnsureCurrent(transcriptPath);
        if (LocalCowork.RefsDirFor(transcriptPath) is string refsDir)
            RefsDump.TryEnsureCurrent(transcriptPath, refsDir);
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

        // The retrieval launcher the digest headers point at, re-targeted at
        // the currently running binary on every pass so it can't go stale.
        Launcher.EnsureCurrent(transcriptPath);

        // Cowork local mode: headers promise plain files under outputs/, so the
        // dump is part of the mirror-first contract — no dump, no compaction
        // (docs/cowork-compatibility.md E6/E7: an unkeepable retrieval promise
        // silently degrades the model into inferring from previews).
        if (LocalCowork.RefsDirFor(transcriptPath) is string refsDir
            && !RefsDump.TryEnsureCurrent(transcriptPath, refsDir))
        {
            return;
        }

        foreach (var rule in RuleCatalog.All)
        {
            try
            {
                rule.Apply(transcript);
            }
            catch when (!Dbg.Active)
            {
                return; // a misbehaving rule poisons the pass, not the file
            }
        }

        bool ok = transcript.TryRewrite();
        if (Dbg.Active)
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
    /// emitter spells a get-command naming the &lt;full-id&gt; inside the record it
    /// stamps — `claudinine get &lt;id&gt;` (stubs, short headers, pre-launcher full
    /// headers) or `sh "…/run.sh" get &lt;id&gt;` (launcher-form full headers) — so
    /// own-sid text inside a marked record is exactly "this session promised
    /// its own mirror". BOTH forms must stay matched forever: transcripts
    /// compacted by 0.1.x/0.2.x carry the old phrasing and must keep tripping.
    /// </summary>
    private static bool MirrorLost(TranscriptFile transcript)
    {
        string sid = Path.GetFileNameWithoutExtension(transcript.Path);
        string ownPhrase = Protocol.BareGetCommand + sid;
        // The launcher form as it appears in the RAW jsonl line: the closing
        // quote of the launcher path is JSON-escaped there (`…run.sh\" get <id>`),
        // and RawLine is the raw line — matching the unescaped form would never hit.
        string ownLauncherPhrase = Protocol.LauncherGetFragmentJsonEscaped + sid;
        // Local-mode headers carry no get-command (their verbs are the model's
        // file tools); the sid rides in the block's `mirror key:` breadcrumb.
        string ownMirrorKeyPhrase = Protocol.MirrorKeyPrefix + sid;
        if (!transcript.Records.Any(r => r.View["claudinine"].Exists
            && (r.RawLine.Contains(ownPhrase, StringComparison.OrdinalIgnoreCase)
                || r.RawLine.Contains(ownLauncherPhrase, StringComparison.OrdinalIgnoreCase)
                || r.RawLine.Contains(ownMirrorKeyPhrase, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }
        if (MirrorLocator.ExistingMirrorsFor(transcript.Path).Count > 0)
            return false;
        // Local mode: the refs dump is a second, independent copy of every
        // archived payload, readable by the tools the headers actually teach.
        // With it intact, retrieval — the promise the stubs make — still holds;
        // stopping every future pass forever would protect nothing the dump
        // doesn't already serve. Restore fidelity is degraded (the mirror is
        // gone), which a fresh mirror rebuilds forward from here.
        if (LocalCowork.RefsDirFor(transcript.Path) is string refsDir
            && Directory.Exists(refsDir)
            && Directory.EnumerateFiles(refsDir, "*.txt").Any())
        {
            Dbg.Log($"mirror lost for {transcript.Path} but local refs dump intact; pass continues");
            return false;
        }
        Dbg.Log($"mirror lost for stubbed transcript {transcript.Path}; pass disabled");
        return true;
    }
}
