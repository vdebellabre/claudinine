using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Claudinine.Transcript;

namespace Claudinine.Mirror;

/// <summary>
/// Per-session uncompacted mirror: what the transcript would contain if we never
/// compacted. Append-only, idempotent by uuid — its tail IS the progress marker,
/// so steady-state appends, crash recovery and SessionEnd all share one algorithm.
/// Serves restore, retrieval (stubs carry origUuid) and savings measurement.
/// </summary>
internal static class MirrorFile
{
    private const string HeaderVersion = "1";

    public static string MirrorsDirectory()
    {
        string root = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA")
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claudinine");
        return System.IO.Path.Combine(root, "mirrors");
    }

    public static string PathFor(string transcriptPath) =>
        System.IO.Path.Combine(
            MirrorsDirectory(),
            System.IO.Path.GetFileNameWithoutExtension(transcriptPath) + ".jsonl");

    /// <summary>
    /// Directories to probe when resolving a mirror for READING. Writes always use
    /// MirrorsDirectory() — hooks have CLAUDE_PLUGIN_DATA set. But `get` typically
    /// runs from the session's Bash tool, which does NOT inherit that variable
    /// (verified live 2026-08-08), and the app hands a different data dir to each
    /// install context (claudinine-inline for desktop, claudinine-&lt;marketplace&gt;
    /// for CLI), so a read must look everywhere mirrors are known to land.
    /// </summary>
    public static IReadOnlyList<string> SearchDirectories() =>
        SearchDirectories(
            Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static IReadOnlyList<string> SearchDirectories(string? pluginData, string home)
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string dir)
        {
            if (Directory.Exists(dir) && seen.Add(System.IO.Path.GetFullPath(dir)))
                dirs.Add(dir);
        }

        if (!string.IsNullOrEmpty(pluginData))
            Add(System.IO.Path.Combine(pluginData, "mirrors"));
        string dataRoot = System.IO.Path.Combine(home, ".claude", "plugins", "data");
        if (Directory.Exists(dataRoot))
        {
            foreach (string plugin in Directory.EnumerateDirectories(dataRoot, "claudinine-*"))
                Add(System.IO.Path.Combine(plugin, "mirrors"));
        }
        Add(System.IO.Path.Combine(home, ".claudinine", "mirrors"));
        return dirs;
    }

    /// <summary>
    /// Match a session's mirror files by id prefix across every known mirror
    /// directory. The same session may have a mirror in several dirs
    /// (cross-context resume) — all of them are returned; a prefix that resolves
    /// to more than one distinct session id matches nothing.
    /// </summary>
    public static List<string> FindSessionMirrors(string session)
    {
        var exact = new List<string>();
        var byPrefix = new List<string>();
        foreach (string dir in SearchDirectories())
        {
            string candidate = System.IO.Path.Combine(dir, session + ".jsonl");
            if (File.Exists(candidate))
                exact.Add(candidate);
            else
                byPrefix.AddRange(Directory.EnumerateFiles(dir, session + "*.jsonl"));
        }
        if (exact.Count > 0)
            return exact;
        int distinct = byPrefix
            .Select(p => System.IO.Path.GetFileNameWithoutExtension(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return distinct == 1 ? byPrefix : [];
    }

    // ---- skip markers: `restore-compaction-off` freezes a session ----
    //
    // Marker = `<sid>.skip` next to the session's mirror(s). File presence is the
    // whole state: hooks that see it keep mirroring but never compact, so an
    // explicit restore is never silently undone. Written next to every mirror the
    // session has (plus the write dir) because the verb typically runs from a
    // shell WITHOUT CLAUDE_PLUGIN_DATA while hooks probe all known dirs anyway.

    /// <summary>True when any known mirror dir holds a skip marker for this session.</summary>
    public static bool IsCompactionSkipped(string sessionId)
    {
        foreach (string dir in SearchDirectories())
        {
            if (File.Exists(System.IO.Path.Combine(dir, sessionId + ".skip")))
                return true;
        }
        return false;
    }

    public static void WriteSkipMarkers(string sessionId, string transcriptPath)
    {
        var content = new JsonObject
        {
            ["claudinine"] = new JsonObject
            {
                ["v"] = HeaderVersion,
                ["skipCompactionOf"] = System.IO.Path.GetFullPath(transcriptPath),
            },
        };
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string mirror in FindSessionMirrors(sessionId))
            dirs.Add(System.IO.Path.GetDirectoryName(mirror)!);
        Directory.CreateDirectory(MirrorsDirectory());
        dirs.Add(MirrorsDirectory());
        foreach (string dir in dirs)
        {
            try
            {
                File.WriteAllText(System.IO.Path.Combine(dir, sessionId + ".skip"),
                    content.ToJsonString(Json.Compact) + "\n", new UTF8Encoding(false));
            }
            catch
            {
                // one unwritable dir must not block the others
            }
        }
    }

    public static void RemoveSkipMarkers(string sessionId)
    {
        foreach (string dir in SearchDirectories())
        {
            try { File.Delete(System.IO.Path.Combine(dir, sessionId + ".skip")); } catch { }
        }
    }

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
            mirrorPath ??= PathFor(transcript.Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(mirrorPath)!);

            // Identity: uuid when present; identical uuid-less lines (repeated
            // queue-operations…) are tracked by content hash WITH multiplicity, so
            // a restore reproduces every copy.
            var seen = new HashSet<string>();
            var seenCounts = new Dictionary<string, int>();
            bool hasHeader = false;
            if (File.Exists(mirrorPath))
            {
                foreach (string line in File.ReadLines(mirrorPath, Encoding.UTF8))
                {
                    if (line.Length == 0) continue;
                    if (!hasHeader) { hasHeader = true; continue; }
                    Register(IdentityOf(line), seen, seenCounts);
                }
            }

            var toAppend = new List<string>();
            if (!hasHeader)
            {
                var header = new JsonObject
                {
                    ["claudinine"] = new JsonObject
                    {
                        ["v"] = HeaderVersion,
                        ["mirrorOf"] = System.IO.Path.GetFullPath(transcript.Path),
                    },
                };
                toAppend.Add(header.ToJsonString(Json.Compact));
            }
            var transcriptCounts = new Dictionary<string, int>();
            foreach (TranscriptRecord rec in transcript.Records)
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
                        toAppend.Add(line);
                }
                else if (seen.Add(identity))
                {
                    toAppend.Add(line);
                }
            }

            if (toAppend.Count == 0)
                return true;

            using var stream = new FileStream(mirrorPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (string line in toAppend)
                writer.Write(line + "\n");
            writer.Flush();
            stream.Flush(flushToDisk: true); // the invariant is only real once it's durable
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
            string mirrorPath = PathFor(transcript.Path);
            List<string> sources = ParentMirrorFiles(parentSessionId, mirrorPath);
            if (sources.Count == 0)
                return false;

            Directory.CreateDirectory(MirrorsDirectory());
            var seen = new HashSet<string>();
            bool hasHeader = false;
            if (File.Exists(mirrorPath))
            {
                foreach (string line in File.ReadLines(mirrorPath, Encoding.UTF8))
                {
                    if (line.Length == 0) continue;
                    if (!hasHeader) { hasHeader = true; continue; }
                    if (UuidOf(line) is string uuid)
                        seen.Add(uuid);
                }
            }

            string targetSid = System.IO.Path.GetFileNameWithoutExtension(transcript.Path);
            var toAppend = new List<string>();
            if (!hasHeader)
            {
                var header = new JsonObject
                {
                    ["claudinine"] = new JsonObject
                    {
                        ["v"] = HeaderVersion,
                        ["mirrorOf"] = System.IO.Path.GetFullPath(transcript.Path),
                    },
                };
                toAppend.Add(header.ToJsonString(Json.Compact));
            }
            // Merged records land at the mirror's END although they are
            // chronologically the session's OLDEST — this separator is how a
            // restore knows the suffix needs a chain-aware reorder. Mirrors
            // without it are guaranteed to be in original file order, app
            // write quirks included, and must never be reordered.
            var separator = new JsonObject
            {
                ["claudinine"] = new JsonObject
                {
                    ["v"] = HeaderVersion,
                    ["mergedFromFork"] = parentSessionId,
                },
            };
            toAppend.Add(separator.ToJsonString(Json.Compact));
            int preludeLines = toAppend.Count;
            foreach (string source in sources)
            {
                foreach (string rawLine in File.ReadLines(source, Encoding.UTF8))
                {
                    string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
                    if (line.Length == 0) continue;
                    JsonObject? rec;
                    try { rec = JsonNode.Parse(line) as JsonObject; } catch { continue; }
                    // The uuid requirement also skips the parent mirror's header.
                    if (rec?["uuid"]?.GetValue<string>() is not string uuid)
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

            using var stream = new FileStream(mirrorPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (string line in toAppend)
                writer.Write(line + "\n");
            writer.Flush();
            stream.Flush(flushToDisk: true); // refs are only retargeted once this is real
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? UuidOf(string line)
    {
        try
        {
            return (JsonNode.Parse(line) as JsonObject)?["uuid"]?.GetValue<string>();
        }
        catch
        {
            return null;
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
            List<string> sources = ParentMirrorFiles(sessionId, PathFor(transcript.Path));
            if (sources.Count == 0)
                return null;
            var uuids = new HashSet<string>();
            foreach (string source in sources)
            {
                foreach (string line in File.ReadLines(source, Encoding.UTF8))
                {
                    if (line.Length > 0 && UuidOf(line) is string uuid)
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

    /// <summary>A session's mirror files across all known dirs, never our own.</summary>
    private static List<string> ParentMirrorFiles(string sessionId, string ownMirrorPath)
    {
        var sources = new List<string>();
        foreach (string dir in SearchDirectories())
        {
            string candidate = System.IO.Path.Combine(dir, sessionId + ".jsonl");
            if (File.Exists(candidate)
                && !string.Equals(System.IO.Path.GetFullPath(candidate),
                    System.IO.Path.GetFullPath(ownMirrorPath), StringComparison.OrdinalIgnoreCase))
            {
                sources.Add(candidate);
            }
        }
        return sources;
    }

    /// <summary>
    /// Delete mirrors whose transcript no longer exists (the app ages transcripts
    /// out itself). Run at SessionStart; failures are ignored record by record.
    /// </summary>
    public static void CollectGarbage()
    {
        string dir = MirrorsDirectory();
        if (!Directory.Exists(dir))
            return;
        foreach (string mirror in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            try
            {
                string? headerLine = File.ReadLines(mirror, Encoding.UTF8).FirstOrDefault();
                if (headerLine is null) continue;
                if (JsonNode.Parse(headerLine) is not JsonObject header) continue;
                string? mirrorOf = header["claudinine"]?["mirrorOf"]?.GetValue<string>();
                if (mirrorOf is not null && !File.Exists(mirrorOf))
                    File.Delete(mirror);
            }
            catch
            {
                // Unreadable mirror: leave it for a human.
            }
        }
        foreach (string marker in Directory.EnumerateFiles(dir, "*.skip"))
        {
            try
            {
                string? line = File.ReadLines(marker, Encoding.UTF8).FirstOrDefault();
                if (line is null) continue;
                if (JsonNode.Parse(line) is not JsonObject header) continue;
                string? target = header["claudinine"]?["skipCompactionOf"]?.GetValue<string>();
                if (target is not null && !File.Exists(target))
                    File.Delete(marker);
            }
            catch
            {
                // Unreadable marker: leave it for a human.
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
    private static string IdentityOf(string line, string? knownUuid = null)
    {
        JsonObject? obj = null;
        if (knownUuid is null)
        {
            try
            {
                obj = JsonNode.Parse(line) as JsonObject;
            }
            catch
            {
                // fall through to raw hash
            }
        }
        string? uuid = knownUuid ?? obj?["uuid"]?.GetValue<string>();
        if (uuid is not null)
            return "u:" + uuid;
        if (obj?["leafUuid"] is not null)
        {
            obj.Remove("leafUuid");
            return "h:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(obj.ToJsonString(Json.Compact))));
        }
        return "h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
    }
}
