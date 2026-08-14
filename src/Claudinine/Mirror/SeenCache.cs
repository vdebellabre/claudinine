using System.Globalization;

namespace Claudinine.Mirror;

/// <summary>
/// Sidecar cache of a mirror's identity multiset (`&lt;mirror&gt;.jsonl.seen`),
/// keyed by the mirror's byte length. <see cref="MirrorFile.TryAppendMissing"/>
/// used to re-read and JSON-parse the ENTIRE mirror on every hook invocation to
/// rebuild its dedup state — the largest steady-state stage at every session
/// size, 73% of the structural per-prompt cost on a 15.6 MB session
/// (eng/bench/profiling-notes.md). This file is exactly that state, persisted:
/// one `u:` line per uuid identity, one `h:` line PER COPY of a content-hash
/// identity (multiplicity is line count), a `len:` marker after every batch.
///
/// Fail-closed by construction: the cache is valid only when its FINAL line is a
/// `len:` marker equal to the mirror's current byte length. Any out-of-band
/// mirror write (fork adoption, a restore targeting another dir, manual edits),
/// any torn cache append, or any unrecognized content fails validation, and the
/// caller falls back to the full mirror read — which then rewrites the cache.
/// Deleting the cache at any moment costs one full re-read and nothing else.
///
/// Never fsynced, deliberately: this is derived state, and the mirror-first
/// durability guarantee lives in the mirror itself.
/// </summary>
internal static class SeenCache
{
    private const string Header = "claudinine-seen v1";
    private const string Suffix = ".seen";

    public static string PathFor(string mirrorPath) => mirrorPath + Suffix;

    /// <summary>Mirror path a sidecar belongs to (inverse of <see cref="PathFor"/>).</summary>
    public static string MirrorPathOf(string cachePath) => cachePath[..^Suffix.Length];

    /// <summary>
    /// Populate <paramref name="seen"/>/<paramref name="counts"/> from the cache,
    /// true only when the cache is present, well-formed, and its final marker
    /// matches <paramref name="mirrorLength"/>. On any failure the sets are left
    /// EMPTY (cleared), so the caller can run the full read into them directly.
    /// </summary>
    public static bool TryLoad(string mirrorPath, long mirrorLength,
        HashSet<string> seen, Dictionary<string, int> counts)
    {
        try
        {
            string path = PathFor(mirrorPath);
            if (!File.Exists(path))
                return false;

            string expected = "len:" + mirrorLength.ToString(CultureInfo.InvariantCulture);
            string? last = null;
            bool first = true;
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                if (first)
                {
                    if (line != Header)
                        return Invalid(seen, counts);
                    first = false;
                    continue;
                }
                if (line.StartsWith("u:", StringComparison.Ordinal))
                {
                    seen.Add(line);
                }
                else if (line.StartsWith("h:", StringComparison.Ordinal))
                {
                    counts[line] = counts.GetValueOrDefault(line) + 1;
                }
                else if (!line.StartsWith("len:", StringComparison.Ordinal))
                {
                    return Invalid(seen, counts);
                }
                last = line;
            }
            return last == expected || Invalid(seen, counts);
        }
        catch
        {
            return Invalid(seen, counts);
        }
    }

    /// <summary>
    /// Extend a VALID cache with one appended batch: the identities just written
    /// to the mirror, then the marker for its new length — one buffered append,
    /// no fsync. A tear leaves the final line short of a matching marker, which
    /// TryLoad rejects.
    /// </summary>
    public static void TryAppendBatch(
        string mirrorPath, long newMirrorLength, IReadOnlyList<string> identities)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (string identity in identities)
                sb.Append(identity).Append('\n');
            sb.Append("len:").Append(newMirrorLength).Append('\n');
            File.AppendAllText(PathFor(mirrorPath), sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            TryDelete(mirrorPath); // a cache in unknown state is worse than none
        }
    }

    /// <summary>
    /// Write the cache from scratch (after a full mirror read). Atomic via temp +
    /// rename so a crash never leaves a half-written file AT the cache path.
    /// </summary>
    public static void TryRewrite(string mirrorPath, long mirrorLength,
        HashSet<string> seen, Dictionary<string, int> counts)
    {
        string temp = PathFor(mirrorPath) + Jsonl.TempSuffix;
        try
        {
            using (var writer = new StreamWriter(temp, append: false, new UTF8Encoding(false)))
            {
                writer.Write(Header);
                writer.Write('\n');
                foreach (string identity in seen)
                {
                    writer.Write(identity);
                    writer.Write('\n');
                }
                foreach ((string identity, int count) in counts)
                {
                    for (int i = 0; i < count; i++)
                    {
                        writer.Write(identity);
                        writer.Write('\n');
                    }
                }
                writer.Write("len:");
                writer.Write(mirrorLength.ToString(CultureInfo.InvariantCulture));
                writer.Write('\n');
            }
            File.Move(temp, PathFor(mirrorPath), overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            TryDelete(mirrorPath);
        }
    }

    public static void TryDelete(string mirrorPath)
    {
        try { File.Delete(PathFor(mirrorPath)); } catch { }
    }

    /// <summary>All sidecar files in a mirror directory, for garbage collection.</summary>
    public static IEnumerable<string> CacheFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.jsonl" + Suffix);

    private static bool Invalid(HashSet<string> seen, Dictionary<string, int> counts)
    {
        seen.Clear();
        counts.Clear();
        return false;
    }
}
