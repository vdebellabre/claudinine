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

            // Every event runs the same idempotent pass; they differ only in
            // which part of the file still has work in it. UserPromptSubmit is
            // the steady-state workhorse (the turn that just ended), Stop covers
            // autonomous stretches (scheduled tasks, /loop, Workflow runs chain
            // turns with no prompt between them) under a min-interval guard,
            // SessionEnd makes the file clean at rest (a resume loads the
            // transcript BEFORE SessionStart hooks run), SessionStart/PreCompact
            // are repair for crashes and missed ends — they pay at the next load.
            //
            // Hooks fire CONCURRENTLY (parallel agents' SubagentStops land
            // together, and Stop can overlap them all), so every pass runs under
            // its transcript's PassLock; busy means another hook is doing this
            // exact idempotent work right now, and skipping is free.
            switch (input.HookEventName)
            {
                case "UserPromptSubmit" or "Stop" or "SessionEnd" or "PreCompact" or "SessionStart":
                    // Before the pass and OUTSIDE the lock — the teardown must
                    // be recorded even if the pass below fails or is skipped as
                    // busy: hosts that keep sessions server-side (Cowork cloud)
                    // tear down on idle — SessionEnd fires — and later re-hydrate
                    // into a new process WITHOUT firing SessionStart (measured
                    // 2026-08-15). The marker turns the first event after such a
                    // teardown into this session's start boundary below — a
                    // prompt, or a turn end when the wake was autonomous and no
                    // prompt ever arrives.
                    if (input.HookEventName == "SessionEnd")
                        EndMarker.Write(input.TranscriptPath);

                    using (var held = PassLock.TryAcquire(input.TranscriptPath))
                    {
                        if (held is null)
                            return 0;

                        // The app seeds its context buffer from the transcript
                        // BEFORE SessionStart hooks run, so the file as it
                        // stands right now IS what this session holds. Stamp it
                        // before the repair pass below mutates anything — the
                        // statusline prices a reload against this watermark.
                        // Deliberately ahead of the exists-guard: a brand-new
                        // session has no transcript yet and must stamp as
                        // "loaded nothing".
                        if (input.HookEventName == "SessionStart")
                            LoadStamp.Write(input.TranscriptPath);

                        if (!File.Exists(input.TranscriptPath))
                            return 0;

                        // A session frozen by `restore-compaction-off` keeps its
                        // mirror fresh but is never compacted — an explicit
                        // restore must not be silently undone. Global
                        // housekeeping (GC) still runs.
                        bool skipped = SkipMarkers.IsCompactionSkipped(input.TranscriptPath);

                        // Inside the lock, so two racing boundaries can never
                        // both consume the one marker and double-run the work.
                        bool wake = input.HookEventName is "UserPromptSubmit" or "Stop"
                            && EndMarker.Consume(input.TranscriptPath);

                        // Stop fires at every turn end, right after the pass a
                        // per-turn UserPromptSubmit just ran — the stamp throttles
                        // it to stretches where nothing else compacts. A wake
                        // bypasses the guard: start-boundary work is never skipped.
                        if (input.HookEventName == "Stop" && !wake
                            && PassStamp.IsFresh(input.TranscriptPath, TimeSpan.FromSeconds(120)))
                        {
                            return 0;
                        }

                        if (skipped) Compactor.MirrorOnly(input.TranscriptPath);
                        else Compactor.Run(input.TranscriptPath);
                        PassStamp.Touch(input.TranscriptPath);
                        if (input.HookEventName is "SessionEnd" or "SessionStart" || wake)
                        {
                            // Subagent transcripts get no hook events of their own, so
                            // they ride the session's boundary events — off the
                            // per-prompt critical path (a first pass over a large
                            // subagents/ dir mirrors tens of MB). SessionEnd leaves
                            // them clean at rest, SessionStart repairs after a crash.
                            CompactSubagents(input.TranscriptPath, skipped);
                        }
                        if (input.HookEventName == "SessionEnd")
                        {
                            // The file at rest after this pass is byte-for-byte
                            // what the next re-hydration will load (Cowork resumes
                            // from the transcript with no SessionStart), so stamp
                            // it here rather than at the wake: a Stop wake fires
                            // after a whole autonomous turn was appended, and
                            // stamping then would overstate what was loaded.
                            LoadStamp.Write(input.TranscriptPath);
                        }
                        if (input.HookEventName == "SessionStart" || wake)
                        {
                            // Housekeeping rides the start boundary, off the
                            // per-prompt critical path. The colocated sweep covers
                            // this session's own claudinine dir (orphaned subagent
                            // mirrors and markers); other sessions' dirs are their
                            // own hooks' business, or SessionDirGc's when they die.
                            // NOTE: this transcript's PassLock does NOT protect the
                            // other trees CollectGarbage and SessionDirGc touch —
                            // those sessions' hooks hold only their own locks.
                            // Tolerable because both act only on long-dead targets
                            // (transcript gone, plus 24 h of quiet for SessionDirGc),
                            // never a live pass's working set.
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
                    }
                    break;

                case "SubagentStop":
                    // The one file the event names, on the spot: subagent
                    // transcripts are the best-compacting file type, and the
                    // boundary sweeps alone leave a Workflow's agent files fat
                    // for the whole session. Fires when the agent has finished
                    // writing, so mid-turn is safe — only the agent's own file
                    // is touched, never the live session transcript. The
                    // boundary sweeps still run: they are the repair path for
                    // agents whose SubagentStop was missed, and idempotence
                    // makes the overlap free. Locked on the AGENT's stem, so
                    // parallel agents never contend with each other, only with
                    // a boundary sweep touching this same file.
                    if (input.AgentTranscriptPath is not null
                        && File.Exists(input.AgentTranscriptPath))
                    {
                        using var agentHeld = PassLock.TryAcquire(input.AgentTranscriptPath);
                        if (agentHeld is null)
                            return 0;
                        if (SkipMarkers.IsCompactionSkipped(input.TranscriptPath)
                            || SkipMarkers.IsCompactionSkipped(input.AgentTranscriptPath))
                        {
                            Compactor.MirrorOnly(input.AgentTranscriptPath);
                        }
                        else
                        {
                            Compactor.Run(input.AgentTranscriptPath);
                        }
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
                    // Busy means this file's own SubagentStop is compacting it
                    // right now — the sweep is the repair path, not the owner.
                    using var held = PassLock.TryAcquire(file);
                    if (held is null)
                        continue;
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
