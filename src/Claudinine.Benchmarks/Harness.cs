using Claudinine.Transcript;

namespace Claudinine.Benchmarks;

/// <summary>
/// The measurement core shared by the BenchmarkDotNet suite and the `run` verb,
/// so both exercise the same code path and their numbers stay comparable.
/// </summary>
internal static class Harness
{
    /// <summary>
    /// Parse a transcript from an in-memory string, through the production
    /// parser. <paramref name="path"/> is not read — it is passed through
    /// because rules legitimately depend on it (ForkHealRule derives the current
    /// session id from the filename), so a placeholder would change behavior.
    /// </summary>
    public static TranscriptFile? ParseFromText(string text, string path) =>
        TranscriptFile.TryParseText(text, path, Encoding.UTF8.GetByteCount(text));

    /// <summary>
    /// Serialize the pending rewrite and re-validate it, WITHOUT touching the
    /// filesystem — the compute half of <see cref="TranscriptFile.TryRewrite"/>.
    ///
    /// This deliberately does not call TryRewrite: that method ends in a length
    /// re-check and an atomic file swap, which would mutate the corpus. Since the
    /// corpus is the fixed, private, hard-to-rebuild baseline the effectiveness
    /// numbers depend on, no benchmark may write to it — the run verb copies to
    /// a scratch dir when it wants the real thing.
    ///
    /// Returns the resulting line count, purely so callers have a value the JIT
    /// cannot optimize the work away for.
    /// </summary>
    public static int SerializeAndValidate(TranscriptFile transcript)
    {
        if (!transcript.HasChanges)
            return transcript.Records.Count;

        int kept = 0;
        var sb = new StringBuilder();
        foreach (var rec in transcript.Records)
        {
            if (rec.Removed)
                continue;
            var node = rec.Replacement;
            string line = node is not null
                ? node.ToJsonString(Json.Compact) + (rec.HadCarriageReturn ? "\r" : "")
                : rec.RawLine;
            sb.Append(line).Append('\n');
            kept++;
        }

        // Re-parse the result the way TryRewrite's independent re-validation
        // does. It is a real and non-trivial share of the pass's cost, so
        // omitting it would flatter the rewrite number.
        string rewritten = sb.ToString();
        int reparsed = 0;
        foreach (string line in rewritten.Split('\n'))
        {
            if (line.Length == 0)
                continue;
            if (TranscriptRecord.TryParse(line) is not null)
                reparsed++;
        }
        return kept + reparsed;
    }

    /// <summary>Byte length of the text a full pass would write, for saving stats.</summary>
    public static long RewrittenLength(TranscriptFile transcript)
    {
        long total = 0;
        foreach (var rec in transcript.Records)
        {
            if (rec.Removed)
                continue;
            string line = rec.Replacement is not null
                ? rec.Replacement.ToJsonString(Json.Compact)
                : rec.RawLine;
            total += Encoding.UTF8.GetByteCount(line) + 1;
        }
        return total;
    }
}
