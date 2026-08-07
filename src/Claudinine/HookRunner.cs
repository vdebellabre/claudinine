using System.Text.Json;

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

            switch (input.HookEventName)
            {
                case "UserPromptSubmit":
                    // v1 workhorse: mirror-then-compact the turn(s) since the
                    // previous real user message. Wired next.
                    break;

                case "SessionEnd":
                    // Compact the session's final turn so the file is clean at
                    // rest (a resume loads the transcript before SessionStart
                    // hooks run, so this is what makes the next load lean).
                    break;

                case "SessionStart":
                case "PreCompact":
                    // Full scan + repair: crash leftovers, missed SessionEnd,
                    // mirror GC. Pays off at the next transcript load.
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
