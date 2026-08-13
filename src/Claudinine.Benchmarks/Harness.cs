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
    /// filesystem — <see cref="TranscriptFile.TryComputeRewrite"/>, the real
    /// compute half of TryRewrite (this used to be a hand-written copy of it;
    /// calling the real thing means it cannot drift). The file half — length
    /// re-check and atomic swap — stays out: the corpus is the fixed, private,
    /// hard-to-rebuild baseline the effectiveness numbers depend on, so no
    /// benchmark may write to it; the run verb copies to a scratch dir when it
    /// wants the real thing.
    ///
    /// Returns the resulting line count, purely so callers have a value the JIT
    /// cannot optimize the work away for.
    /// </summary>
    public static int SerializeAndValidate(TranscriptFile transcript)
    {
        if (!transcript.HasChanges)
            return transcript.Records.Count;
        return transcript.TryComputeRewrite()?.Count ?? -1;
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
