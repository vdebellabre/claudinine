using System.Text;
using System.Text.RegularExpressions;

namespace Claudinine.Rules;

/// <summary>One file range a command reads. <c>End == null</c> means "to end of file".</summary>
internal readonly record struct ReadTarget(string Path, int Start, int? End)
{
    /// <summary>
    /// True if this target's range fully contains <paramref name="other"/>'s.
    /// A later read that covers an earlier one makes the earlier result redundant.
    /// Open-ended ranges cover anything starting at or after their start.
    /// </summary>
    public bool Covers(ReadTarget other)
    {
        if (Path != other.Path) return false;
        if (Start > other.Start) return false;
        if (End is null) return true;
        if (other.End is null) return false;
        return End >= other.End;
    }

    public override string ToString() => $"{Path}:{Start}-{End?.ToString() ?? "EOF"}";
}

/// <summary>
/// Parses read-only file-inspection Bash commands into (path, line-range) targets.
/// Port of cozempic's <c>_bashread.py</c>, same contract: targets are returned ONLY
/// when the whole command is provably a pure file read; anything not fully understood
/// yields an empty list ("do not touch"). Fail-closed is the entire safety story —
/// a false positive silently deletes output the model reasoned from.
/// </summary>
internal static partial class BashReadParser
{
    // Commands that only ever READ a file and write to stdout. Anything else
    // (git, grep, pytest, …) produces a *finding*, not a reproducible file slice.
    private static readonly string[] ReadVerbs = ["cat", "head", "tail", "sed"];

    // Redirection, command substitution and backgrounding can have side effects or
    // pull content not attributable to a file — refuse the whole command.
    [GeneratedRegex(@"[>&`]|\$\(|<\(")]
    private static partial Regex UnsafeShell();

    [GeneratedRegex(@"^(\d+),(\d+)p$")]
    private static partial Regex SedRange();

    [GeneratedRegex(@"^(\d+)p$")]
    private static partial Regex SedSingle();

    [GeneratedRegex(@"^-(\d+)$")]
    private static partial Regex DashNumber();

    /// <summary>
    /// Parse a Bash command into the file ranges it reads. Empty unless EVERY
    /// segment of the command is a recognized pure file read.
    /// </summary>
    public static List<ReadTarget> ParseReadTargets(string? cmd)
    {
        var none = new List<ReadTarget>();
        if (string.IsNullOrEmpty(cmd) || UnsafeShell().IsMatch(cmd))
            return none;

        var tokens = ShellTokenizer.TrySplit(cmd);
        if (tokens is null || tokens.Count == 0)
            return none;

        // Split on ';' — sequential commands whose outputs concatenate, so every
        // segment's content really is in the result. Pipes are refused outright
        // (deviation from the cozempic POC, which mis-credited upstream segments):
        // `cat a | cat b` outputs only b, yet segment-parsing would claim it
        // delivered a and wrongly retire an earlier real read of a. `||` has the
        // same only-some-segments-ran problem; `&&` never gets here (the
        // unsafe-shell regex refuses '&').
        var segments = new List<List<string>> { new() };
        foreach (string tok in tokens)
        {
            if (tok is "|" or "||")
                return none;
            if (tok == ";")
                segments.Add([]);
            else
                segments[^1].Add(tok);
        }

        var targets = new List<ReadTarget>();
        bool sawRead = false;
        foreach (var seg in segments)
        {
            if (seg.Count == 0) continue;
            if (IsLiteralEcho(seg))
                continue; // separator noise; its output is reproducible from the command text
            var got = ParseOne(seg);
            if (got is null || got.Count == 0)
                return none; // fail closed: one unrecognized segment poisons the command
            sawRead = true;
            targets.AddRange(got);
        }
        return sawRead ? targets : none;
    }

    /// <summary>
    /// `echo` with purely literal arguments (a common section separator in chained
    /// reads). No flags, no '$' anywhere: variable or arithmetic expansion would
    /// make the output environment-dependent, i.e. not reproducible.
    /// </summary>
    private static bool IsLiteralEcho(List<string> seg) =>
        seg[0] == "echo" && seg.Skip(1).All(a => !a.StartsWith('-') && !a.Contains('$'));

    private static bool LooksLikePath(string tok) => tok.Length > 0 && !tok.StartsWith('-');

    private static List<ReadTarget>? ParseOne(List<string> argv)
    {
        if (argv.Count == 0) return null;
        string verb = argv[0];
        int slash = verb.LastIndexOf('/');
        if (slash >= 0) verb = verb[(slash + 1)..];
        if (!ReadVerbs.Contains(verb)) return null;
        var args = argv[1..];

        switch (verb)
        {
            case "cat":
                {
                    var paths = args.Where(LooksLikePath).ToList();
                    // Any flag (-n, -A, …) changes the output shape; refuse rather than guess.
                    if (paths.Count != args.Count || paths.Count == 0) return null;
                    return [.. paths.Select(p => new ReadTarget(p, 1, null))];
                }

            case "head":
            case "tail":
                {
                    int? n = null;
                    var paths = new List<string>();
                    for (int i = 0; i < args.Count; i++)
                    {
                        string a = args[i];
                        if (a is "-n" or "--lines")
                        {
                            if (i + 1 >= args.Count) return null;
                            if (!int.TryParse(args[i + 1].TrimStart('+'), out int parsed)) return null;
                            n = parsed;
                            i++;
                            continue;
                        }
                        var m = DashNumber().Match(a);
                        if (m.Success)
                        {
                            n = int.Parse(m.Groups[1].Value);
                            continue;
                        }
                        if (!LooksLikePath(a)) return null; // unknown flag
                        paths.Add(a);
                    }
                    if (paths.Count != 1 || n is null) return null;
                    // `tail -n N` is relative to EOF — not resolvable to absolute line
                    // numbers without reading the file, which may have changed since.
                    if (verb == "tail") return null;
                    return [new ReadTarget(paths[0], 1, n)];
                }

            case "sed":
                {
                    // Accept exactly: sed -n <expr> <file>, where <expr> is one or
                    // more ';'-separated print ranges ('10,20p', '42p', '5,9p;30,40p').
                    if (args.Count != 3 || args[0] != "-n") return null;
                    string expr = args[1], path = args[2];
                    if (!LooksLikePath(path)) return null;
                    var targets = new List<ReadTarget>();
                    foreach (string part in expr.Split(';'))
                    {
                        var m = SedRange().Match(part);
                        if (m.Success)
                        {
                            if (!int.TryParse(m.Groups[1].Value, out int a) ||
                                !int.TryParse(m.Groups[2].Value, out int b) || a > b)
                            {
                                return null;
                            }

                            targets.Add(new ReadTarget(path, a, b));
                            continue;
                        }
                        m = SedSingle().Match(part);
                        if (m.Success)
                        {
                            if (!int.TryParse(m.Groups[1].Value, out int a)) return null;
                            targets.Add(new ReadTarget(path, a, a));
                            continue;
                        }
                        return null; // any non-print part poisons the sed
                    }
                    return targets.Count > 0 ? targets : null;
                }
        }
        return null;
    }
}

/// <summary>
/// Minimal POSIX-style tokenizer (the subset of Python's shlex the parser needs):
/// whitespace splitting, single/double quotes, backslash escapes, and the four
/// separators kept as their own tokens. Returns null on anything it cannot fully
/// tokenize (unclosed quote, trailing backslash) — the caller treats that as
/// "not a pure read".
/// </summary>
internal static class ShellTokenizer
{
    public static List<string>? TrySplit(string cmd)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();
        bool hasCur = false;
        int i = 0;

        void Flush()
        {
            if (hasCur) tokens.Add(cur.ToString());
            cur.Clear();
            hasCur = false;
        }

        while (i < cmd.Length)
        {
            char c = cmd[i];

            if (char.IsWhiteSpace(c)) { Flush(); i++; continue; }

            if (c is ';' or '|')
            {
                Flush();
                if (c == '|' && i + 1 < cmd.Length && cmd[i + 1] == '|') { tokens.Add("||"); i += 2; }
                else { tokens.Add(c.ToString()); i++; }
                continue;
            }
            // '&' outside "&&" is rejected upstream by the unsafe-shell regex, and
            // "&&" itself contains '&' — so this tokenizer never sees either.

            if (c == '\'')
            {
                int end = cmd.IndexOf('\'', i + 1);
                if (end < 0) return null; // unclosed quote
                cur.Append(cmd, i + 1, end - i - 1);
                hasCur = true;
                i = end + 1;
                continue;
            }

            if (c == '"')
            {
                i++;
                while (true)
                {
                    if (i >= cmd.Length) return null; // unclosed quote
                    char d = cmd[i];
                    if (d == '"') { i++; break; }
                    if (d == '\\' && i + 1 < cmd.Length && cmd[i + 1] is '"' or '\\' or '$' or '`')
                    {
                        cur.Append(cmd[i + 1]);
                        i += 2;
                        continue;
                    }
                    cur.Append(d);
                    i++;
                }
                hasCur = true;
                continue;
            }

            if (c == '\\')
            {
                if (i + 1 >= cmd.Length) return null; // trailing backslash
                cur.Append(cmd[i + 1]);
                hasCur = true;
                i += 2;
                continue;
            }

            cur.Append(c);
            hasCur = true;
            i++;
        }

        Flush();
        return tokens;
    }
}
