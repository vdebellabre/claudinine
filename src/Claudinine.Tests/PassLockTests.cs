namespace Claudinine.Tests;

/// <summary>
/// Hooks fire concurrently — parallel agents' SubagentStops land together while
/// the session's own Stop runs — and every pass runs under its transcript's
/// PassLock so two processes can never interleave writes into the same mirror.
/// Busy is not an error: the holder is running the same idempotent pass, so
/// every contended path here must skip cleanly and leave the file untouched.
/// </summary>
public sealed class PassLockTests : IDisposable
{
    private readonly string _dir;

    public PassLockTests()
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
    public async Task SecondAcquireIsRefusedUntilReleased()
    {
        string transcript = Path.Combine(_dir, "abc.jsonl");

        using (var first = PassLock.TryAcquire(transcript))
        {
            await Assert.That(first).IsNotNull();
            await Assert.That(PassLock.TryAcquire(transcript)).IsNull();
        }
        using var afterRelease = PassLock.TryAcquire(transcript);
        await Assert.That(afterRelease).IsNotNull();
    }

    [Test]
    public async Task DifferentStemsDoNotContend()
    {
        using var session = PassLock.TryAcquire(Path.Combine(_dir, "abc.jsonl"));
        using var agent = PassLock.TryAcquire(
            Path.Combine(_dir, "abc", "subagents", "agent-x.jsonl"));
        await Assert.That(session).IsNotNull();
        await Assert.That(agent).IsNotNull();
    }

    [Test]
    public async Task SessionPassSkipsWhileTranscriptIsLocked()
    {
        string path = Fixtures.CompactableTranscript(_dir);
        byte[] before = File.ReadAllBytes(path);

        using var held = PassLock.TryAcquire(path);
        await Assert.That(Run($$"""
            {"hook_event_name":"UserPromptSubmit","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(before);
    }

    [Test]
    public async Task SessionEndWritesTeardownMarkerEvenWhileLocked()
    {
        string path = Fixtures.CompactableTranscript(_dir);

        using var held = PassLock.TryAcquire(path);
        await Assert.That(Run($$"""
            {"hook_event_name":"SessionEnd","transcript_path":"{{path.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        string marker = Path.Combine(
            MirrorLocator.ClaudinineDirFor(path),
            Path.GetFileNameWithoutExtension(path) + ".end");
        await Assert.That(File.Exists(marker)).IsTrue();
    }

    [Test]
    public async Task SubagentStopSkipsWhileAgentFileIsLocked()
    {
        string session = new TranscriptBuilder().UserPrompt("hi").AssistantText("ok").WriteTo(_dir);
        string agent = Fixtures.AgentTranscript(_dir);
        byte[] before = File.ReadAllBytes(agent);

        using var held = PassLock.TryAcquire(agent);
        await Assert.That(Run($$"""
            {"hook_event_name":"SubagentStop","transcript_path":"{{session.Replace("\\", "\\\\")}}","agent_transcript_path":"{{agent.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllBytes(agent)).IsEquivalentTo(before);
    }

    [Test]
    public async Task BoundarySweepSkipsALockedAgentFileButCompactsTheSession()
    {
        string session = Fixtures.CompactableTranscript(_dir);
        string agent = Fixtures.AgentTranscript(_dir);
        byte[] agentBefore = File.ReadAllBytes(agent);

        using var held = PassLock.TryAcquire(agent);
        await Assert.That(Run($$"""
            {"hook_event_name":"SessionEnd","transcript_path":"{{session.Replace("\\", "\\\\")}}"}
            """)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(session)).Contains("[claudinine");
        await Assert.That(File.ReadAllBytes(agent)).IsEquivalentTo(agentBefore);
    }
}
