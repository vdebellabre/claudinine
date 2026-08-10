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
    /// Below this, the reclaim is not worth a line of the user's status bar.
    ///
    /// An absolute token count, deliberately not a percentage: what decides
    /// whether a reload is worth the interruption is how much context comes back,
    /// and that is a token figure. A percentage answers a different question and
    /// scales the wrong way — 5% of a small window is a couple of thousand tokens
    /// (not worth a reload), while a genuinely large reclaim late in a big session
    /// can sit under 5% and stay hidden precisely when it matters most.
    /// </summary>
    private const long InterestingTokens = 50_000;

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

        // The token figure is the ONLY gate: it is what the threshold is
        // expressed in, so a reclaim we cannot price in tokens cannot be judged
        // against it. That means no percent-only fallback — before the first API
        // response there is no `total_input_tokens` and the bar stays silent,
        // rather than showing a figure held to a different (weaker) bar.
        if (ReclaimableTokens(m, input.ContextWindow) is not { } tokens)
            return null;

        // Percent of what the session WOULD load uncompacted — i.e. of the live
        // buffer, since the buffer holds the uncollapsed conversation. Not a
        // percentage of the file on disk: the file is the compacted side.
        // Decoration only: it gives the token figure a sense of scale.
        double percent = (double)m.RemovedBytes / m.UncompactedBytes * 100.0;
        return $"claudinine · ~{Humanize(tokens)} reclaimable · {percent:F0}% of context · /exit + resume";
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
    /// Null when there is no usable live measurement (before the first API
    /// response) or when the reclaim is below <see cref="InterestingTokens"/> —
    /// this is where the status bar's one gate lives.
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

    /// <summary>What a reload would take out of the live buffer, in bytes.</summary>
    /// <param name="LiveBytes">What a reload would load (the transcript's records today).</param>
    /// <param name="RemovedBytes">What the buffer holds beyond that.</param>
    internal readonly record struct Measurement(long LiveBytes, long RemovedBytes)
    {
        /// <summary>What the buffer holds now (loaded + appended, uncollapsed).</summary>
        public long UncompactedBytes => LiveBytes + RemovedBytes;
    }

    /// <summary>
    /// Bytes a reload would return, measured EXACTLY rather than inferred.
    ///
    /// Two earlier versions were wrong in ways users saw. Comparing file sizes
    /// (mirror vs transcript) and dividing reported 77% on a session whose true
    /// figure was 0.3% — the two totals are not comparable. Pairing records by
    /// uuid against the MIRROR fixed that but reported the standing gap, which
    /// SURVIVES a reload: seconds after resuming, the bar still claimed ~48k
    /// reclaimable when the true figure was zero, because the buffer had just
    /// been re-seeded from the compacted file while the mirror kept its fat
    /// originals forever.
    ///
    /// The reference is therefore not the mirror but what the buffer actually
    /// holds, per record: the load stamp — written at every SessionStart, i.e.
    /// at the last load/reload — for records that existed then, the mirror
    /// original for records appended and compacted since, and the record itself
    /// otherwise. A record's reclaim is buffer size minus its transcript size
    /// today; records compacted before the last load contribute zero because the
    /// stamp already saw them small. Records the transcript dropped entirely
    /// (pre-boundary history) are correctly ignored: they are not in the live
    /// buffer either, so reloading would not return them.
    ///
    /// Null when nothing is reclaimable, when either file is unreadable, or when
    /// there is no stamp (session last loaded under a build without the
    /// watermark) — silence over the misleading standing-gap number.
    /// </summary>
    internal static Measurement? Measure(string transcriptPath)
    {
        var transcript = new FileInfo(transcriptPath);
        if (!transcript.Exists || transcript.Length == 0)
            return null;

        Dictionary<string, long>? loaded = LoadStamp.Read(transcriptPath);
        if (loaded is null)
            return null;

        // No mirror ⇒ nothing was ever compacted (mirror-first invariant), and
        // post-load compactions are priced off the mirror's originals.
        string? mirrorPath = FindMirror(transcriptPath);
        if (mirrorPath is null)
            return null;
        var mirrorSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string uuid, long size) in LoadStamp.ScanRecordSizes(mirrorPath))
            mirrorSizes[uuid] = size;

        long live = 0;
        long removed = 0;
        foreach ((string uuid, long size) in LoadStamp.ScanRecordSizes(transcriptPath))
        {
            live += size;
            long buffer = loaded.TryGetValue(uuid, out long atLoad) ? atLoad
                : mirrorSizes.TryGetValue(uuid, out long original) ? original
                : size;
            if (buffer > size)
                removed += buffer - size;
        }

        return live > 0 && removed > 0 ? new Measurement(live, removed) : null;
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
