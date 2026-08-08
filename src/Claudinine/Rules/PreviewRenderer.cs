using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Claudinine.Rules;

/// <summary>
/// Per-tool preview renderers for collapsed digest lines — faithful port of
/// cozempic's <c>_digest_render.py</c>. A digest line is the ONLY thing a future
/// session sees without paying for retrieval, so the preview must carry the
/// *verdict* of the call, not merely its first bytes: pytest writes its summary at
/// the TAIL; git leads with context; Edit/Write return a fixed success sentence
/// (the informative part is the path); JSON head-previews as pure punctuation.
/// </summary>
internal static partial class PreviewRenderer
{
    // pytest terminal summary, e.g. "40 failed, 1826 passed, 48 skipped ... in 131.16s"
    [GeneratedRegex(
        @"^\s*(?=.*\b\d+\s+(?:passed|failed|error))(?:=+\s*)?(.*?\b\d+\s+(?:passed|failed|errors?|skipped)[^=\n]*?)(?:\s*=+)?\s*$",
        RegexOptions.Multiline)]
    private static partial Regex PytestSummary();

    [GeneratedRegex(@"^FAILED (\S+)", RegexOptions.Multiline)]
    private static partial Regex PytestFailedName();

    // A line that is only an `echo` separator ("=== hooks.json ===", "--- foo ---"):
    // OUR OWN section labels, not output — the single most common weak-preview cause.
    [GeneratedRegex(@"^\s*(?:[=\-*#]{2,}.*?[=\-*#]{2,}|[=\-*#]{3,})\s*$")]
    private static partial Regex Banner();

    [GeneratedRegex(@"^\s*\d+\t")]
    private static partial Regex ReadGutter();

    [GeneratedRegex(@"\|\s*(tail|wc)\b|\btail -")]
    private static partial Regex TailPipeline();

    [GeneratedRegex(@"\bgit\s+status")]
    private static partial Regex GitStatus();

    [GeneratedRegex(@"\bgit\s+log")]
    private static partial Regex GitLog();

    private static readonly string[] ErrorMarkers =
        ["Traceback (most recent call last)", "FAILED ", "ERROR ", "fatal:", "error:"];

    /// <summary>
    /// Build the one-line preview for a digest entry. Ordering is deliberate: a
    /// hard verdict (test summary, error marker) wins over positional heuristics,
    /// because that is the part a reader must not miss.
    /// </summary>
    public static string RenderPreview(string tool, string arg, string text, bool isError = false)
    {
        string prefix = isError ? "[ERROR] " : "";

        // Claude Code overflowed this result to a sidecar file and kept only a
        // preview. The path is the whole verdict — and the only pointer to a file
        // nothing garbage-collects — so it must survive collapse, ahead of every
        // heuristic below (an overflowed diff routinely contains "error:").
        if (RuleHelpers.PersistedOutputPath(text) is string sidecar)
            return prefix + $"output persisted to {sidecar}";

        if (arg.Contains("pytest") || text[..Math.Min(400, text.Length)].Contains("pytest"))
        {
            string? p = PytestPreview(text);
            if (p is not null)
                return prefix + p;
        }

        // Any output carrying an explicit failure marker: surface it, not the head.
        foreach (string marker in ErrorMarkers)
        {
            if (!text.Contains(marker, StringComparison.Ordinal))
                continue;
            foreach (string line in text.Split('\n'))
            {
                if (line.Contains(marker, StringComparison.Ordinal))
                    return prefix + $"CONTAINS '{marker.Trim()}' :: {Truncate(line.Trim(), 160)}";
            }
        }

        if (tool is "Edit" or "Write")
            return prefix + (arg.Length > 0 ? $"applied to {Truncate(arg, 120)}" : Head(text, 1));

        if (tool == "Read")
        {
            int n = text.Split('\n').Length;
            // Skip the line-number gutter and punctuation-only lines so the preview
            // lands on the first line that actually says what the file is.
            var body = new List<string>();
            foreach (string line in InformativeLines(text))
            {
                string stripped = ReadGutter().Replace(line, "").Trim();
                if (stripped.Length > 0 && stripped.Any(c => !@"{}[](),:""' ".Contains(c)))
                    body.Add(stripped);
                if (body.Count == 2)
                    break;
            }
            string joined = Truncate(string.Join(" / ", body), 160);
            return prefix + $"{n} lines :: " + (joined.Length > 0 ? joined : Head(text, 1));
        }

        // JSON payloads (MCP tools, APIs): describe the shape — a head preview
        // shows only punctuation.
        string lstripped = text.TrimStart();
        if (lstripped.StartsWith('[') || lstripped.StartsWith('{'))
        {
            string? shape = JsonShape(text);
            if (shape is not null)
                return prefix + shape;
        }

        if (text.Trim().Length == 0)
            return prefix + "(no output)";

        if (tool is "Bash" or "PowerShell")
        {
            string? g = GitPreview(arg, text);
            if (g is not null)
                return prefix + g;
            // Multi-section output (our own `echo "=== x ==="` scaffolding):
            // summarize every section rather than showing only the first.
            string? s = Sectioned(text);
            if (s is not null)
                return prefix + s;
            // Commands whose payoff is at the end (pipelines into tail/wc, counters).
            if (TailPipeline().IsMatch(arg))
                return prefix + $"tail :: {Tail(text, 3)}";
            return prefix + Head(text, 2);
        }

        return prefix + Head(text, 2);
    }

    private static string? PytestPreview(string text)
    {
        var matches = PytestSummary().Matches(text);
        if (matches.Count == 0)
            return null;
        string summary = matches[^1].Groups[1].Value.Trim();
        var failed = PytestFailedName().Matches(text).Select(m => m.Groups[1].Value).ToList();
        string result = $"RESULT: {summary}";
        if (failed.Count > 0)
        {
            string shown = string.Join(", ", failed.Take(3));
            string more = failed.Count > 3 ? $" (+{failed.Count - 3} more)" : "";
            result += $" | first failures: {shown}{more}";
        }
        return Truncate(result, 300);
    }

    private static string? GitPreview(string cmd, string text)
    {
        if (GitStatus().IsMatch(cmd))
        {
            int n = text.Split('\n').Count(l => l.Trim().Length > 0);
            return $"{n} status line(s) :: {Head(text, 2)}";
        }
        if (GitLog().IsMatch(cmd))
        {
            int n = text.Split('\n').Count(l => l.Trim().Length > 0);
            return $"{n} commit line(s) :: {Head(text, 2)}";
        }
        return null;
    }

    /// <summary>
    /// For output with several `=== label ===` sections, name the sections and
    /// show the first informative line of each — a head preview would show only
    /// the first section and silently hide the rest.
    /// </summary>
    private static string? Sectioned(string text)
    {
        string[] lines = text.Split('\n');
        var idx = Enumerable.Range(0, lines.Length).Where(i => Banner().IsMatch(lines[i])).ToList();
        if (idx.Count < 2)
            return null;
        var parts = new List<string>();
        for (int k = 0; k < idx.Count; k++)
        {
            string label = lines[idx[k]].Trim(' ', '=', '-', '*', '#');
            if (label.Length == 0) label = "?";
            int end = k + 1 < idx.Count ? idx[k + 1] : lines.Length;
            string body = lines[(idx[k] + 1)..end].Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0) ?? "";
            parts.Add($"{label}: {Truncate(body, 60)}");
        }
        return $"{idx.Count} sections | " + string.Join(" | ", parts);
    }

    private static string? JsonShape(string text)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch
        {
            return null;
        }
        if (parsed is JsonArray arr)
        {
            string shape = "";
            if (arr.FirstOrDefault() is JsonObject first)
            {
                var keys = first.Select(kv => kv.Key).Order().Take(6);
                shape = $" of objects with keys [{string.Join(", ", keys)}]";
            }
            return $"JSON array, {arr.Count} item(s){shape}";
        }
        if (parsed is JsonObject obj)
        {
            var keys = obj.Select(kv => kv.Key).Order().Take(8);
            return $"JSON object, keys [{string.Join(", ", keys)}]";
        }
        return null;
    }

    private static IEnumerable<string> InformativeLines(string text) =>
        text.Split('\n').Where(l => l.Trim().Length > 0 && !Banner().IsMatch(l));

    private static string Head(string text, int n) =>
        Truncate(string.Join(" / ", InformativeLines(text).Take(n)), 200);

    private static string Tail(string text, int n)
    {
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        return Truncate(string.Join(" / ", lines.TakeLast(n)), 200);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
