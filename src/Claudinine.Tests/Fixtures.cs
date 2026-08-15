namespace Claudinine.Tests;

/// <summary>Transcript fixtures shared by the hook-layer test classes.</summary>
internal static class Fixtures
{
    /// <summary>A session with enough archivable tool output that a pass
    /// visibly compacts it ("[claudinine" appears).</summary>
    public static string CompactableTranscript(string dir)
    {
        var b = new TranscriptBuilder();
        for (int i = 0; i < 8; i++)
        {
            b.UserPrompt($"look ({i})");
            b.BashRead("sed -n '1,100p' src/foo.cs", out _, new string('x', 500));
        }
        b.AssistantText("done");
        return b.WriteTo(dir);
    }

    /// <summary>A finished agent run under the session's subagents/ dir —
    /// corpus-sized outputs, so the collapse economics gate fires.</summary>
    public static string AgentTranscript(string dir)
    {
        var b = new TranscriptBuilder(sidechain: true).UserPrompt("agent task");
        for (int i = 0; i < 4; i++)
        {
            b.BashRead($"sed -n '1,5p' file{i}.txt", out _, "tool output " + new string('o', 2000));
            b.AssistantText($"note {i}");
        }
        b.AssistantText("agent final report");
        return b.WriteTo(
            Path.Combine(dir, "test-session", "subagents"),
            "agent-abc123def456abc12.jsonl");
    }
}
