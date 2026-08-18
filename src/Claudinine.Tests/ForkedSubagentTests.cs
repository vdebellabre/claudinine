using Claudinine.Transcript;

namespace Claudinine.Tests;

/// <summary>
/// Fork-mode subagent transcripts (CLI 2.1.232+ default, subagent_type "fork")
/// open with a `fork-context-ref` record: a pointer to the inherited parent
/// context, carrying no uuid, no sessionId, no message and no isSidechain — a
/// shape nothing else in a transcript has (docs/forked-subagents-analysis.md).
/// These tests turn the previously accidental tolerance of that record into a
/// designed one: it must not declassify the file, must never be touched by a
/// pass, and must reach the mirror so restore reproduces it.
/// </summary>
public sealed class ForkedSubagentTests : IDisposable
{
    private readonly string _dir;
    // Corpus-sized: see ChainCollapseTests.Output — the economics gate makes the
    // fixture payload size load-bearing.
    private static readonly string Output = "tool output " + new string('o', 2000);

    public ForkedSubagentTests()
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

    /// <summary>A finished fork-mode agent run: head pointer, then a dense turn.</summary>
    private static TranscriptBuilder ForkedAgentSession(int calls = 4)
    {
        var b = new TranscriptBuilder(sidechain: true).ForkContextRef();
        b.UserPrompt("agent task prompt");
        for (int i = 0; i < calls; i++)
        {
            b.BashRead($"sed -n '1,5p' file{i}.txt", out _, Output + i);
            b.AssistantText($"note {i}");
        }
        b.AssistantText("agent final report");
        return b;
    }

    [Test]
    public async Task ForkContextRefHeadKeepsSidechainClassification()
    {
        // The head pointer carries no isSidechain; without its exemption every
        // fork-mode agent file classifies MAIN and agent compaction silently
        // disarms (measured live 2026-08-18 on a real 2.1.232 transcript).
        string path = ForkedAgentSession().WriteTo(_dir, "agent-abc123def456abc12.jsonl");
        await Assert.That(TranscriptFile.TryLoad(path)!.IsSidechainFile).IsTrue();
    }

    [Test]
    public async Task UnflaggedRecordStillClassifiesAsMainDespiteForkHead()
    {
        // The exemption is for the one known type only — mixed content stays
        // MAIN, so main-file guards keep the fail-closed direction.
        string path = ForkedAgentSession().TaskReminder("x", sidechain: false)
            .WriteTo(_dir, "agent-abc123def456abc12.jsonl");
        await Assert.That(TranscriptFile.TryLoad(path)!.IsSidechainFile).IsFalse();
    }

    [Test]
    public async Task ForkedAgentTurnCollapses_HeadRecordSurvivesByteIdentical()
    {
        string path = ForkedAgentSession().WriteTo(_dir, "agent-abc123def456abc12.jsonl");
        string headBefore = File.ReadLines(path).First();

        Compactor.Run(path);

        string text = File.ReadAllText(path);
        await Assert.That(text).Contains(ChainCollapseRule.CarrierPrefix);
        // Digest addressing stays on the FILE STEM, exactly like a plain agent file.
        await Assert.That(text).Contains(" get agent-abc123def456abc12 --ref");
        // The pointer is app metadata: never removed, never rewritten.
        await Assert.That(File.ReadLines(path).First()).IsEqualTo(headBefore);
    }

    [Test]
    public async Task ForkedAgentCollapseIsIdempotent()
    {
        string path = ForkedAgentSession().WriteTo(_dir, "agent-abc123def456abc12.jsonl");
        Compactor.Run(path);
        byte[] once = File.ReadAllBytes(path);
        Compactor.Run(path);
        await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(once);
    }

    [Test]
    public async Task ForkContextRefIsMirrored()
    {
        // Uuid-less lines are mirrored by content hash with multiplicity
        // (MirrorFile.IdentityOf) — the pointer must reach the mirror so a
        // restore rebuilds the file whole, head included.
        string path = ForkedAgentSession()
            .WriteTo(Path.Combine(_dir, "test-session", "subagents"), "agent-abc123def456abc12.jsonl");
        string head = File.ReadLines(path).First();

        Compactor.Run(path);

        string mirror = Path.Combine(_dir, "test-session", "claudinine", "agent-abc123def456abc12.jsonl");
        await Assert.That(File.Exists(mirror)).IsTrue();
        await Assert.That(File.ReadAllText(mirror)).Contains(head);
    }

    [Test]
    public async Task ForkContextRefIsProtected()
    {
        var b = new TranscriptBuilder(sidechain: true).ForkContextRef();
        b.UserPrompt("task").AssistantText("ok");
        string path = b.WriteTo(_dir, "agent-abc123def456abc12.jsonl");
        var transcript = TranscriptFile.TryLoad(path)!;

        await Assert.That(transcript.Records[0].IsProtected()).IsTrue();
    }
}
