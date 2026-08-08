using System.Text;
using System.Text.Json.Nodes;
using Claudinine.Mirror;
using Claudinine.Transcript;

namespace Claudinine;

/// <summary>
/// `claudinine clone &lt;session&gt;` — copy a session's transcript and mirror under a
/// fresh session id, so the user can `/resume` into the compacted transcript.
///
/// Why this exists: our hooks compact the transcript ON DISK as a session runs, but
/// the running process assembled its context at startup and never re-reads the file.
/// The savings are real and banked, yet unusable until someone starts a new session.
/// Cloning produces that resumable session without waiting for the current one to end.
///
/// Non-destructive by design: the source transcript and mirror are left untouched.
/// Archiving or deleting the old pair is a separate, explicit act — a clone that is
/// never resumed must cost nothing but disk.
/// </summary>
internal static class CloneVerb
{
    /// <summary>
    /// Appended to the clone's custom-title. Two identical titles in the resume
    /// picker are indistinguishable; the marker is what makes the clone pickable.
    /// </summary>
    private const string TitleSuffix = " (compacted)";

    public static int Run(string[] args) =>
        Run(args, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Home is a parameter so tests can point the whole operation at a temp profile:
    /// SpecialFolder.UserProfile reads the registry on Windows and ignores the
    /// USERPROFILE variable, so an env override cannot redirect it. Same reason
    /// MirrorFile.SearchDirectories takes its home explicitly.
    /// </summary>
    internal static int Run(string[] args, string home)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: claudinine clone <session-id>");
            return 1;
        }

        string session = args[0];
        if (args.Length > 1)
        {
            Console.Error.WriteLine($"unknown argument: {args[1]}");
            return 1;
        }

        string? sourceTranscript = FindTranscript(session, home);
        if (sourceTranscript is null)
        {
            Console.Error.WriteLine(
                $"no transcript found for session '{session}' (searched: " +
                string.Join("; ", ProjectDirectories(home)) + ")");
            return 1;
        }

        string sourceId = Path.GetFileNameWithoutExtension(sourceTranscript);
        string targetId = Guid.NewGuid().ToString();
        string targetTranscript = Path.Combine(
            Path.GetDirectoryName(sourceTranscript)!, targetId + ".jsonl");

        if (File.Exists(targetTranscript))
        {
            Console.Error.WriteLine($"target transcript already exists: {targetTranscript}");
            return 1;
        }

        string? targetMirror = null; // set the moment we own a mirror file to clean up
        try
        {
            string? title = RewriteTranscript(sourceTranscript, targetTranscript, sourceId, targetId);
            Console.WriteLine($"  Transcript: {targetTranscript}");
            if (title is not null)
                Console.WriteLine($"  Title:      {title}");

            string? sourceMirror = FindSourceMirror(sourceId, home);
            if (sourceMirror is not null)
            {
                string candidate = Path.Combine(
                    Path.GetDirectoryName(sourceMirror)!, targetId + ".jsonl");
                if (!File.Exists(candidate))
                {
                    targetMirror = candidate;
                    CloneMirror(sourceMirror, candidate, targetId, targetTranscript);
                }
            }
            if (targetMirror is not null)
                Console.WriteLine($"  Mirror:     {targetMirror}");
            else
                Console.WriteLine("  Mirror:     none found — retrieval refs will not resolve.");

            Console.WriteLine();
            Console.WriteLine($"  Clone ready. Resume it with:  claude --resume {targetId}");
            Console.WriteLine($"  The original session ({Rules.RuleHelpers.RefPrefix(sourceId)}) is untouched.");
            return 0;
        }
        catch (Exception ex)
        {
            // A half-written clone is worse than none: the transcript would show up in
            // the resume picker as a plausible session and fail on load, and a partial
            // headerless mirror is invisible to CollectGarbage forever.
            try { if (File.Exists(targetTranscript)) File.Delete(targetTranscript); } catch { }
            try { if (targetMirror is not null && File.Exists(targetMirror)) File.Delete(targetMirror); } catch { }
            Console.Error.WriteLine($"clone failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Copy the transcript line by line, rebinding every record to the new session.
    /// Returns the clone's title, when the source carried one.
    /// </summary>
    private static string? RewriteTranscript(
        string source, string target, string sourceId, string targetId)
    {
        string? lastTitle = null;
        bool sawTitle = false;

        using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            foreach (string rawLine in File.ReadLines(source, Encoding.UTF8))
            {
                string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
                if (line.Length == 0)
                    continue;

                JsonObject? node;
                try { node = JsonNode.Parse(line) as JsonObject; }
                catch { node = null; }

                if (node is null)
                {
                    // Unparseable line: copy verbatim rather than drop history.
                    writer.Write(line + "\n");
                    continue;
                }

                RebindSessionId(node, targetId);
                RewriteRetrievalCommands(node, sourceId, targetId);

                if (node["type"].GetString() == "custom-title")
                {
                    // Titles are appended repeatedly, last-wins. Suffix each one so the
                    // clone is distinguishable no matter which the app reads.
                    string? original = node["customTitle"].GetString();
                    if (original is not null)
                    {
                        string suffixed = Suffixed(original);
                        node["customTitle"] = suffixed;
                        lastTitle = suffixed;
                        sawTitle = true;
                    }
                }

                writer.Write(node.ToJsonString(Json.Compact) + "\n");
            }

            if (!sawTitle)
            {
                // No title in the source: the app derives one from the first prompt, so
                // both sessions would read alike. Give the clone an explicit one.
                lastTitle = Suffixed(Rules.RuleHelpers.RefPrefix(sourceId));
                var titleRecord = new JsonObject
                {
                    ["type"] = "custom-title",
                    ["customTitle"] = lastTitle,
                    ["sessionId"] = targetId,
                };
                writer.Write(titleRecord.ToJsonString(Json.Compact) + "\n");
            }

            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        return lastTitle;
    }

    private static string Suffixed(string title) =>
        title.EndsWith(TitleSuffix, StringComparison.Ordinal) ? title : title + TitleSuffix;

    /// <summary>
    /// Point a record at the new session. Only the session binding moves: uuid,
    /// parentUuid and leafUuid stay as they are, because the clone preserves the
    /// original chain topology and mirror refs are addressed by those uuids.
    /// </summary>
    private static void RebindSessionId(JsonObject node, string targetId)
    {
        if (node["sessionId"] is not null)
            node["sessionId"] = targetId;
    }

    /// <summary>
    /// Rewrite the `claudinine get &lt;sid&gt; …` commands our own digests embed in their
    /// text. Without this the clone's digests would send every retrieval at the source
    /// session id — which resolves to the source mirror, or to nothing once the source
    /// is archived. Every emitter (chain-collapse, carrier-header dedup, anchor-input
    /// stubs, image-strip) spells exactly `claudinine get &lt;full-id&gt;`, so the rewrite
    /// matches that whole phrase — NEVER the bare id, which also occurs in strings that
    /// must survive verbatim (persisted-output sidecar paths under the source session's
    /// directory are the only pointer to those files).
    /// </summary>
    private static void RewriteRetrievalCommands(JsonNode? node, string sourceId, string targetId)
    {
        string sourcePhrase = "claudinine get " + sourceId;
        string targetPhrase = "claudinine get " + targetId;
        RewritePhrase(node, sourcePhrase, targetPhrase);
    }

    private static void RewritePhrase(JsonNode? node, string sourcePhrase, string targetPhrase)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(kv => kv.Key).ToList())
                {
                    if (obj[key] is JsonValue value
                        && value.TryGetValue<string>(out string? text)
                        && text.Contains(sourcePhrase, StringComparison.OrdinalIgnoreCase))
                    {
                        obj[key] = text.Replace(sourcePhrase, targetPhrase, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        RewritePhrase(obj[key], sourcePhrase, targetPhrase);
                    }
                }
                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value
                        && value.TryGetValue<string>(out string? text)
                        && text.Contains(sourcePhrase, StringComparison.OrdinalIgnoreCase))
                    {
                        array[i] = text.Replace(sourcePhrase, targetPhrase, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        RewritePhrase(array[i], sourcePhrase, targetPhrase);
                    }
                }
                break;
        }
    }

    /// <summary>The source session's mirror, from the first search dir that has one.</summary>
    private static string? FindSourceMirror(string sourceId, string home)
    {
        foreach (string dir in MirrorFile.SearchDirectories(
            Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA"), home))
        {
            string candidate = Path.Combine(dir, sourceId + ".jsonl");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Copy the mirror under the new session id, repointing its header at the clone's
    /// transcript. That header field is load-bearing: MirrorFile.CollectGarbage deletes
    /// any mirror whose mirrorOf target is gone, so a verbatim copy would have the
    /// clone's mirror collected the moment the source transcript is archived.
    /// Written next to the source mirror — the dir the writing hook actually uses.
    /// </summary>
    private static void CloneMirror(
        string sourceMirror, string targetMirror, string targetId, string targetTranscript)
    {
        using var stream = new FileStream(targetMirror, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        bool first = true;
        foreach (string rawLine in File.ReadLines(sourceMirror, Encoding.UTF8))
        {
            string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
                continue;

            if (first)
            {
                first = false;
                if (JsonNode.Parse(line) is JsonObject header
                    && header["claudinine"] is JsonObject meta)
                {
                    meta["mirrorOf"] = Path.GetFullPath(targetTranscript);
                    writer.Write(header.ToJsonString(Json.Compact) + "\n");
                    continue;
                }
            }

            // Mirror bodies are pristine originals — the whole point of the mirror.
            // Only the session binding moves; retrieval matches on uuid, untouched.
            if (JsonNode.Parse(line) is JsonObject rec)
            {
                RebindSessionId(rec, targetId);
                writer.Write(rec.ToJsonString(Json.Compact) + "\n");
            }
            else
            {
                writer.Write(line + "\n");
            }
        }
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Resolve a session id (exact, else unique prefix) to its transcript.</summary>
    private static string? FindTranscript(string session, string home)
    {
        var byPrefix = new List<string>();
        foreach (string dir in ProjectDirectories(home))
        {
            string candidate = Path.Combine(dir, session + ".jsonl");
            if (File.Exists(candidate))
                return candidate;
            byPrefix.AddRange(Directory.EnumerateFiles(dir, session + "*.jsonl"));
        }
        int distinct = byPrefix
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return distinct == 1 ? byPrefix[0] : null;
    }

    /// <summary>
    /// Project transcript directories. The current session's own project dir first
    /// (cheap hit for the common case), then every project the app knows about — a
    /// clone may well target a session from another repo.
    /// </summary>
    private static IReadOnlyList<string> ProjectDirectories(string home)
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string dir)
        {
            if (Directory.Exists(dir) && seen.Add(Path.GetFullPath(dir)))
                dirs.Add(dir);
        }

        string projects = Path.Combine(home, ".claude", "projects");
        if (Directory.Exists(projects))
        {
            foreach (string project in Directory.EnumerateDirectories(projects))
                Add(project);
        }
        return dirs;
    }
}
