namespace Claudinine.Tests;

/// <summary>
/// The record-removal housekeeping family: metadata-keep-last,
/// queue-history-collapse, stop-hook-summary-strip. All run end-to-end through
/// Compactor.Run so mirror-first ordering and rewrite validation are exercised.
/// </summary>
public sealed class HousekeepingTests : IDisposable
{
    private readonly string _dir;

    public HousekeepingTests()
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

    private static int CountType(JsonObject[] records, string type) =>
        records.Count(r => r["type"]?.GetValue<string>() == type);

    [Test]
    public async Task MetadataKeepLast_KeepsOnlyLastOfEachType()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .MetaLine("last-prompt", ("lastPrompt", "first"))
            .MetaLine("custom-title", ("customTitle", "old title"))
            .MetaLine("mode", ("mode", "plan"))
            .AssistantText("working")
            .MetaLine("last-prompt", ("lastPrompt", "second"))
            .MetaLine("mode", ("mode", "normal"))
            .MetaLine("custom-title", ("customTitle", "new title"))
            .MetaLine("last-prompt", ("lastPrompt", "third"))
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(CountType(records, "last-prompt")).IsEqualTo(1);
        await Assert.That(CountType(records, "custom-title")).IsEqualTo(1);
        await Assert.That(CountType(records, "mode")).IsEqualTo(1);
        // The survivor is the LAST occurrence of each.
        await Assert.That(records.Single(r => r["type"]?.GetValue<string>() == "last-prompt")["lastPrompt"]!.GetValue<string>()).IsEqualTo("third");
        await Assert.That(records.Single(r => r["type"]?.GetValue<string>() == "custom-title")["customTitle"]!.GetValue<string>()).IsEqualTo("new title");
        await Assert.That(records.Single(r => r["type"]?.GetValue<string>() == "mode")["mode"]!.GetValue<string>()).IsEqualTo("normal");
    }

    [Test]
    public async Task MetadataKeepLast_SingleOccurrenceAndTailUntouched()
    {
        // The sole last-prompt is also the file's final record: must survive.
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .AssistantText("done")
            .MetaLine("last-prompt", ("lastPrompt", "only"))
            .WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(CountType(records, "last-prompt")).IsEqualTo(1);
        await Assert.That(records[^1]["type"]!.GetValue<string>()).IsEqualTo("last-prompt");
    }

    [Test]
    public async Task QueueHistory_NetEmptyRemovesAllOps()
    {
        string path = new TranscriptBuilder()
            .QueueOp("enqueue", "queued question A")
            .UserPrompt("hello")
            .QueueOp("dequeue")
            .QueueOp("enqueue", "queued question B")
            .QueueOp("remove", "queued question B")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(CountType(records, "queue-operation")).IsEqualTo(0);
        await Assert.That(records.Length).IsEqualTo(2); // user + assistant untouched
    }

    [Test]
    public async Task QueueHistory_PendingEnqueueKeepsEverything()
    {
        string path = new TranscriptBuilder()
            .QueueOp("enqueue", "delivered")
            .UserPrompt("hello")
            .QueueOp("dequeue")
            .QueueOp("enqueue", "still waiting")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(CountType(Load(path), "queue-operation")).IsEqualTo(3);
    }

    [Test]
    public async Task QueueHistory_DequeueOnEmptyFailsClosed()
    {
        // Ops from a session whose enqueue predates this file: not net-provable.
        string path = new TranscriptBuilder()
            .QueueOp("dequeue")
            .UserPrompt("hello")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(CountType(Load(path), "queue-operation")).IsEqualTo(1);
    }

    [Test]
    public async Task QueueHistory_QueuesTrackedPerSession()
    {
        // Balanced per session id even though a global FIFO replay would misalign.
        string path = new TranscriptBuilder()
            .QueueOp("enqueue", "from A", session: "session-a")
            .QueueOp("enqueue", "from B", session: "session-b")
            .UserPrompt("hello")
            .QueueOp("dequeue", session: "session-b")
            .QueueOp("dequeue", session: "session-a")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(CountType(Load(path), "queue-operation")).IsEqualTo(0);
    }

    [Test]
    public async Task QueueHistory_TrailingQueueOpSkipsWholeFile()
    {
        string path = new TranscriptBuilder()
            .QueueOp("enqueue", "msg")
            .UserPrompt("hello")
            .AssistantText("done")
            .QueueOp("dequeue")
            .WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(CountType(Load(path), "queue-operation")).IsEqualTo(2);
    }

    [Test]
    public async Task StopHookSummary_BareSummaryRemovedAndChildRechained()
    {
        var b = new TranscriptBuilder()
            .UserPrompt("hello")
            .AssistantText("turn one done");
        string assistantUuid = b.LastUuid!;
        b.StopHookSummary()
            .UserPrompt("second prompt")
            .AssistantText("turn two done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(CountType(records, "system")).IsEqualTo(0);
        // The second user prompt re-parents to the surviving assistant record.
        JsonObject secondPrompt = records.Single(r =>
            r["message"]?["content"] is JsonValue v
            && v.TryGetValue<string>(out string? s) && s == "second prompt");
        await Assert.That(secondPrompt["parentUuid"]!.GetValue<string>()).IsEqualTo(assistantUuid);
    }

    [Test]
    public async Task StopHookSummary_AnySignalKeepsRecord()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .AssistantText("done")
            .StopHookSummary(additionalContext: ["hook says: check the linter"])
            .StopHookSummary(hasOutput: true)
            .StopHookSummary(errors: ["hook exited 1"])
            .StopHookSummary(preventedContinuation: true)
            .StopHookSummary(stopReason: "blocked")
            .AssistantText("tail")
            .WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(CountType(Load(path), "system")).IsEqualTo(5);
    }

    [Test]
    public async Task StopHookSummary_AtTailKept()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .AssistantText("done")
            .StopHookSummary()
            .WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(CountType(Load(path), "system")).IsEqualTo(1);
    }

    [Test]
    public async Task HousekeepingFamily_IdempotentSecondPass()
    {
        string path = new TranscriptBuilder()
            .QueueOp("enqueue", "q")
            .UserPrompt("hello")
            .QueueOp("dequeue")
            .MetaLine("last-prompt", ("lastPrompt", "one"))
            .AssistantText("mid")
            .StopHookSummary()
            .UserPrompt("again")
            .MetaLine("last-prompt", ("lastPrompt", "two"))
            .MetaLine("custom-title", ("customTitle", "t"))
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);
        byte[] afterFirst = File.ReadAllBytes(path);
        Compactor.Run(path);
        byte[] afterSecond = File.ReadAllBytes(path);

        await Assert.That(afterSecond).IsEquivalentTo(afterFirst);
    }
}
