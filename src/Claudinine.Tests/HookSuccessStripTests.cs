namespace Claudinine.Tests;

/// <summary>
/// hook-success-strip: Stop and PostToolUse hook_success attachments are proven
/// inert on resume (canary 2026-08-07) and removed; SessionStart is proven
/// replayed and any other event is unproven — both kept. Runs end-to-end through
/// Compactor.Run so removal rechaining and rewrite validation are exercised.
/// </summary>
public sealed class HookSuccessStripTests : IDisposable
{
    private readonly string _dir;

    public HookSuccessStripTests()
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

    private static string[] HookEvents(JsonObject[] records) =>
        [.. records
            .Where(r => r["type"]?.GetValue<string>() == "attachment"
                && r["attachment"]?["type"]?.GetValue<string>() == "hook_success")
            .Select(r => r["attachment"]!["hookEvent"]!.GetValue<string>())];

    [Test]
    public async Task InertEventsRemoved_ProvenAndUnprovenKept()
    {
        string path = new TranscriptBuilder()
            .HookSuccess("SessionStart")
            .UserPrompt("hello")
            .HookSuccess("PostToolUse")
            .AssistantText("turn one")
            .HookSuccess("Stop")
            .UserPrompt("more")
            .HookSuccess("PreToolUse")
            .HookSuccess("SomeFutureEvent")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        // Allowlist removal: only the two canary-proven-inert events go.
        await Assert.That(HookEvents(Load(path))).IsEquivalentTo(["SessionStart", "PreToolUse", "SomeFutureEvent"]);
    }

    [Test]
    public async Task RemovedRecord_ChildRechainedToSurvivingAncestor()
    {
        var b = new TranscriptBuilder()
            .UserPrompt("hello")
            .AssistantText("turn one");
        string assistantUuid = b.LastUuid!;
        b.HookSuccess("Stop")
            .UserPrompt("second prompt")
            .AssistantText("done");
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject secondPrompt = Load(path).Single(r =>
            r["message"]?["content"] is JsonValue v
            && v.TryGetValue<string>(out string? s) && s == "second prompt");
        await Assert.That(secondPrompt["parentUuid"]!.GetValue<string>()).IsEqualTo(assistantUuid);
    }

    [Test]
    public async Task TailRecordNeverRemoved()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .AssistantText("done")
            .HookSuccess("Stop")
            .WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(HookEvents(Load(path))).IsEquivalentTo(["Stop"]);
    }

    [Test]
    public async Task SidechainRecordsUntouched()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .HookSuccess("Stop", sidechain: true)
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);

        await Assert.That(HookEvents(Load(path))).IsEquivalentTo(["Stop"]);
    }

    [Test]
    public async Task TailLeafUuidRemapDoesNotAbortThePass()
    {
        // Regression (found 2026-08-07 on the hook_success corpus): files often
        // end in a uuid-less last-prompt record whose leafUuid anchors the final
        // chain record. Removing that anchor forces the rewrite layer to remap
        // the TAIL's leafUuid — the old validation demanded a byte-identical
        // tail unless its parentUuid changed, so the whole pass silently aborted.
        var b = new TranscriptBuilder()
            .UserPrompt("hello")
            .AssistantText("turn one");
        string assistantUuid = b.LastUuid!;
        b.HookSuccess("Stop");
        string hookUuid = b.LastUuid!;
        b.MetaLine("last-prompt", ("leafUuid", hookUuid));
        string path = b.WriteTo(_dir);

        Compactor.Run(path);

        JsonObject[] records = Load(path);
        await Assert.That(HookEvents(records)).IsEmpty();
        JsonObject tail = records[^1];
        await Assert.That(tail["type"]!.GetValue<string>()).IsEqualTo("last-prompt");
        await Assert.That(tail["leafUuid"]!.GetValue<string>()).IsEqualTo(assistantUuid);
    }

    [Test]
    public async Task IdempotentSecondPass()
    {
        string path = new TranscriptBuilder()
            .UserPrompt("hello")
            .HookSuccess("Stop")
            .HookSuccess("PostToolUse")
            .AssistantText("done")
            .WriteTo(_dir);

        Compactor.Run(path);
        byte[] afterFirst = File.ReadAllBytes(path);
        Compactor.Run(path);
        byte[] afterSecond = File.ReadAllBytes(path);

        await Assert.That(afterSecond).IsEquivalentTo(afterFirst);
    }
}
