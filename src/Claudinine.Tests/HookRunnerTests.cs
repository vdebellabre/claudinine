namespace Claudinine.Tests;

/// <summary>
/// The layer's core safety promise: a hook must NEVER break the session it runs
/// in — anything unexpected exits 0 silently and leaves the transcript alone.
/// SessionStart is deliberately not exercised end-to-end here: its housekeeping
/// sweeps real per-user directories that tests must not touch.
/// </summary>
public sealed class HookRunnerTests : IDisposable
{
    private readonly string _dir;

    public HookRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "claudinine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", Path.Combine(_dir, "plugin-data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_PLUGIN_DATA", null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static int Run(string stdin) =>
        HookRunner.Run(new MemoryStream(Encoding.UTF8.GetBytes(stdin)));

    [Test]
    public async Task GarbageStdinExitsZero() =>
        await Assert.That(Run("this is not json at all {")).IsEqualTo(0);

    [Test]
    public async Task EmptyStdinExitsZero() =>
        await Assert.That(Run("")).IsEqualTo(0);

    [Test]
    public async Task MissingTranscriptPathExitsZero() =>
        await Assert.That(Run("""{"hook_event_name":"UserPromptSubmit"}""")).IsEqualTo(0);

    [Test]
    public async Task NonexistentTranscriptExitsZero() =>
        await Assert.That(Run($$"""
            {"hook_event_name":"UserPromptSubmit","transcript_path":"{{Path.Combine(_dir, "gone.jsonl").Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

    [Test]
    public async Task UnknownEventLeavesTranscriptUntouched()
    {
        string path = new TranscriptBuilder().UserPrompt("hello").AssistantText("hi").WriteTo(_dir);
        byte[] before = File.ReadAllBytes(path);

        await Assert.That(Run($$"""
            {"hook_event_name":"SomeFutureEvent","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(before);
    }

    [Test]
    public async Task UserPromptSubmitCompactsAndExitsZero()
    {
        string path = CompactableTranscript();

        await Assert.That(Run($$"""
            {"hook_event_name":"UserPromptSubmit","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(path)).Contains("[claudinine");
    }

    // Stop is the autonomous-stretch trigger (scheduled tasks, /loop, Workflow
    // runs chain turns with no prompt between them): it compacts like
    // UserPromptSubmit, but under a min-interval guard so an interactive
    // session's per-prompt pass is not doubled at every turn end.

    [Test]
    public async Task StopCompactsWhenNoPassRanRecently()
    {
        string path = CompactableTranscript();

        await Assert.That(Run($$"""
            {"hook_event_name":"Stop","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(path)).Contains("[claudinine");
    }

    [Test]
    public async Task StopSkipsWhenPassIsFresh()
    {
        string path = CompactableTranscript();
        PassStamp.Touch(path);
        byte[] before = File.ReadAllBytes(path);

        await Assert.That(Run($$"""
            {"hook_event_name":"Stop","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(before);
    }

    [Test]
    public async Task StopRunsWhenStampIsStale()
    {
        string path = CompactableTranscript();
        PassStamp.Touch(path);
        string stamp = Path.Combine(
            MirrorLocator.ClaudinineDirFor(path),
            Path.GetFileNameWithoutExtension(path) + ".pass");
        File.SetLastWriteTimeUtc(stamp, DateTime.UtcNow - TimeSpan.FromMinutes(10));

        await Assert.That(Run($$"""
            {"hook_event_name":"Stop","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(path)).Contains("[claudinine");
    }

    [Test]
    public async Task UserPromptSubmitIgnoresFreshStamp()
    {
        string path = CompactableTranscript();
        PassStamp.Touch(path);

        await Assert.That(Run($$"""
            {"hook_event_name":"UserPromptSubmit","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(path)).Contains("[claudinine");
    }

    [Test]
    public async Task CompletedPassTouchesStamp()
    {
        string path = CompactableTranscript();

        await Assert.That(Run($$"""
            {"hook_event_name":"UserPromptSubmit","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(PassStamp.IsFresh(path, TimeSpan.FromSeconds(120))).IsTrue();
    }

    // SubagentStop compacts the one file the event names, on the spot, and
    // never touches the live session transcript it fired under.

    [Test]
    public async Task SubagentStopCompactsTheNamedAgentFile()
    {
        string session = new TranscriptBuilder().UserPrompt("hi").AssistantText("ok").WriteTo(_dir);
        string agent = AgentTranscript();
        byte[] sessionBefore = File.ReadAllBytes(session);

        await Assert.That(Run($$"""
            {"hook_event_name":"SubagentStop","transcript_path":"{{session.Replace("\\", "\\\\")}}","agent_transcript_path":"{{agent.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(agent)).Contains("[claudinine");
        await Assert.That(File.ReadAllBytes(session)).IsEquivalentTo(sessionBefore);
    }

    [Test]
    public async Task SubagentStopWithoutAgentPathExitsZero()
    {
        string session = new TranscriptBuilder().UserPrompt("hi").AssistantText("ok").WriteTo(_dir);

        await Assert.That(Run($$"""
            {"hook_event_name":"SubagentStop","transcript_path":"{{session.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);
    }

    [Test]
    public async Task SubagentStopOnFrozenSessionMirrorsWithoutCompacting()
    {
        string session = new TranscriptBuilder().UserPrompt("hi").AssistantText("ok").WriteTo(_dir);
        string agent = AgentTranscript();
        string colocated = MirrorLocator.ClaudinineDirFor(session);
        Directory.CreateDirectory(colocated);
        File.WriteAllText(Path.Combine(colocated, "test-session.skip"), "frozen\n");
        byte[] agentBefore = File.ReadAllBytes(agent);

        await Assert.That(Run($$"""
            {"hook_event_name":"SubagentStop","transcript_path":"{{session.Replace("\\", "\\\\")}}","agent_transcript_path":"{{agent.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllBytes(agent)).IsEquivalentTo(agentBefore);
        await Assert.That(File.Exists(MirrorLocator.PathFor(agent))).IsTrue();
    }

    private string CompactableTranscript()
    {
        var b = new TranscriptBuilder();
        for (int i = 0; i < 8; i++)
        {
            b.UserPrompt($"look ({i})");
            b.BashRead("sed -n '1,100p' src/foo.cs", out _, new string('x', 500));
        }
        b.AssistantText("done");
        return b.WriteTo(_dir);
    }

    /// <summary>A finished agent run under the session's subagents/ dir —
    /// corpus-sized outputs, so the collapse economics gate fires.</summary>
    private string AgentTranscript()
    {
        var b = new TranscriptBuilder(sidechain: true).UserPrompt("agent task");
        for (int i = 0; i < 4; i++)
        {
            b.BashRead($"sed -n '1,5p' file{i}.txt", out _, "tool output " + new string('o', 2000));
            b.AssistantText($"note {i}");
        }
        b.AssistantText("agent final report");
        return b.WriteTo(
            Path.Combine(_dir, "test-session", "subagents"),
            "agent-abc123def456abc12.jsonl");
    }
}
