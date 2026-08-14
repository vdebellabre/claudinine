namespace Claudinine.Mirror;

/// <summary>
/// Per-session uncompacted mirror: what the transcript would contain if we never
/// compacted. Append-only, idempotent by uuid — its tail IS the progress marker,
/// so steady-state appends, crash recovery and SessionEnd all share one algorithm.
/// Serves restore, retrieval (stubs carry origUuid) and savings measurement.
/// Path resolution lives in <see cref="MirrorLocator"/>; the freeze-marker
/// lifecycle in <see cref="SkipMarkers"/>.
/// </summary>
internal static class MirrorFile
{
    /// <summary>
    /// Append every transcript record not yet mirrored. Must succeed BEFORE any
    /// compaction (mirror-first invariant): nothing is ever stubbed that is not
    /// already mirrored. Records that already carry a claudinine marker are skipped —
    /// their original went into the mirror when they were first seen.
    /// `mirrorPath` overrides the env-derived location for callers (restore) that
    /// must target the dir where the session's mirror actually lives.
    /// </summary>
    public static bool TryAppendMissing(TranscriptFile transcript, string? mirrorPath = null)
    {
        try
        {
            mirrorPath ??= MirrorLocator.PathFor(transcript.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(mirrorPath)!);

            // Identity: uuid when present; identical uuid-less lines (repeated
            // queue-operations…) are tracked by content hash WITH multiplicity, so
            // a restore reproduces every copy.
            //
            // The state comes from the SeenCache sidecar when its length key
            // matches the mirror — the full mirror re-read+parse below was the
            // largest steady-state stage at every session size (see the cache's
            // doc comment). Any mismatch falls back to the full read, which is
            // also what rebuilds the cache.
            var seen = new HashSet<string>();
            var seenCounts = new Dictionary<string, int>();
            bool hasHeader = false;
            bool cacheValid = false;
            long mirrorLength = 0;
            if (File.Exists(mirrorPath))
            {
                mirrorLength = new FileInfo(mirrorPath).Length;
                if (mirrorLength > 0
                    && SeenCache.TryLoad(mirrorPath, mirrorLength, seen, seenCounts))
                {
                    // A non-empty mirror always starts with the header line.
                    hasHeader = true;
                    cacheValid = true;
                }
                else
                {
                    foreach (var (line, node) in Jsonl.ReadRecords(mirrorPath))
                    {
                        if (!hasHeader) { hasHeader = true; continue; }
                        Register(IdentityOf(line, node), seen, seenCounts);
                    }
                }
            }

            var toAppend = new List<string>();
            // Identities of the appended records, one entry per line appended
            // (excluding the header, which is not a record) — the cache batch.
            var appended = new List<string>();
            if (!hasHeader)
                toAppend.Add(MirrorFormat.Line("mirrorOf", Path.GetFullPath(transcript.Path)));
            var transcriptCounts = new Dictionary<string, int>();
            foreach (var rec in transcript.Records)
            {
                if (rec.Node["claudinine"] is not null)
                    continue; // already a stub; its original is already mirrored
                string line = rec.HadCarriageReturn ? rec.RawLine[..^1] : rec.RawLine;
                string identity = IdentityOf(line, rec.Uuid);
                if (identity.StartsWith("h:", StringComparison.Ordinal))
                {
                    // uuid-less: mirror as many copies as the transcript holds.
                    int nth = transcriptCounts[identity] = transcriptCounts.GetValueOrDefault(identity) + 1;
                    if (nth > seenCounts.GetValueOrDefault(identity))
                    {
                        toAppend.Add(line);
                        appended.Add(identity);
                    }
                }
                else if (seen.Add(identity))
                {
                    toAppend.Add(line);
                    appended.Add(identity);
                }
            }

            if (toAppend.Count == 0)
            {
                // No-op pass over a mirror whose cache was missing or stale: heal
                // it now so the next pass skips the full read we just paid.
                if (!cacheValid && hasHeader)
                    SeenCache.TryRewrite(mirrorPath, mirrorLength, seen, seenCounts);
                return true;
            }

            // Durable: the mirror-first invariant is only real once it's on disk.
            Jsonl.WriteLinesDurably(mirrorPath, FileMode.Append, toAppend);

            // Cache upkeep, AFTER the mirror bytes are durable. Post-loop `seen`
            // is exactly the mirror's post-append uuid set (Add gates every
            // append); h-multiplicities are max(mirror, transcript) per identity.
            // A fresh mirror writes its cache here too: the identities are already
            // in memory and the write is a fraction of the mirror append it rides
            // on, whereas deferring it would make the NEXT pass re-parse the
            // mirror this pass just wrote in full.
            long newLength = new FileInfo(mirrorPath).Length;
            if (cacheValid)
            {
                SeenCache.TryAppendBatch(mirrorPath, newLength, appended);
            }
            else
            {
                foreach ((string identity, int count) in transcriptCounts)
                {
                    if (count > seenCounts.GetValueOrDefault(identity))
                        seenCounts[identity] = count;
                }
                SeenCache.TryRewrite(mirrorPath, newLength, seen, seenCounts);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Merge a fork parent's mirror into this transcript's own mirror. When the
    /// desktop forks a conversation to a new session id, the pre-fork originals
    /// exist only in the parent's mirror — which CollectGarbage deletes the moment
    /// the parent transcript is aged out, killing retrieval for the LIVE fork.
    /// Only uuid-bearing records are merged (retrieval addresses by uuid; uuid-less
    /// metadata has no retrieval value), deduplicated against the target, with
    /// sessionId rebound so the mirror reads as this session's own history.
    /// Returns true only when a parent mirror was found and the merge is durable —
    /// the caller's license to retarget digest refs at this session.
    /// </summary>
    public static bool TryAdoptForkParent(string parentSessionId, TranscriptFile transcript)
    {
        try
        {
            string mirrorPath = MirrorLocator.PathFor(transcript.Path);
            var sources = MirrorLocator.ParentMirrorFiles(parentSessionId, mirrorPath);
            if (sources.Count == 0)
                return false;

            Directory.CreateDirectory(MirrorLocator.MirrorsDirectory());
            var seen = new HashSet<string>();
            bool hasHeader = false;
            if (File.Exists(mirrorPath))
            {
                foreach ((string _, var node) in Jsonl.ReadRecords(mirrorPath))
                {
                    if (!hasHeader) { hasHeader = true; continue; }
                    if (node?["uuid"].GetString() is string uuid)
                        seen.Add(uuid);
                }
            }

            string targetSid = Path.GetFileNameWithoutExtension(transcript.Path);
            var toAppend = new List<string>();
            if (!hasHeader)
                toAppend.Add(MirrorFormat.Line("mirrorOf", Path.GetFullPath(transcript.Path)));
            // Merged records land at the mirror's END although they are
            // chronologically the session's OLDEST — this separator is how a
            // restore knows the suffix needs a chain-aware reorder. Mirrors
            // without it are guaranteed to be in original file order, app
            // write quirks included, and must never be reordered.
            toAppend.Add(MirrorFormat.Line("mergedFromFork", parentSessionId));
            int preludeLines = toAppend.Count;
            foreach (string source in sources)
            {
                foreach ((string _, var rec) in Jsonl.ReadRecords(source))
                {
                    // The uuid requirement also skips the parent mirror's header.
                    if (rec?["uuid"].GetString() is not string uuid)
                        continue;
                    if (!seen.Add(uuid))
                        continue;
                    if (rec["sessionId"] is not null)
                        rec["sessionId"] = targetSid;
                    toAppend.Add(rec.ToJsonString(Json.Compact));
                }
            }
            if (toAppend.Count == preludeLines)
                return true; // no new records: already merged on an earlier pass

            // Durable: refs are only retargeted once this is real.
            Jsonl.WriteLinesDurably(mirrorPath, FileMode.Append, toAppend);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Every uuid held by a session's mirror(s), or null when no mirror exists.
    /// This is the fork-vs-quote discriminator: a record genuinely copied by a
    /// fork was mirrored by the parent under the SAME uuid, while a record that
    /// merely quotes another session's retrieval command never appears in that
    /// session's mirror.
    /// </summary>
    public static HashSet<string>? MirrorUuidsOf(string sessionId, TranscriptFile transcript)
    {
        try
        {
            var sources = MirrorLocator.ParentMirrorFiles(
                sessionId, MirrorLocator.PathFor(transcript.Path));
            if (sources.Count == 0)
                return null;
            var uuids = new HashSet<string>();
            foreach (string source in sources)
            {
                foreach ((string _, var node) in Jsonl.ReadRecords(source))
                {
                    if (node?["uuid"].GetString() is string uuid)
                        uuids.Add(uuid);
                }
            }
            return uuids;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Delete mirrors (and skip markers and load stamps, via
    /// <see cref="SkipMarkers.CollectGarbage"/> / <see cref="LoadStamp.CollectGarbage"/>)
    /// whose transcript no longer exists — the app ages transcripts out itself.
    /// Run at SessionStart; failures are ignored file by file. Sweeps every known
    /// mirror dir, not just this context's write dir: skip markers fan out to all
    /// of them, and an uninstalled context's hooks never run again to clean up
    /// their own.
    /// </summary>
    public static void CollectGarbage() => CollectGarbage(MirrorLocator.SearchDirectories());

    /// <summary>Explicit-dirs seam, same reason as SearchDirectories: tests must
    /// never sweep (and delete from) the developer's real mirror dirs.</summary>
    internal static void CollectGarbage(IReadOnlyList<string> dirs)
    {
        foreach (string dir in dirs)
        {
            CollectGarbage(dir);
            SkipMarkers.CollectGarbage(dir);
            LoadStamp.CollectGarbage(dir);
        }
    }

    private static void CollectGarbage(string dir)
    {
        foreach (string mirror in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            try
            {
                string? headerLine = File.ReadLines(mirror, Encoding.UTF8).FirstOrDefault();
                if (headerLine is null) continue;
                if (JsonNode.Parse(headerLine) is not JsonObject header) continue;
                string? mirrorOf = header["claudinine"]?["mirrorOf"].GetString();
                if (mirrorOf is not null && !File.Exists(mirrorOf))
                {
                    File.Delete(mirror);
                    SeenCache.TryDelete(mirror);
                }
            }
            catch
            {
                // Unreadable mirror: leave it for a human.
            }
        }

        // Orphaned seen-caches: their mirror is gone (deleted above in a previous
        // sweep that crashed between the two deletes, or by an older version that
        // did not know about sidecars). A cache without its mirror is pure noise.
        foreach (string cache in SeenCache.CacheFiles(dir))
        {
            try
            {
                if (!File.Exists(SeenCache.MirrorPathOf(cache)))
                    File.Delete(cache);
            }
            catch
            {
                // Best-effort, same as the rest of the sweep.
            }
        }
    }

    private static void Register(string identity, HashSet<string> seen, Dictionary<string, int> counts)
    {
        if (identity.StartsWith("h:", StringComparison.Ordinal))
            counts[identity] = counts.GetValueOrDefault(identity) + 1;
        else
            seen.Add(identity);
    }

    /// <summary>
    /// Identity for the "already mirrored?" test: uuid when present, else a content
    /// hash. For uuid-less records the hash EXCLUDES leafUuid — the rewrite layer
    /// remaps it (nearest surviving ancestor) on records like last-prompt, and that
    /// remapped variant is not a new original; re-appending it would pollute the
    /// mirror with a rewritten copy on the pass after a collapse.
    /// </summary>
    /// <param name="disposableObj">The line's parse, owned by this method — it may
    /// be mutated. NEVER pass a TranscriptRecord.Node here.</param>
    private static string IdentityOf(string line, JsonObject? disposableObj)
    {
        if (disposableObj?["uuid"].GetString() is string uuid)
            return "u:" + uuid;
        if (disposableObj?["leafUuid"] is not null)
        {
            disposableObj.Remove("leafUuid");
            return "h:" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(disposableObj.ToJsonString(Json.Compact))));
        }
        return "h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
    }

    /// <summary>Identity when the record's uuid is already known (transcript records).</summary>
    private static string IdentityOf(string line, string? knownUuid)
    {
        if (knownUuid is not null)
            return "u:" + knownUuid;
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(line) as JsonObject; }
        catch { /* fall through to raw hash */ }
        return IdentityOf(line, obj);
    }
}
