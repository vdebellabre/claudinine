using Claudinine.Transcript;

namespace Claudinine.Tests;

/// <summary>
/// Subagent transcripts (agent-*.jsonl under the session dir's subagents/) carry
/// isSidechain: true on EVERY record — the sidechain guards written for main
/// transcripts must switch off there, and digest addressing must use the file
/// stem (the mirror key) instead of the records' sessionId (the PARENT's).
/// The hook layer sweeps them on the session's boundary events.
/// </summary>
public sealed class SubagentTranscriptTests : IDisposable
{
    private readonly string _dir;
    private static readonly string Output = "tool output " + new string('o', 400);

    public SubagentTranscriptTests()
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

    /// <summary>A finished agent run: one dense multi-call turn, prose tail.</summary>
    private static TranscriptBuilder AgentSession(int calls = 4)
    {
        var b = new TranscriptBuilder(sidechain: true).UserPrompt("agent task prompt");
        for (int i = 0; i < calls; i++)
        {
            b.BashRead($"sed -n '1,5p' file{i}.txt", out _, Output + i);
            b.AssistantText($"note {i}");
        }
        b.AssistantText("agent final report");
        return b;
    }

    [Test]
    public async Task AllSidechainRecordsClassifyAsSidechainFile()
    {
        string path = AgentSession().WriteTo(_dir, "agent-abc123def456abc12.jsonl");
        await Assert.That(TranscriptFile.TryLoad(path)!.IsSidechainFile).IsTrue();
    }

    [Test]
    public async Task MainTranscriptIsNotASidechainFile()
    {
        string path = new TranscriptBuilder().UserPrompt("hi").AssistantText("ok").WriteTo(_dir);
        await Assert.That(TranscriptFile.TryLoad(path)!.IsSidechainFile).IsFalse();
    }

    [Test]
    public async Task OneUnflaggedRecordClassifiesAsMain()
    {
        // Fail-closed direction: mixed content means MAIN, so main-file guards stay armed.
        string path = AgentSession().TaskReminder("x", sidechain: false)
            .WriteTo(_dir, "agent-abc123def456abc12.jsonl");
        await Assert.That(TranscriptFile.TryLoad(path)!.IsSidechainFile).IsFalse();
    }

    [Test]
    public async Task SubagentTurnCollapses_HeaderAddressesFileStem()
    {
        string path = AgentSession().WriteTo(_dir, "agent-abc123def456abc12.jsonl");
        int linesBefore = File.ReadAllLines(path).Length;

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        await Assert.That(File.ReadAllLines(path).Length < linesBefore).IsTrue();
        await Assert.That(text).Contains(ChainCollapseRule.CarrierPrefix);
        // Retrieval must go through the agent-file mirror, not the parent session's.
        await Assert.That(text).Contains("claudinine get agent-abc123def456abc12 --ref");
        await Assert.That(text).DoesNotContain("claudinine get test-session");
    }

    [Test]
    public async Task SubagentCollapseIsIdempotent()
    {
        string path = AgentSession().WriteTo(_dir, "agent-abc123def456abc12.jsonl");
        Compactor.Run(path);
        byte[] once = File.ReadAllBytes(path);
        Compactor.Run(path);
        await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(once);
    }

    [Test]
    public async Task SidechainRecordInsideMainTurnStillAbortsCollapse()
    {
        // The guard this feature scoped must stay armed in MAIN transcripts:
        // sidechain material spliced into a normal turn is foreign matter.
        string path = new TranscriptBuilder()
            .UserPrompt("investigate")
            .BashRead("sed -n '1,5p' a.txt", out _, Output)
            .TaskReminder("side note", sidechain: true)
            .BashRead("sed -n '1,5p' b.txt", out _, Output)
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(File.ReadAllText(path)).DoesNotContain(ChainCollapseRule.CarrierPrefix);
    }

    [Test]
    public async Task AnchorInputStubAddressesFileStem()
    {
        var b = new TranscriptBuilder(sidechain: true).UserPrompt("agent task");
        b.ToolCall("Write", new JsonObject
        {
            ["file_path"] = "x.cs",
            ["content"] = new string('w', 600),
        }, "ok");
        b.BashRead("sed -n '1,5p' f.txt", out _, Output);
        b.AssistantText("done");
        string path = b.WriteTo(_dir, "agent-abc123def456abc12.jsonl");

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        await Assert.That(text).Contains("input archived at collapse");
        await Assert.That(text).Contains("claudinine get agent-abc123def456abc12 --ref");
    }

    [Test]
    public async Task SessionEndSweepsSubagentFiles()
    {
        string mainPath = new TranscriptBuilder().UserPrompt("main work").AssistantText("ok")
            .WriteTo(_dir);
        string agentPath = AgentSession()
            .WriteTo(Path.Combine(_dir, "test-session", "subagents"), "agent-abc123def456abc12.jsonl");

        await Assert.That(Run("SessionEnd", mainPath)).IsEqualTo(0);

        await Assert.That(File.ReadAllText(agentPath)).Contains(ChainCollapseRule.CarrierPrefix);
        await Assert.That(File.Exists(Path.Combine(
            _dir, "plugin-data", "mirrors", "agent-abc123def456abc12.jsonl"))).IsTrue();
    }

    [Test]
    public async Task UserPromptSubmitDoesNotSweepSubagents()
    {
        // The per-prompt event stays main-only: a first pass over a large
        // subagents/ dir belongs on the boundary events.
        string mainPath = new TranscriptBuilder().UserPrompt("main work").AssistantText("ok")
            .WriteTo(_dir);
        string agentPath = AgentSession()
            .WriteTo(Path.Combine(_dir, "test-session", "subagents"), "agent-abc123def456abc12.jsonl");
        byte[] before = File.ReadAllBytes(agentPath);

        await Assert.That(Run("UserPromptSubmit", mainPath)).IsEqualTo(0);

        await Assert.That(File.ReadAllBytes(agentPath)).IsEquivalentTo(before);
    }

    [Test]
    public async Task FrozenSessionFreezesItsSubagents()
    {
        string mainPath = new TranscriptBuilder().UserPrompt("main work").AssistantText("ok")
            .WriteTo(_dir);
        string agentPath = AgentSession()
            .WriteTo(Path.Combine(_dir, "test-session", "subagents"), "agent-abc123def456abc12.jsonl");
        SkipMarkers.Write("test-session", mainPath);
        byte[] before = File.ReadAllBytes(agentPath);

        await Assert.That(Run("SessionEnd", mainPath)).IsEqualTo(0);

        // Frozen: never compacted, but the mirror stays fresh (crash protection).
        await Assert.That(File.ReadAllBytes(agentPath)).IsEquivalentTo(before);
        await Assert.That(File.Exists(Path.Combine(
            _dir, "plugin-data", "mirrors", "agent-abc123def456abc12.jsonl"))).IsTrue();
    }

    private static int Run(string hookEvent, string transcriptPath) =>
        HookRunner.Run(new MemoryStream(Encoding.UTF8.GetBytes($$"""
            {"hook_event_name":"{{hookEvent}}","transcript_path":"{{transcriptPath.Replace("\\", "\\\\")}}"}
            """)));
}
