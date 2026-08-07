using Claudinine.Mirror;
using Claudinine.Rules;
using Claudinine.Transcript;

namespace Claudinine;

/// <summary>
/// The one idempotent pass every hook runs: load → mirror-append → decide →
/// rewrite (validate + atomic swap). Stateless — the compacted transcript IS the
/// state, the mirror's content is the progress marker. Fail-closed at every step:
/// any anomaly means the transcript is left exactly as found.
/// </summary>
internal static class Compactor
{
    public static void Run(string transcriptPath)
    {
        TranscriptFile? transcript = TranscriptFile.TryLoad(transcriptPath);
        if (transcript is null)
            return; // unreadable or unfamiliar shape: do nothing silently

        // Mirror-first invariant: nothing is ever stubbed that isn't already
        // durably mirrored. Append failure → skip compaction entirely.
        if (!MirrorFile.TryAppendMissing(transcript))
            return;

        foreach (ICompactionRule rule in RuleCatalog.All)
        {
            try
            {
                rule.Apply(transcript);
            }
            catch when (Environment.GetEnvironmentVariable("CLAUDININE_DEBUG") is null)
            {
                return; // a misbehaving rule poisons the pass, not the file
            }
        }

        bool ok = transcript.TryRewrite();
        if (Environment.GetEnvironmentVariable("CLAUDININE_DEBUG") is not null)
        {
            int replaced = transcript.Records.Count(r => r.Replacement is not null);
            int removed = transcript.Records.Count(r => r.Removed);
            Console.Error.WriteLine(
                $"[claudinine debug] replaced={replaced} removed={removed} rewriteOk={ok}");
        }
    }
}
