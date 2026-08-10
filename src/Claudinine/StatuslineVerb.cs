namespace Claudinine;

/// <summary>
/// `claudinine statusline` — the one surface that can SEE the live context.
///
/// Hooks are blind to context size (their envelope carries no token counts) and
/// can only append to the buffer; the statusline receives `context_window` from
/// the most recent API response, so the reclaimable figure stops being an
/// estimate derived from what we removed on disk and becomes a subtraction
/// between two measured quantities.
///
/// It reports; it cannot act. No in-session reload exists (see the plan memory) —
/// the value here is making the invisible visible, so the user reloads when it is
/// worth it instead of being interrupted about it.
///
/// Fail-closed like every other surface, and then some: this runs once per
/// assistant message and its stdout IS the user's status bar, so a throw or a
/// stray byte garbles their terminal every turn. Every failure prints nothing
/// and exits 0.
/// </summary>
internal static class StatuslineVerb
{
    /// <summary>
    /// Below this, the reclaim is not worth a line of the user's status bar —
    /// under ~5% the reload costs more attention than it returns.
    /// </summary>
    private const double InterestingPercent = 5.0;

    /// <summary>
    /// Sub-1k reclaims are noise at status-bar resolution.
    /// </summary>
    private const long InterestingTokens = 1000;

    public static int Run(Stream stdin)
    {
        try
        {
            var input = JsonSerializer.Deserialize(stdin, ClaudinineJsonContext.Default.StatuslineInput);
            if (input?.TranscriptPath is null)
                return 0;

            string? line = Render(input);
            if (line is not null)
            {
                // The default console encoding on Windows is the OEM code page,
                // which renders our separator as mojibake in the status bar —
                // and the bar redraws every turn, so one bad byte is permanent
                // visual noise. Set it only when we actually have output.
                Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                Console.WriteLine(line);
            }
            return 0;
        }
        catch (Exception ex) when (!Dbg.Enabled)
        {
            _ = ex;
            return 0;
        }
    }

    /// <summary>
    /// The status text, or null when there is nothing worth saying. Separated
    /// from <see cref="Run"/> so the interesting logic is testable without a
    /// stdin stream.
    /// </summary>
    internal static string? Render(StatuslineInput input)
    {
        if (Measure(input.TranscriptPath!) is not { } m)
            return null;

        // Percent of what the session WOULD load uncompacted — i.e. of the live
        // buffer, since the buffer holds the uncollapsed conversation. Not a
        // percentage of the file on disk: the file is the compacted side.
        double percent = (double)m.RemovedBytes / m.UncompactedBytes * 100.0;
        if (percent < InterestingPercent)
            return null;

        long? reclaimable = ReclaimableTokens(m, input.ContextWindow);
        return reclaimable is { } tokens
            ? $"claudinine · ~{Humanize(tokens)} reclaimable · {percent:F0}% of context · /exit + resume"
            : $"claudinine · {percent:F0}% reclaimable · /exit + resume";
    }

    /// <summary>
    /// How many context tokens a reload would actually return.
    ///
    /// The naive form — live tokens × saved fraction — overstates it, because a
    /// large part of the window is NOT transcript-derived (system prompt, tool
    /// definitions, skills, memory) and survives a reload untouched. That fixed
    /// floor is not broken out in the statusline payload, so rather than guess at
    /// it we sidestep it: derive a tokens-per-byte rate from the conversation we
    /// can measure, then price the removed bytes at that rate.
    ///
    /// The rate comes from the UNCOMPACTED size, because that is what the live
    /// window holds — compaction is retroactive to disk and never rewrites the
    /// buffer. The rate is observed from THIS session rather than assumed, so it
    /// already reflects how this particular conversation tokenizes.
    ///
    /// Null when there is no usable live measurement: before the first API
    /// response, or when the result would be noise.
    /// </summary>
    private static long? ReclaimableTokens(Measurement m, ContextWindow? window)
    {
        if (window?.TotalInputTokens is not { } liveTokens || liveTokens <= 0)
            return null;

        double tokensPerByte = (double)liveTokens / m.UncompactedBytes;
        long reclaimable = (long)(m.RemovedBytes * tokensPerByte);

        // A reload cannot return more than is in the window.
        if (reclaimable >= liveTokens || reclaimable < InterestingTokens)
            return null;
        return reclaimable;
    }

    /// <summary>What compaction has taken out of the live buffer, in bytes.</summary>
    /// <param name="LiveBytes">What the session loads today (the compacted records).</param>
    /// <param name="RemovedBytes">What collapsing those records took out.</param>
    private readonly record struct Measurement(long LiveBytes, long RemovedBytes)
    {
        /// <summary>What the buffer WOULD hold had we never compacted.</summary>
        public long UncompactedBytes => LiveBytes + RemovedBytes;
    }

    /// <summary>
    /// Bytes compaction removed, measured EXACTLY rather than inferred.
    ///
    /// An earlier version compared file sizes (mirror vs transcript) and divided.
    /// That is wrong in both directions and silently so. The mirror also holds
    /// records the transcript no longer has, and after a `/compact` the app
    /// truncates the transcript at the boundary while the mirror keeps its own
    /// shape — so the two totals are not comparable, and the ratio can report a
    /// large "saving" when almost nothing has been collapsed. Observed on a real
    /// session: the ratio claimed 77%, the true figure was 0.3%.
    ///
    /// Instead, pair each record by uuid. A uuid present in both files whose
    /// mirror copy is larger is a record we collapsed, and the difference is
    /// precisely what left the buffer. Records the transcript dropped entirely
    /// (pre-boundary history) are correctly ignored: they are not in the live
    /// buffer either, so reloading would not return them.
    ///
    /// Null when nothing has been collapsed, or when either file is unreadable.
    /// </summary>
    private static Measurement? Measure(string transcriptPath)
    {
        var transcript = new FileInfo(transcriptPath);
        if (!transcript.Exists || transcript.Length == 0)
            return null;

        string? mirrorPath = FindMirror(transcriptPath);
        if (mirrorPath is null)
            return null;

        Dictionary<string, long> mirrorSizes = RecordSizes(mirrorPath);
        if (mirrorSizes.Count == 0)
            return null;

        long live = 0;
        long removed = 0;
        foreach ((string uuid, long size) in RecordSizesEnumerable(transcriptPath))
        {
            live += size;
            if (mirrorSizes.TryGetValue(uuid, out long original) && original > size)
                removed += original - size;
        }

        return live > 0 && removed > 0 ? new Measurement(live, removed) : null;
    }

    /// <summary>Byte length of every uuid-bearing record in a jsonl file.</summary>
    private static Dictionary<string, long> RecordSizes(string path)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string uuid, long size) in RecordSizesEnumerable(path))
            sizes[uuid] = size;
        return sizes;
    }

    /// <summary>
    /// Streams (uuid, byte-length) per line. Reads line by line rather than
    /// loading the file: transcripts run to megabytes and this executes once per
    /// assistant message, inside a 300ms debounce.
    ///
    /// The uuid is pulled with a targeted scan rather than a full parse — we need
    /// one field out of records that carry entire tool outputs, and parsing them
    /// all would dominate the budget.
    /// </summary>
    private static IEnumerable<(string Uuid, long Size)> RecordSizesEnumerable(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
                continue;
            string? uuid = ExtractUuid(line);
            if (uuid is not null)
                yield return (uuid, Encoding.UTF8.GetByteCount(line));
        }
    }

    /// <summary>
    /// The value of the top-level "uuid" field, or null if absent.
    ///
    /// Deliberately a substring scan, not a JSON parse: these records embed whole
    /// tool outputs, and the transcript is re-read once per assistant message.
    /// A false positive would need the literal `"uuid":"` inside archived content
    /// AND a matching uuid in the other file, which costs an over-count of one
    /// record rather than a wrong verdict.
    /// </summary>
    private static string? ExtractUuid(string line)
    {
        const string Key = "\"uuid\":\"";
        int start = line.IndexOf(Key, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += Key.Length;
        int end = line.IndexOf('"', start);
        return end > start ? line[start..end] : null;
    }

    /// <summary>
    /// This session's mirror. Probes every known mirror directory rather than the
    /// env-derived one: the statusline command is user-configured in settings.json
    /// and does NOT inherit CLAUDE_PLUGIN_DATA, the same trap
    /// <see cref="MirrorLocator.SearchDirectories()"/> documents for `get`.
    /// Cross-context resume can leave a session with a mirror in more than one
    /// dir; the largest is the most complete.
    /// </summary>
    private static string? FindMirror(string transcriptPath)
    {
        string sessionId = Path.GetFileNameWithoutExtension(transcriptPath);
        string? best = null;
        long bestLength = 0;
        foreach (string dir in MirrorLocator.SearchDirectories())
        {
            var candidate = new FileInfo(Path.Combine(dir, sessionId + ".jsonl"));
            if (candidate.Exists && candidate.Length > bestLength)
            {
                best = candidate.FullName;
                bestLength = candidate.Length;
            }
        }
        return best;
    }

    private static string Humanize(long tokens) =>
        tokens >= 1000 ? $"{tokens / 1000.0:F0}k tokens" : $"{tokens} tokens";
}
