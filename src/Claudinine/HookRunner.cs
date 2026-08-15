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
            bool skipped = SkipMarkers.IsCompactionSkipped(input.TranscriptPath);

            // Every event runs the same idempotent pass; they differ only in
            // which part of the file still has work in it. UserPromptSubmit is
            // the steady-state workhorse (the turn that just ended), SessionEnd
            // makes the file clean at rest (a resume loads the transcript BEFORE
            // SessionStart hooks run), SessionStart/PreCompact are repair for
            // crashes and missed ends — they pay at the next load.
            switch (input.HookEventName)
            {
                case "UserPromptSubmit" or "SessionEnd" or "PreCompact" or "SessionStart":
                    // Before the pass, so the teardown is recorded even if the
                    // pass below fails: hosts that keep sessions server-side
                    // (Cowork cloud) tear down on idle — SessionEnd fires — and
                    // later re-hydrate into a new process WITHOUT firing
                    // SessionStart (measured 2026-08-15). The marker turns the
                    // first prompt after such a teardown into this session's
                    // start boundary below.
                    if (input.HookEventName == "SessionEnd")
                        EndMarker.Write(input.TranscriptPath);

                    bool wake = input.HookEventName == "UserPromptSubmit"
                        && EndMarker.Consume(input.TranscriptPath);

                    // The re-hydration loaded the file as it stands right now:
                    // re-stamp before the pass mutates anything — the same
                    // invariant as the SessionStart stamp above.
                    if (wake)
                        LoadStamp.Write(input.TranscriptPath);

                    if (skipped) Compactor.MirrorOnly(input.TranscriptPath);
                    else Compactor.Run(input.TranscriptPath);
                    if (input.HookEventName is "SessionEnd" or "SessionStart" || wake)
                    {
                        // Subagent transcripts get no hook events of their own, so
                        // they ride the session's boundary events — off the
                        // per-prompt critical path (a first pass over a large
                        // subagents/ dir mirrors tens of MB). SessionEnd leaves
                        // them clean at rest, SessionStart repairs after a crash.
                        CompactSubagents(input.TranscriptPath, skipped);
                    }
                    if (input.HookEventName == "SessionStart" || wake)
                    {
                        // Housekeeping rides the start boundary, off the
                        // per-prompt critical path. The colocated sweep covers
                        // this session's own claudinine dir (orphaned subagent
                        // mirrors and markers); other sessions' dirs are their
                        // own hooks' business, or SessionDirGc's when they die.
                        MirrorFile.CollectGarbage();
                        MirrorFile.CollectGarbageColocated(
                            MirrorLocator.ClaudinineDirFor(input.TranscriptPath));
                        SessionDirGc.Run(input.TranscriptPath, input.SessionId);
                        // A real SessionStart clears any pending teardown marker,
                        // so a CLI resume never replays the start work on its
                        // first prompt.
                        if (input.HookEventName == "SessionStart")
                            EndMarker.Consume(input.TranscriptPath);
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
                    if (sessionSkipped || SkipMarkers.IsCompactionSkipped(file))
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
