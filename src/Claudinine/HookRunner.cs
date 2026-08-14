namespace Claudinine;

/// <summary>
/// Entry point for all hook invocations. Fail-closed: anything unexpected
/// (unparseable input, unknown event, missing transcript) exits 0 silently —
/// a context optimizer must never break the session it optimizes.
/// </summary>
internal static class HookRunner
{
    public static int Run(Stream stdin)
    {
        try
        {
            var input = JsonSerializer.Deserialize(stdin, ClaudinineJsonContext.Default.HookInput);
            if (input?.TranscriptPath is null)
                return 0;

            // The app seeds its context buffer from the transcript BEFORE
            // SessionStart hooks run, so the file as it stands right now IS what
            // this session holds. Stamp it before the repair pass below mutates
            // anything — the statusline prices a reload against this watermark.
            // Deliberately ahead of the exists-guard: a brand-new session has no
            // transcript yet and must stamp as "loaded nothing".
            if (input.HookEventName == "SessionStart")
                LoadStamp.Write(input.TranscriptPath);

            if (!File.Exists(input.TranscriptPath))
                return 0;

            // A session frozen by `restore-compaction-off` keeps its mirror
            // fresh but is never compacted — an explicit restore must not be
            // silently undone. Global housekeeping (GC) still runs.
            string sid = Path.GetFileNameWithoutExtension(input.TranscriptPath);
            bool skipped = SkipMarkers.IsCompactionSkipped(sid);

            // Every event runs the same idempotent pass; they differ only in
            // which part of the file still has work in it. UserPromptSubmit is
            // the steady-state workhorse (the turn that just ended), SessionEnd
            // makes the file clean at rest (a resume loads the transcript BEFORE
            // SessionStart hooks run), SessionStart/PreCompact are repair for
            // crashes and missed ends — they pay at the next load.
            switch (input.HookEventName)
            {
                case "UserPromptSubmit" or "SessionEnd" or "PreCompact" or "SessionStart":
                    if (skipped) Compactor.MirrorOnly(input.TranscriptPath);
                    else Compactor.Run(input.TranscriptPath);
                    if (input.HookEventName is "SessionEnd" or "SessionStart")
                    {
                        // Subagent transcripts get no hook events of their own, so
                        // they ride the session's boundary events — off the
                        // per-prompt critical path (a first pass over a large
                        // subagents/ dir mirrors tens of MB). SessionEnd leaves
                        // them clean at rest, SessionStart repairs after a crash.
                        CompactSubagents(input.TranscriptPath, skipped);
                    }
                    if (input.HookEventName == "SessionStart")
                    {
                        // Housekeeping rides the once-per-session event, off the
                        // per-prompt critical path.
                        MirrorFile.CollectGarbage();
                        SessionDirGc.Run(input.TranscriptPath, input.SessionId);
                    }
                    break;
            }

            return 0;
        }
        catch (Exception e)
        {
            // Fail-closed exit is non-negotiable; being silent about it under
            // CLAUDININE_DEBUG is not. This is also where a rule exception lands
            // when debug lets it escape Compactor's per-rule filter.
            Dbg.Log($"hook failed: {e}");
            return 0;
        }
    }

    /// <summary>
    /// Run the same idempotent pass over every subagent transcript of this
    /// session (&lt;session-uuid&gt;/subagents/agent-*.jsonl). Each agent file gets
    /// its own stem-keyed mirror, so retrieval, restore, skip markers and mirror
    /// GC all work unchanged. A frozen session freezes its subagents too; an
    /// individually restored agent file carries its own skip marker. Per-file
    /// fail-closed: one unreadable file must not stop the sweep.
    /// </summary>
    private static void CompactSubagents(string transcriptPath, bool sessionSkipped)
    {
        try
        {
            string dir = Path.Combine(
                Path.GetDirectoryName(transcriptPath)!,
                Path.GetFileNameWithoutExtension(transcriptPath),
                "subagents");
            if (!Directory.Exists(dir))
                return;
            foreach (string file in Directory.EnumerateFiles(dir, "agent-*.jsonl"))
            {
                try
                {
                    if (sessionSkipped
                        || SkipMarkers.IsCompactionSkipped(Path.GetFileNameWithoutExtension(file)))
                    {
                        Compactor.MirrorOnly(file);
                    }
                    else
                    {
                        Compactor.Run(file);
                    }
                }
                catch (Exception e)
                {
                    Dbg.Log($"subagent sweep failed on {file}: {e.Message}");
                    // next agent file
                }
            }
        }
        catch (Exception e)
        {
            Dbg.Log($"subagent sweep failed: {e.Message}");
            // no subagents dir we can read: nothing to sweep
        }
    }
}
