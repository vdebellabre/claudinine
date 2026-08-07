using System.Text.Json;
using Claudinine.Mirror;

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
            HookInput? input = JsonSerializer.Deserialize(stdin, ClaudinineJsonContext.Default.HookInput);
            if (input?.TranscriptPath is null || !File.Exists(input.TranscriptPath))
                return 0;

            // Every event runs the same idempotent pass; they differ only in
            // which part of the file still has work in it. UserPromptSubmit is
            // the steady-state workhorse (the turn that just ended), SessionEnd
            // makes the file clean at rest (a resume loads the transcript BEFORE
            // SessionStart hooks run), SessionStart/PreCompact are repair for
            // crashes and missed ends — they pay at the next load.
            switch (input.HookEventName)
            {
                case "UserPromptSubmit":
                case "SessionEnd":
                case "PreCompact":
                    Compactor.Run(input.TranscriptPath);
                    break;

                case "SessionStart":
                    Compactor.Run(input.TranscriptPath);
                    MirrorFile.CollectGarbage();
                    break;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }
}
