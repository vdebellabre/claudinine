namespace Claudinine.Rules;

/// <summary>
/// Unified-diff context collapsing, ported VERBATIM from cozempic standard.py
/// (post-audit version). The gate requires the unified-diff ENVELOPE (a
/// `--- `/`+++ ` file-header pair or a `diff ` command line) AND a real hunk
/// header — a lone coincidental `@@ … @@` line in non-diff output (git-log
/// fragment, CI text, indented config block) must never trigger collapse. And
/// even past the gate, context lines are collapsed ONLY while inside a hunk, so
/// indented non-diff content trailing a real diff is kept verbatim (the audit P1:
/// in_hunk was set but never reset).
/// </summary>
internal static partial class DiffCollapse
{
    // A real unified-diff hunk header, anchored at line start: "@@ -12,7 +12,9 @@".
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+\d+(?:,\d+)? @@", RegexOptions.Multiline)]
    private static partial Regex UnifiedHunk();

    // The file-header pair every unified diff carries: a "--- …" line immediately
    // followed by a "+++ …" line.
    [GeneratedRegex(@"^--- .*\n\+\+\+ ", RegexOptions.Multiline)]
    private static partial Regex DiffFileHeader();

    public static bool LooksLikeUnifiedDiff(string content)
    {
        if (!UnifiedHunk().IsMatch(content))
            return false;
        if (DiffFileHeader().IsMatch(content))
            return true;
        return content.StartsWith("diff ", StringComparison.Ordinal)
            || content.Contains("\ndiff --git ", StringComparison.Ordinal)
            || content.Contains("\ndiff ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Strip unchanged context lines from unified diffs, keep +/- and headers.
    /// Returns the input unchanged when collapsing would not shrink it.
    /// </summary>
    public static string CollapseContext(string diffText)
    {
        string[] lines = diffText.Split('\n');
        var result = new List<string>(lines.Length);
        int contextRun = 0;
        bool inHunk = false;

        void Flush()
        {
            if (contextRun > 0)
            {
                result.Add($"  [...{contextRun} unchanged lines...]");
                contextRun = 0;
            }
        }

        foreach (string line in lines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                inHunk = true;
                result.Add(line);
            }
            else if (line.StartsWith("diff ", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal)
                || line.StartsWith("+++", StringComparison.Ordinal)
                || line.StartsWith('+') || line.StartsWith('-'))
            {
                Flush();
                result.Add(line);
            }
            else if (inHunk && line.StartsWith(' '))
            {
                contextRun++;
            }
            else
            {
                // A non-context line: no longer inside a hunk's body. Reset inHunk
                // so indented content AFTER the hunk (a git-log -p second commit's
                // message body, trailing prose) is kept verbatim, never collapsed.
                inHunk = false;
                Flush();
                result.Add(line);
            }
        }

        Flush();
        string collapsed = string.Join('\n', result);
        return collapsed.Length < diffText.Length ? collapsed : diffText;
    }
}
