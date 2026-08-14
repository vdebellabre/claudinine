namespace Claudinine.Rules;

/// <summary>
/// Keep-last for task_reminder attachments. Each one carries a FULL snapshot of
/// the session's task list (content = every item with subject/description/status,
/// itemCount), so a later reminder strictly supersedes any earlier one — file
/// order is time order, and a stale list presented as current state is
/// misinformation (census 2026-08-07: 2.6MB / 2363 records across the corpus,
/// 72% of them empty zero-state nudges; keep-last removes 97% of the bytes). No
/// per-key scoping: the list is session-global, the whole snapshot is the unit.
/// Corpus hazard check: "last is empty but earlier had items" never occurs —
/// empty reminders come in runs before a list exists. Only main-chain records
/// participate, as candidates AND superseders (all 2363 corpus records are
/// main-chain; a subagent chain, should one ever carry reminders, is a separate
/// conversation and must not be superseded across). Records sit ON the uuid
/// chain, so removal leans on the rewrite layer's rechaining; the last reminder
/// is never removed, so the tail is safe by construction.
/// </summary>
internal sealed class TaskReminderKeepLastRule : ICompactionRule
{
    public string Name => "task-reminder-keep-last";

    public void Apply(TranscriptFile transcript) =>
        RuleHelpers.RemoveAllButLast(transcript.Records, IsMainChainReminder);

    private static bool IsMainChainReminder(TranscriptRecord rec) =>
        rec.Type == "attachment"
        && rec.Node["attachment"] is JsonObject att
        && att["type"] is JsonValue v && v.TryGetValue(out string? t)
        && t == "task_reminder"
        && !rec.IsSidechain;
}
