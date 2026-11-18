using Claudinine.Transcript;

namespace Claudinine.Tests;

/// <summary>
/// The live window and orphan healing (1.2.3). A pass must never remove a
/// record the live process may still chain onto — the app parents its next
/// write to its in-memory tip, which is not the last record on disk — and
/// links broken that way by earlier versions are repaired from the mirror.
/// The live writer is simulated by appending after the pass, exactly as the
/// app does seconds later.
/// </summary>
public sealed class LiveWindowTests : IDisposable
{
    private static readonly string Output = "tool output " + new string('o', 2000);
    // p1 a1 | marker p2 a2
    private static readonly bool[] WindowAfterMarker = [false, false, true, true, true];
    // p1 a1 | p2 a2
    private static readonly bool[] WindowAfterPrompt = [false, false, true, true];
    private readonly string _dir;
    private int _appended;

    public LiveWindowTests()
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

    private static JsonObject[] Load(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 0)
            .Select(l => (JsonObject)JsonNode.Parse(l)!).ToArray();

    /// <summary>What the app writes next, parented to its in-memory tip.</summary>
    private string AppendLive(string path, string parentUuid, string type = "assistant")
    {
        string uuid = $"aaaaaaaa-0000-0000-0000-{++_appended:D12}";
        var record = new JsonObject
        {
            ["type"] = type,
            ["uuid"] = uuid,
            ["parentUuid"] = parentUuid,
            ["sessionId"] = "test-session",
            ["message"] = type == "user"
                ? new JsonObject { ["role"] = "user", ["content"] = "next prompt" }
                : new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "reply" }),
                },
        };
        File.AppendAllText(path, record.ToJsonString() + "\n");
        return uuid;
    }

    private static string[] DanglingParents(JsonObject[] records)
    {
        var uuids = records.Select(r => r["uuid"]?.GetValue<string>()).OfType<string>().ToHashSet();
        return records
            .Select(r => r["parentUuid"]?.GetValue<string>())
            .OfType<string>()
            .Where(p => !uuids.Contains(p))
            .ToArray();
    }

    private static string? ParentOf(JsonObject[] records, string uuid) =>
        records.Single(r => r["uuid"]?.GetValue<string>() == uuid)["parentUuid"]?.GetValue<string>();

    private static bool HasUuid(JsonObject[] records, string uuid) =>
        records.Any(r => r["uuid"]?.GetValue<string>() == uuid);

    // ── prevention ──────────────────────────────────────────────────────────

    [Test]
    public async Task MidTurnPass_LeavesTheRunningTurnWhole()
    {
        // The reported failure: a pass mid-turn (queued prompt, auto-compaction)
        // collapsed the running turn's last tool_result; the model's reply landed
        // ~10 s later parented to it, and resume cut the chain there.
        var b = new TranscriptBuilder().UserPrompt("first");
        for (int i = 0; i < 3; i++)
            b.BashRead($"sed -n '1,5p' a{i}.txt", out _, Output + i);
        b.AssistantText("turn one done").StopHookSummary();
        b.UserPrompt("second");
        var running = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            b.BashRead($"sed -n '1,5p' b{i}.txt", out string id, Output + "b" + i);
            running.Add(id);
        }
        string lastResult = b.LastUuid!;
        string path = b.WriteTo(_dir, atRest: false);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        string all = string.Join("\n", records.Select(r => r.ToJsonString()));
        await Assert.That(all).Contains("this turn originally ran 3 separate tool calls");
        foreach (string id in running)
            await Assert.That(all).Contains(id);
        await Assert.That(HasUuid(records, lastResult)).IsTrue();

        AppendLive(path, lastResult);
        await Assert.That(DanglingParents(Load(path))).IsEmpty();
    }

    [Test]
    public async Task TurnEndMarker_SurvivesWhileItIsTheTip()
    {
        // The next prompt parents the turn's stop_hook_summary, which is no
        // longer last on disk once uuid-less metadata trails it.
        var b = new TranscriptBuilder().UserPrompt("first");
        for (int i = 0; i < 3; i++)
            b.BashRead($"sed -n '1,5p' a{i}.txt", out _, Output + i);
        b.AssistantText("done").StopHookSummary();
        string marker = b.LastUuid!;
        b.MetaLine("relocated", ("relocatedCwd", @"C:\proj"))
            .MetaLine("bridge-session", ("bridgeSessionId", "x"));
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(HasUuid(Load(path), marker)).IsTrue();
        AppendLive(path, marker, type: "user");
        await Assert.That(DanglingParents(Load(path))).IsEmpty();
    }

    [Test]
    public async Task OlderMarker_IsStrippedOnceANewerTurnEnds()
    {
        var b = new TranscriptBuilder().UserPrompt("first").AssistantText("one");
        string assistant = b.LastUuid!;
        b.StopHookSummary();
        string oldMarker = b.LastUuid!;
        b.UserPrompt("second");
        string secondPrompt = b.LastUuid!;
        b.AssistantText("two");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(HasUuid(records, oldMarker)).IsFalse();
        await Assert.That(ParentOf(records, secondPrompt)).IsEqualTo(assistant);
        await Assert.That(records[^1]["subtype"]!.GetValue<string>()).IsEqualTo("stop_hook_summary");
    }

    [Test]
    public async Task WindowStartsAtTheLastMarker_OrTheLastPromptWithout()
    {
        var withMarker = new TranscriptBuilder().UserPrompt("p1").AssistantText("a1")
            .StopHookSummary().UserPrompt("p2").AssistantText("a2")
            .WriteTo(_dir, "with-marker.jsonl", atRest: false);
        var records = TranscriptFile.TryLoad(withMarker)!.Records;
        await Assert.That(records.Select(r => r.IsLiveWindow))
            .IsEquivalentTo(WindowAfterMarker);

        var noMarker = new TranscriptBuilder().UserPrompt("p1").AssistantText("a1")
            .UserPrompt("p2").AssistantText("a2")
            .WriteTo(_dir, "no-marker.jsonl", atRest: false);
        records = TranscriptFile.TryLoad(noMarker)!.Records;
        await Assert.That(records.Select(r => r.IsLiveWindow))
            .IsEquivalentTo(WindowAfterPrompt);
    }

    [Test]
    public async Task AgentFiles_HaveNoWindow()
    {
        string path = Fixtures.AgentTranscript(_dir);
        var transcript = TranscriptFile.TryLoad(path)!;
        await Assert.That(transcript.IsSidechainFile).IsTrue();
        await Assert.That(transcript.Records.Any(r => r.IsLiveWindow)).IsFalse();
    }

    [Test]
    public async Task RemovingAWindowRecord_RefusesTheRewrite()
    {
        // Backstop for a rule that ignores IsProtected.
        string path = new TranscriptBuilder().UserPrompt("p1").AssistantText("a1")
            .StopHookSummary().MetaLine("relocated", ("relocatedCwd", @"C:\proj"))
            .WriteTo(_dir);
        string before = File.ReadAllText(path);
        var transcript = TranscriptFile.TryLoad(path)!;
        transcript.Records.Single(r => r.Type == "system").Removed = true;

        await Assert.That(transcript.TryRewrite()).IsFalse();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
    }

    // ── healing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A session compacted before the fix: the first marker was stripped, and
    /// the live writer had parented a record to it afterwards.
    /// </summary>
    private (string Path, string Assistant, string Orphan) DamagedSession(string name)
    {
        var b = new TranscriptBuilder().UserPrompt("first").AssistantText("one");
        string assistant = b.LastUuid!;
        b.StopHookSummary();
        string stripped = b.LastUuid!;
        b.UserPrompt("second").AssistantText("two");
        string path = b.WriteTo(_dir, name);
        Compactor.Run(path); // strips the first marker, mirrors it first
        string orphan = AppendLive(path, stripped);
        return (path, assistant, orphan);
    }

    [Test]
    public async Task OrphanLeftByAnEarlierPass_IsHealedFromTheMirror()
    {
        var (path, assistant, orphan) = DamagedSession("test-session.jsonl");
        await Assert.That(DanglingParents(Load(path))).IsNotEmpty();

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(DanglingParents(records)).IsEmpty();
        await Assert.That(ParentOf(records, orphan)).IsEqualTo(assistant);
    }

    [Test]
    public async Task HealIsIdempotent()
    {
        var (path, _, _) = DamagedSession("test-session.jsonl");
        Compactor.Run(path);
        string afterFirst = File.ReadAllText(path);
        Compactor.Run(path);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(afterFirst);
    }

    [Test]
    public async Task ForeignDanglingParent_IsLeftAsFound()
    {
        // Not in our mirror: someone else's reference (fork copy, crash leftover).
        const string Foreign = "ffffffff-0000-0000-0000-000000000001";
        var b = new TranscriptBuilder().UserPrompt("first").AssistantText("one");
        string path = b.WriteTo(_dir);
        string orphan = AppendLive(path, Foreign);
        // Close the turn so the orphan sits outside the live window.
        File.AppendAllText(path, new JsonObject
        {
            ["type"] = "system", ["subtype"] = "stop_hook_summary",
            ["uuid"] = "bbbbbbbb-0000-0000-0000-000000000001", ["parentUuid"] = orphan,
            ["sessionId"] = "test-session",
        }.ToJsonString() + "\n");

        Compactor.Run(path);

        await Assert.That(ParentOf(Load(path), orphan)).IsEqualTo(Foreign);
    }

    [Test]
    public async Task HealResolvesThroughARecordRemovedInTheSamePass()
    {
        // The healed ancestor can itself be removed by this pass: one walk
        // resolves both (TranscriptFile.SurvivingAncestor).
        var b = new TranscriptBuilder().UserPrompt("first").AssistantText("one");
        string assistant = b.LastUuid!;
        b.HookSuccess("Stop");
        string hook = b.LastUuid!;
        string path = b.WriteTo(_dir);
        const string Missing = "cccccccc-0000-0000-0000-000000000001";
        string orphan = AppendLive(path, Missing);
        File.AppendAllText(path, new JsonObject
        {
            ["type"] = "system", ["subtype"] = "stop_hook_summary",
            ["uuid"] = "bbbbbbbb-0000-0000-0000-000000000002", ["parentUuid"] = orphan,
            ["sessionId"] = "test-session",
        }.ToJsonString() + "\n");

        var transcript = TranscriptFile.TryLoad(path)!;
        transcript.HealedParents = new() { [Missing] = hook };
        transcript.Records.Single(r => r.Uuid == hook).Removed = true;
        await Assert.That(transcript.TryRewrite()).IsTrue();

        await Assert.That(ParentOf(Load(path), orphan)).IsEqualTo(assistant);
    }

    // ── SessionStart sweep ──────────────────────────────────────────────────

    [Test]
    public async Task HealSweep_RepairsADormantSiblingOnce()
    {
        var (sibling, assistant, orphan) = DamagedSession("other-session.jsonl");
        string current = Path.Combine(_dir, "test-session.jsonl");

        HealSweep.Run(current, TimeSpan.FromSeconds(30));

        await Assert.That(ParentOf(Load(sibling), orphan)).IsEqualTo(assistant);
        await Assert.That(File.Exists(HealSweep.MarkerPathFor(sibling))).IsTrue();

        // Marked: a second sweep does not even open it.
        string second = AppendLive(sibling, "dddddddd-0000-0000-0000-000000000001");
        HealSweep.Run(current, TimeSpan.FromSeconds(30));
        await Assert.That(ParentOf(Load(sibling), second))
            .IsEqualTo("dddddddd-0000-0000-0000-000000000001");
    }

    [Test]
    public async Task HealSweep_LeavesFrozenSessionsAlone()
    {
        var (sibling, _, _) = DamagedSession("other-session.jsonl");
        SkipMarkers.Write("other-session", sibling);
        string before = File.ReadAllText(sibling);

        HealSweep.Run(Path.Combine(_dir, "test-session.jsonl"), TimeSpan.FromSeconds(30));

        await Assert.That(File.ReadAllText(sibling)).IsEqualTo(before);
        await Assert.That(File.Exists(HealSweep.MarkerPathFor(sibling))).IsFalse();
    }

    [Test]
    public async Task HealSweep_SkipsItsOwnSessionAndUnmirroredOnes()
    {
        var (own, _, ownOrphan) = DamagedSession("test-session.jsonl");
        string ownBefore = File.ReadAllText(own);
        string unmirrored = new TranscriptBuilder().UserPrompt("never compacted")
            .WriteTo(_dir, "fresh-session.jsonl");

        HealSweep.Run(own, TimeSpan.FromSeconds(30));

        await Assert.That(File.ReadAllText(own)).IsEqualTo(ownBefore);
        await Assert.That(ParentOf(Load(own), ownOrphan)).IsNotNull();
        await Assert.That(File.Exists(HealSweep.MarkerPathFor(own))).IsFalse();
        await Assert.That(File.Exists(HealSweep.MarkerPathFor(unmirrored))).IsFalse();
    }
}
