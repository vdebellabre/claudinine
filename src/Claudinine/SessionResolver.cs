namespace Claudinine;

/// <summary>
/// Exact-else-unique-prefix resolution of a session id to its .jsonl files,
/// shared by mirror lookup and transcript lookup. The ambiguity rule is a safety
/// rule: a prefix matching two DISTINCT session ids matches nothing — never
/// guess which session the user meant.
/// </summary>
internal static class SessionResolver
{
    /// <summary>
    /// All files named exactly &lt;idOrPrefix&gt;.jsonl across <paramref name="dirs"/>;
    /// else all &lt;idOrPrefix&gt;*.jsonl matches IF they belong to a single session id;
    /// else empty.
    /// </summary>
    public static List<string> ResolveByIdOrUniquePrefix(IEnumerable<string> dirs, string idOrPrefix)
    {
        var exact = new List<string>();
        var byPrefix = new List<string>();
        foreach (string dir in dirs)
        {
            string candidate = Path.Combine(dir, idOrPrefix + ".jsonl");
            if (File.Exists(candidate))
                exact.Add(candidate);
            else
                byPrefix.AddRange(Directory.EnumerateFiles(dir, idOrPrefix + "*.jsonl"));
        }
        if (exact.Count > 0)
            return exact;
        int distinct = byPrefix
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return distinct == 1 ? byPrefix : [];
    }
}
