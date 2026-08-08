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
    /// <summary>
    /// Rollout order follows cozempic's tier ordering (gentle-equivalents first):
    /// supersession/dedup rules run before age-based ones so a deduped result gets
    /// the more informative stub, and the mega-block safety net runs last.
    /// </summary>
    public static readonly ICompactionRule[] All =
    [
        // First: after a desktop fork, merge the parent's mirror and retarget
        // digest refs BEFORE any rule reads or rewrites those digests, so e.g.
        // carrier-header-dedup parses the healed session id, not the stale one.
        new ForkHealRule(),
        new BashReadDedupRule(),
        new ReadToolDedupRule(),
        new SystemReminderDedupRule(),
        new DocumentDedupRule(),
        // Chain-collapse runs before the age tiers so digest previews render from
        // (mostly) original content; whatever it leaves behind ages normally.
        new ChainCollapseRule(),
        // Immediately after chain-collapse: slims the retrieval boilerplate of
        // every carrier but the file's first, including carriers born this pass,
        // then retires the anchor tool_use's dead-weight input.
        new CarrierHeaderDedupRule(),
        new AnchorInputStubRule(),
        new ToolResultAgeRule(),
        new MegaBlockTrimRule(),
        new ImageStripRule(),
        // Record-removal housekeeping (whole inert records, no stubs) — touches
        // nothing the content rules above read, so order-independent of them.
        new MetadataKeepLastRule(),
        new QueueHistoryCollapseRule(),
        new StopHookSummaryStripRule(),
        new EditedTextFileSupersessionRule(),
        new TaskReminderKeepLastRule(),
        new HookSuccessStripRule(),
    ];
}
