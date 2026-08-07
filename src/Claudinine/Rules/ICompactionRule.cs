using Claudinine.Transcript;

namespace Claudinine.Rules;

/// <summary>
/// One entry in the decide-step catalog. A rule inspects the whole transcript
/// (read-scope is always the full file — supersession may retire earlier records)
/// and marks records for replacement via <see cref="TranscriptRecord.Replacement"/>.
/// Rules must be idempotent (re-running on their own output changes nothing) and
/// must never touch protected records, uuids, or the file's final record.
/// </summary>
internal interface ICompactionRule
{
    string Name { get; }
    void Apply(TranscriptFile transcript);
}

internal static class RuleCatalog
{
    /// <summary>v1 order per plan: dedup → archive/retrieval → chain-collapse.</summary>
    public static readonly ICompactionRule[] All = [new BashReadDedupRule(), new ReadToolDedupRule()];
}
