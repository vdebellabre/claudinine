namespace Claudinine.Rules;

/// <summary>
/// Shared engine for "a later read of the same range makes the earlier result
/// redundant" rules. Subclasses only say which tool_use blocks are reads and what
/// file ranges they provably return. The engine finds superseded reads (every
/// target covered by some later NON-ERRORED read — a read whose result carries
/// is_error returned no content and covers nothing), keeps a recency window
/// untouched, and stubs the matching tool_result payloads — original always
/// mirrored first by the pipeline.
/// </summary>
internal abstract class ReadSupersessionRule : ICompactionRule
{
    public abstract string Name { get; }

    /// <summary>Tool name(s) whose tool_use blocks this rule inspects.</summary>
    protected internal abstract bool IsReadTool(string toolName);

    /// <summary>Ranges this call provably returns; empty = not a pure read, don't touch.</summary>
    protected internal abstract List<ReadTarget> ExtractTargets(JsonView toolUseBlock);

    /// <summary>Results smaller than this aren't worth a stub (the stub itself costs bytes).</summary>
    private const int MinResultChars = 400;

    /// <summary>Never touch this many of the session's most recent reads — the tail is live context.</summary>
    private const int RecencyKeep = 6;

    public void Apply(TranscriptFile transcript)
    {
        // Pass 1: every eligible read, in file order — plus the ids of reads whose
        // result errored. A failed read returned no file content, so it may be
        // superseded but can never supersede. Error results are collected from
        // protected records too: the tool_result can sit on a protected carrier
        // while its tool_use record is not.
        var reads = new List<(string ToolUseId, List<ReadTarget> Targets)>();
        var errored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rec in transcript.Records)
        {
            foreach (var b in RuleHelpers.BlocksOfType(rec.CurrentView, "tool_result"))
            {
                if (b["is_error"].IsTrue && b["tool_use_id"].AsString() is string errId)
                    errored.Add(errId);
            }
            if (rec.IsProtected())
                continue;
            foreach (var b in RuleHelpers.BlocksOfType(rec.CurrentView, "tool_use"))
            {
                if (b["name"].AsString() is not string name || !IsReadTool(name))
                    continue;
                var targets = ExtractTargets(b);
                if (targets.Count == 0)
                    continue;
                if (b["id"].AsString() is string toolUseId && toolUseId.Length > 0)
                    reads.Add((toolUseId, targets));
            }
        }
        if (reads.Count < 2)
            return;

        // Pass 2: a read is superseded when some LATER read covers every target.
        // Never the most recent read of a range, never the recency window.
        // Walked backwards, accumulating later reads' targets per path, so each
        // read checks only its own paths' candidates instead of rescanning every
        // later read (that rescan was quadratic in the session's read count).
        var superseded = new Dictionary<string, List<ReadTarget>>();
        int cutoff = reads.Count - RecencyKeep;
        var laterByPath = new Dictionary<string, List<ReadTarget>>(StringComparer.Ordinal);
        for (int i = reads.Count - 1; i >= 0; i--)
        {
            (string toolUseId, var targets) = reads[i];
            if (i < cutoff)
            {
                bool allCovered = targets.All(t =>
                    laterByPath.TryGetValue(t.Path, out var laters)
                    && laters.Any(lt => lt.Covers(t)));
                if (allCovered)
                    superseded[toolUseId] = targets;
            }
            if (errored.Contains(toolUseId))
                continue;
            foreach (var t in targets)
            {
                if (!laterByPath.TryGetValue(t.Path, out var bucket))
                    laterByPath[t.Path] = bucket = [];
                bucket.Add(t);
            }
        }
        if (superseded.Count == 0)
            return;

        // Pass 3: stub the matching tool_result payloads. Idempotence comes from
        // MinResultChars: a stub is short, so a second pass skips it naturally.
        foreach (var rec in transcript.Records)
        {
            if (rec.IsProtected())
                continue;

            JsonObject? clone = null;
            foreach (var b in RuleHelpers.BlocksOfType(rec.CurrentView, "tool_result"))
            {
                if (b["tool_use_id"].AsString() is not string toolUseId
                    || !superseded.TryGetValue(toolUseId, out var targets))
                {
                    continue;
                }

                string current = RuleHelpers.ResultText(b);
                if (current.Length < MinResultChars)
                    continue;
                // The carrier may no longer hold the read's output at all: a
                // chain-collapse digest reuses the anchor call's tool_use_id, and
                // its content is long — MinResultChars alone won't skip it.
                if (RuleHelpers.IsClaudinineStub(current))
                    continue;

                // First hit on this record: clone it, then mutate the clone's
                // corresponding block (never the original parse).
                clone ??= rec.CloneCurrentNode();
                foreach (var cb in RuleHelpers.ContentBlocks(clone))
                {
                    if (cb is JsonObject cbo && cbo["tool_use_id"].GetString() == toolUseId)
                    {
                        string desc = string.Join(", ", targets);
                        cbo["content"] = $"[claudinine: file read superseded by a later read of {desc}]";
                    }
                }
            }
            if (clone is not null)
                RuleHelpers.SetReplacement(rec, clone, Name);
        }
    }
}
