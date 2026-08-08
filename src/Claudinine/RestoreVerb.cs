namespace Claudinine;

/// <summary>
/// `claudinine restore-compaction-off &lt;session&gt;` / `restore-compaction-on
/// &lt;session&gt;` — rebuild the transcript from its mirror, so the next open of the
/// session loads the complete uncompacted history (the app reads the file into
/// memory BEFORE SessionStart hooks run, so one restore buys one full session).
/// The verb names carry the ongoing state: `-off` also drops a skip marker so
/// hooks stop compacting this session (an explicit restore is never silently
/// undone); `-on` removes any marker, so steady-state compaction resumes at the
/// next boundary — it doubles as the re-enable action for a frozen session.
///
/// Run only while the session is CLOSED: a live session holds its own in-memory
/// copy and appends to the file mid-turn — restoring under it is a race.
///
/// Fail-closed: the transcript is only swapped after validating that every
/// record of the current file survives into the restored one (by uuid), that the
/// tail record is preserved, and that every line parses. A mirror that cannot
/// account for a marked record aborts the restore with the reason.
/// </summary>
internal static class RestoreVerb
{
    public static int Run(string[] args, bool compactionOn)
    {
        string verb = compactionOn ? "restore-compaction-on" : "restore-compaction-off";
        if (args.Length != 1)
        {
            Console.Error.WriteLine($"usage: claudinine {verb} <session-id>");
            return 1;
        }

        var mirrors = MirrorLocator.FindSessionMirrors(args[0]);
        if (mirrors.Count == 0)
        {
            var searched = MirrorLocator.SearchDirectories();
            Console.Error.WriteLine(
                $"no mirror found for session '{args[0]}' (searched: " +
                (searched.Count == 0 ? "no mirror directory exists" : string.Join("; ", searched)) + ")");
            return 1;
        }

        string sid = Path.GetFileNameWithoutExtension(mirrors[0]);
        string? transcriptPath = MirrorTarget(mirrors[0]);
        if (transcriptPath is null || !File.Exists(transcriptPath))
        {
            Console.Error.WriteLine(
                $"the transcript this mirror belongs to no longer exists: {transcriptPath ?? "?"}");
            return 1;
        }

        var transcript = TranscriptFile.TryLoad(transcriptPath);
        if (transcript is null)
        {
            Console.Error.WriteLine($"cannot load transcript: {transcriptPath}");
            return 1;
        }

        // The re-enable half of `-on` comes first: even if the restore below
        // fails, compaction being back on is what the user asked for.
        if (compactionOn)
            SkipMarkers.Remove(sid);

        // A crashed or frozen session may hold records the mirror has not seen
        // yet (its final turn, or turns written while compaction was off).
        if (!MirrorFile.TryAppendMissing(transcript, mirrors[0]))
        {
            Console.Error.WriteLine("could not bring the mirror up to date; nothing changed");
            return 1;
        }

        (var restored, bool forkMerged) = ReadMirrors(mirrors);
        if (restored.Count == 0)
        {
            Console.Error.WriteLine("mirror holds no records; nothing changed");
            return 1;
        }
        if (forkMerged)
            restored = ChainOrder(restored);

        if (Validate(transcript, restored) is string refusal)
        {
            Console.Error.WriteLine($"restore refused ({refusal}); transcript left unchanged");
            return 1;
        }

        try
        {
            Jsonl.ReplaceAtomically(transcriptPath,
                string.Concat(restored.Select(l => l.Raw + "\n")));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"restore failed writing the transcript: {e.Message}");
            return 1;
        }

        if (!compactionOn)
            SkipMarkers.Write(sid, transcriptPath);

        Console.WriteLine($"restored {restored.Count} records to {transcriptPath}");
        Console.WriteLine(compactionOn
            ? "compaction stays on: the next open loads the full history once, then steady-state compaction resumes."
            : $"compaction is now OFF for this session (skip marker in place). Re-enable with: claudinine restore-compaction-on {sid}");
        return 0;
    }

    internal readonly record struct Line(string Raw, string? Uuid, string? ParentUuid);

    private static string? MirrorTarget(string mirrorPath)
    {
        try
        {
            string? header = File.ReadLines(mirrorPath, Encoding.UTF8).FirstOrDefault();
            if (header is null) return null;
            return (JsonNode.Parse(header) as JsonObject)?["claudinine"]?["mirrorOf"].GetString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Body records across all of the session's mirrors: uuid-bearing records
    /// deduplicated first-file-wins (cross-context copies are identical
    /// originals); identical uuid-less lines merged by max multiplicity so no
    /// mirror's copies are dropped. `forkMerged` reports whether any mirror
    /// carries a fork-heal separator — the only condition under which mirror
    /// order may deviate from original file order.
    /// </summary>
    internal static (List<Line> Lines, bool ForkMerged) ReadMirrors(List<string> mirrors)
    {
        var result = new List<Line>();
        var seenUuids = new HashSet<string>();
        var emittedCounts = new Dictionary<string, int>();
        bool forkMerged = false;
        foreach (string mirror in mirrors)
        {
            var fileCounts = new Dictionary<string, int>();
            foreach ((string line, var rec) in Jsonl.ReadRecords(mirror, skipFirst: true))
            {
                if (rec?["claudinine"]?["mergedFromFork"] is not null)
                {
                    forkMerged = true;
                    continue; // heal separator: bookkeeping, not a record
                }
                if (rec?["uuid"].GetString() is string uuid)
                {
                    if (!seenUuids.Add(uuid))
                        continue;
                    result.Add(new Line(line, uuid, rec["parentUuid"].GetString()));
                }
                else
                {
                    int nth = fileCounts[line] = fileCounts.GetValueOrDefault(line) + 1;
                    if (nth <= emittedCounts.GetValueOrDefault(line))
                        continue; // an earlier mirror already contributed this copy
                    emittedCounts[line] = nth;
                    result.Add(new Line(line, null, null));
                }
            }
        }
        return (result, forkMerged);
    }

    /// <summary>
    /// Mirror body order IS original transcript order for a normally-mirrored
    /// session — app write quirks included (real files DO hold a tool_result a
    /// couple of lines before its parent use record; that order must be
    /// reproduced verbatim, which is why this reorder runs ONLY behind the
    /// fork-heal separator). A healed mirror has the parent's pre-fork originals
    /// merged at the END although chronologically they come FIRST; the stable
    /// parent-first reorder puts them back where the chain says they belong.
    /// Pre-separator healed mirrors (healed before 0.1.7) restore complete but
    /// with the merged block left at the end.
    /// </summary>
    private static List<Line> ChainOrder(List<Line> lines)
    {
        var position = new Dictionary<string, int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Uuid is string u)
                position[u] = i;
        }

        var result = new List<Line>(lines.Count);
        var emitted = new HashSet<string>();
        var deferred = new Dictionary<string, List<Line>>();
        bool Blocked(Line l) =>
            l.ParentUuid is string p && position.ContainsKey(p) && !emitted.Contains(p);
        var ready = new Queue<Line>();
        foreach (var line in lines)
        {
            if (Blocked(line))
            {
                if (!deferred.TryGetValue(line.ParentUuid!, out var kids))
                    deferred[line.ParentUuid!] = kids = [];
                kids.Add(line);
                continue;
            }
            ready.Enqueue(line);
            while (ready.Count > 0)
            {
                var e = ready.Dequeue();
                result.Add(e);
                if (e.Uuid is not string u)
                    continue;
                emitted.Add(u);
                if (deferred.Remove(u, out var unblocked))
                {
                    foreach (var k in unblocked)
                        ready.Enqueue(k);
                }
            }
        }
        // Leftovers (cycles, which app files never contain) keep original order;
        // validation is the judge of whatever remains.
        foreach (var kids in deferred.Values)
            result.AddRange(kids);
        return result;
    }

    /// <summary>Null when the restore is safe to swap in, else the refusal reason.</summary>
    private static string? Validate(TranscriptFile transcript, List<Line> restored)
    {
        var restoredUuids = new HashSet<string>(
            restored.Where(l => l.Uuid is not null).Select(l => l.Uuid!));
        foreach (var rec in transcript.Records)
        {
            if (rec.Uuid is string uuid && !restoredUuids.Contains(uuid))
                return $"mirror does not cover live record {Rules.RuleHelpers.RefPrefix(uuid)}";
        }
        if (restored.Count < transcript.Records.Count)
            return "restored file would be smaller than the live one";

        // The app chains future appends off the in-memory tail: the restored
        // file must end on the same record the live file ends on.
        var liveTail = transcript.Records[^1];
        var restoredTail = restored[^1];
        if (liveTail.Uuid is string tailUuid)
        {
            if (restoredTail.Uuid != tailUuid)
                return "tail record would change";
        }
        else if (restoredTail.Uuid is not null)
        {
            return "uuid-less tail record would change";
        }
        return null;
    }
}
