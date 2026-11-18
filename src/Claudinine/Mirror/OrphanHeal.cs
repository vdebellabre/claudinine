namespace Claudinine.Mirror;

/// <summary>
/// Repairs chain links the live process broke AFTER one of our passes: a record
/// whose <c>parentUuid</c> (or <c>leafUuid</c>) names a record we removed. The
/// rewrite layer rechains children of what it removes, but a child written later
/// — the model's reply landing seconds after a mid-turn pass collapsed its
/// tool_result, the next prompt parented to a stripped stop_hook_summary — was
/// never seen by that pass, and the resume loader cuts the chain at it. The live
/// window (TranscriptFile.MarkLiveWindow) keeps new ones from forming; this
/// heals the ones that already exist, and is the net for any race the window
/// doesn't anticipate.
/// <para>
/// The mirror is the proof of authorship and the map back: every record is
/// mirrored, with its ORIGINAL parent, before any pass can remove it. A missing
/// uuid found in the mirror was removed by us, and walking the mirror's chain
/// up to the first uuid still in the transcript gives exactly the ancestor
/// SurvivingAncestor would have picked had the child existed at the time. A
/// missing uuid NOT in the mirror is someone else's (fork copies, crash
/// leftovers) and stays as found — the same "only fix what we break" policy.
/// </para>
/// Prepare only computes <see cref="TranscriptFile.HealedParents"/>; the rewrite
/// applies it under the usual validation and atomic swap.
/// </summary>
internal static class OrphanHeal
{
    /// <summary>
    /// Fill <see cref="TranscriptFile.HealedParents"/> for the transcript's
    /// dangling references that the mirror can resolve. <paramref name="mirrored"/>
    /// (the mirror's <c>u:&lt;uuid&gt;</c> identity set, when the caller has it)
    /// gates the mirror read: without one of OUR uuids dangling, the mirror is
    /// never opened. Returns the number of references that will be healed.
    /// Fail-closed: any read problem leaves the transcript's references as found.
    /// </summary>
    public static int Prepare(TranscriptFile transcript, string mirrorPath,
        IReadOnlySet<string>? mirrored = null)
    {
        try
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rec in transcript.Records)
            {
                if (rec.Uuid is not null)
                    present.Add(rec.Uuid);
            }

            var dangling = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rec in transcript.Records)
            {
                AddIfDangling(rec.ParentUuid);
                AddIfDangling(rec.View["leafUuid"].AsString());
            }
            if (dangling.Count == 0 || !File.Exists(mirrorPath))
                return 0;

            var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach ((string _, var node) in Jsonl.ReadRecords(mirrorPath, skipFirst: true))
            {
                if (node?["uuid"].GetString() is string uuid)
                    parentOf.TryAdd(uuid, node["parentUuid"].GetString());
            }

            var healed = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (string missing in dangling)
            {
                if (TryResolve(missing, present, parentOf, out string? ancestor))
                    healed[missing] = ancestor;
            }
            if (healed.Count == 0)
                return 0;

            transcript.HealedParents = healed;
            Dbg.Log($"orphan heal: {healed.Count} dangling reference(s) resolved via mirror");
            return healed.Count;

            void AddIfDangling(string? reference)
            {
                if (reference is not null && !present.Contains(reference)
                    && (mirrored is null || mirrored.Contains("u:" + reference)))
                {
                    dangling.Add(reference);
                }
            }
        }
        catch when (!Dbg.Active)
        {
            transcript.HealedParents = null;
            return 0;
        }
    }

    /// <summary>
    /// Walk the mirror's original chain from <paramref name="missing"/> to the
    /// first uuid present in the transcript, or to a root (null — the removed
    /// record had no parent, and neither will its healed child). False when the
    /// walk leaves the mirror or cycles: not ours to resolve.
    /// </summary>
    private static bool TryResolve(string missing, HashSet<string> present,
        Dictionary<string, string?> parentOf, out string? ancestor)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = missing;
        while (cursor is not null && !present.Contains(cursor))
        {
            if (!visited.Add(cursor) || !parentOf.TryGetValue(cursor, out cursor))
            {
                ancestor = null;
                return false;
            }
        }
        ancestor = cursor;
        return true;
    }
}
